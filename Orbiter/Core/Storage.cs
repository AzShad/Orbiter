using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orbiter.Core;

public static class AppPaths
{
    public static string Roaming { get; } = MigrateFromNovaTerm(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Orbiter"));
    public static string Local { get; } = Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orbiter"));
    public static string WebRoot => Ensure(Path.Combine(Local, "web"));
    public static string WebViewData => Ensure(Path.Combine(Local, "WebView2"));
    public static string TempEdit => Ensure(Path.Combine(Path.GetTempPath(), "Orbiter"));

    public static string Ensure(string dir) { Directory.CreateDirectory(dir); return dir; }

    /// <summary>Reprend les sessions de l'ancien nom de l'application (NovaTerm) au premier lancement.</summary>
    private static string MigrateFromNovaTerm(string dir)
    {
        var old = Path.Combine(Path.GetDirectoryName(dir)!, "NovaTerm");
        if (!Directory.Exists(dir) && Directory.Exists(old))
        {
            Directory.CreateDirectory(dir);
            foreach (var name in new[] { "sessions.json", "known_hosts.json", "settings.json" })
            {
                var src = Path.Combine(old, name);
                try { if (File.Exists(src)) File.Copy(src, Path.Combine(dir, name)); } catch { }
            }
        }
        return Ensure(dir);
    }
}

/// <summary>Chiffrement lié au compte Windows (DPAPI).</summary>
public static class Secret
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NovaTerm.v1");

    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    public static string? Unprotect(string? b64)
    {
        if (string.IsNullOrEmpty(b64)) return null;
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(b64), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch { return null; }
    }
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T Load<T>(string path) where T : new()
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T();
        }
        catch { /* fichier corrompu : on repart à vide, une sauvegarde est conservée */
            try { File.Copy(path, path + ".bak", true); } catch { }
        }
        return new T();
    }

    public static void Save<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, true);
    }
}

public class SessionStore
{
    private static readonly string FilePath = Path.Combine(AppPaths.Roaming, "sessions.json");
    public List<SessionProfile> Sessions { get; private set; } = new();

    public static SessionStore Load() => new() { Sessions = Json.Load<List<SessionProfile>>(FilePath) };
    public void Save() => Json.Save(FilePath, Sessions);
}

public record KnownHostEntry(string Algorithm, string FingerprintSha256);

/// <summary>Empreintes des serveurs connus (équivalent known_hosts).</summary>
public static class KnownHosts
{
    private static readonly string FilePath = Path.Combine(AppPaths.Roaming, "known_hosts.json");
    private static readonly object Gate = new();
    private static Dictionary<string, KnownHostEntry>? _entries;
    private static readonly HashSet<string> SessionTrusted = new();

    private static Dictionary<string, KnownHostEntry> Entries =>
        _entries ??= Json.Load<Dictionary<string, KnownHostEntry>>(FilePath);

    public static string Key(string host, int port) => $"[{host.ToLowerInvariant()}]:{port}";

    public static KnownHostEntry? Get(string host, int port)
    {
        lock (Gate) return Entries.TryGetValue(Key(host, port), out var e) ? e : null;
    }

    public static bool IsTrustedForSession(string host, int port, string fp)
    {
        lock (Gate) return SessionTrusted.Contains(Key(host, port) + "|" + fp);
    }

    public static void TrustForSession(string host, int port, string fp)
    {
        lock (Gate) SessionTrusted.Add(Key(host, port) + "|" + fp);
    }

    /// <summary>Copie de toutes les empreintes connues (pour la sauvegarde).</summary>
    public static Dictionary<string, KnownHostEntry> All()
    {
        lock (Gate) return new Dictionary<string, KnownHostEntry>(Entries);
    }

    /// <summary>Ajoute les empreintes absentes (celles déjà connues ne changent pas) ; renvoie le nombre ajouté.</summary>
    public static int MergeMissing(Dictionary<string, KnownHostEntry>? entries)
    {
        if (entries == null) return 0;
        lock (Gate)
        {
            var n = 0;
            foreach (var (k, v) in entries)
                if (Entries.TryAdd(k, v)) n++;
            if (n > 0) Json.Save(FilePath, Entries);
            return n;
        }
    }

    public static void Save(string host, int port, string alg, string fp)
    {
        lock (Gate)
        {
            Entries[Key(host, port)] = new KnownHostEntry(alg, fp);
            Json.Save(FilePath, Entries);
        }
    }
}
