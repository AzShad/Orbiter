using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Orbiter.Core;

/// <summary>Une version publiée sur GitHub plus récente que celle qui tourne.</summary>
public sealed record UpdateInfo(Version Version, string Notes, string PageUrl, string? AssetUrl, string? AssetName, long AssetSize, string? Sha256);

/// <summary>Recherche, téléchargement et installation des nouvelles versions (Releases GitHub publiques).</summary>
public static class Updater
{
    public const string Repo = "AzShad/Orbiter";
    public static string ReleasesPage => $"https://github.com/{Repo}/releases/latest";

    public static Version Current { get; } = Normalize(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0));

    private static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Orbiter.exe");
    private static string ExeDir => Path.GetDirectoryName(ExePath)!;

    /// <summary>Installé par l'installeur (désinstalleur Inno Setup à côté de l'exe) ou version portable.</summary>
    public static bool IsInstalled => File.Exists(Path.Combine(ExeDir, "unins000.exe"));

    private static HttpClient NewClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Orbiter/{Current.ToString(3)}");
        return http;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>Renvoie la dernière version si elle est plus récente, sinon null. Lève une exception en cas d'erreur réseau.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var http = NewClient(TimeSpan.FromSeconds(15));
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
        latest = Normalize(latest);
        if (latest <= Current) return null;

        var prefix = IsInstalled ? "Orbiter-Setup-" : "Orbiter-Portable-";
        string? url = null, name = null, sha = null;
        long size = 0;
        if (root.TryGetProperty("assets", out var assets))
            foreach (var a in assets.EnumerateArray())
            {
                var n = a.GetProperty("name").GetString() ?? "";
                if (!n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                name = n;
                url = a.GetProperty("browser_download_url").GetString();
                size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                if (a.TryGetProperty("digest", out var d) && d.GetString() is { } dg && dg.StartsWith("sha256:"))
                    sha = dg["sha256:".Length..];
                break;
            }

        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage;
        return new UpdateInfo(latest, CleanNotes(notes), page, url, name, size, sha);
    }

    /// <summary>Notes Markdown du CHANGELOG -> texte simple lisible dans une boîte de dialogue.</summary>
    private static string CleanNotes(string md) =>
        string.Join("\n", md.Replace("\r", "").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Select(l => (l.StartsWith("- ") || l.StartsWith("* ") ? "•  " + l[2..] : l).Replace("**", "").Replace("`", "")));

    /// <summary>Télécharge le fichier de la nouvelle version ; progress reçoit 0..1. Vérifie l'empreinte SHA-256 si GitHub la fournit.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double> progress, CancellationToken ct)
    {
        if (info.AssetUrl == null || info.AssetName == null) throw new InvalidOperationException("Aucun fichier à télécharger pour cette version.");

        // portable : à côté de l'exe actuel (il le remplacera) ; installeur : dossier temporaire
        var dir = IsInstalled ? AppPaths.Ensure(Path.Combine(Path.GetTempPath(), "Orbiter", "update")) : ExeDir;
        var target = Path.Combine(dir, info.AssetName);
        if (string.Equals(target, ExePath, StringComparison.OrdinalIgnoreCase)) target += ".new";
        var part = target + ".part";

        using var http = NewClient(TimeSpan.FromMinutes(30));
        using var resp = await http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? info.AssetSize;
        try
        {
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buf = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0) progress.Report((double)done / total);
                }
            }

            if (info.Sha256 != null)
            {
                await using var fs = File.OpenRead(part);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
                if (!hash.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Le fichier téléchargé est corrompu (empreinte SHA-256 différente).");
            }
            File.Move(part, target, true);
            return target;
        }
        catch
        {
            try { File.Delete(part); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Lance l'installation de la version téléchargée. L'appelant doit ensuite quitter l'application.
    /// Installeur : installation silencieuse qui relance Orbiter. Portable : le nouvel exe remplace l'ancien.
    /// </summary>
    public static void Apply(string downloaded)
    {
        if (IsInstalled)
        {
            var progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var scope = ExeDir.StartsWith(progFiles, StringComparison.OrdinalIgnoreCase) ? "/ALLUSERS" : "/CURRENTUSER";
            Process.Start(new ProcessStartInfo(downloaded, $"/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS {scope}") { UseShellExecute = true });
            return;
        }

        var current = ExePath;
        var name = Path.GetFileName(current);
        string launch, old;
        if (name.StartsWith("Orbiter-Portable-", StringComparison.OrdinalIgnoreCase) && !downloaded.EndsWith(".new"))
        {
            // nom avec numéro de version : on garde le nouveau nom et on supprime l'ancien fichier
            launch = downloaded;
            old = current;
        }
        else
        {
            // nom choisi par l'utilisateur (ex. Orbiter.exe) : remplacement sur place
            old = current + ".old";
            File.Move(current, old, true); // possible même si l'exe tourne
            File.Move(downloaded, current, true);
            launch = current;
        }
        var psi = new ProcessStartInfo(launch) { UseShellExecute = false, WorkingDirectory = ExeDir };
        psi.ArgumentList.Add("--cleanup-old");
        psi.ArgumentList.Add(old);
        Process.Start(psi);
    }

    /// <summary>Au démarrage après une mise à jour portable : supprime l'ancien exe dès qu'il est libéré.</summary>
    public static void CleanupOld(string[] args)
    {
        var i = Array.IndexOf(args, "--cleanup-old");
        if (i < 0 || i + 1 >= args.Length) return;
        var old = args[i + 1];
        if (!old.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !old.EndsWith(".exe.old", StringComparison.OrdinalIgnoreCase)) return;
        if (string.Equals(Path.GetFullPath(old), Path.GetFullPath(ExePath), StringComparison.OrdinalIgnoreCase)) return;
        Task.Run(async () =>
        {
            for (var n = 0; n < 40 && File.Exists(old); n++)
            {
                try { File.Delete(old); } catch { await Task.Delay(500); }
            }
        });
    }

    /// <summary>Supprime les installeurs téléchargés lors d'une mise à jour précédente.</summary>
    public static void CleanupDownloads()
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "Orbiter", "update");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch { }
    }
}
