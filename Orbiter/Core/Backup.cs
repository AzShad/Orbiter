using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Orbiter.Core;

/// <summary>
/// Sauvegarde portable des sessions, chiffrée par un mot de passe (indépendante du compte Windows, contrairement à DPAPI).
/// Fichier .orbiter = enveloppe JSON : PBKDF2-HMAC-SHA256 (600 000 itérations, sel de 16 octets) -> clé AES-256-GCM.
/// Les paramètres de l'enveloppe sont authentifiés (données associées) : toute modification fait échouer le déchiffrement.
/// </summary>
public static class Backup
{
    public const string Extension = ".orbiter";
    private const string Format = "orbiter-backup";
    private const int FormatVersion = 1;
    private const string Kdf = "PBKDF2-SHA256";
    private const int DefaultIterations = 600_000;
    public const int MinPasswordLength = 10;

    public sealed class Envelope
    {
        public string Format { get; set; } = "";
        public int Version { get; set; }
        public string Kdf { get; set; } = "";
        public int Iterations { get; set; }
        public string Salt { get; set; } = "";
        public string Nonce { get; set; } = "";
        public string Tag { get; set; } = "";
        public string Data { get; set; } = "";
    }

    public sealed class BackupSession
    {
        public SessionProfile Profile { get; set; } = new();
        public string? Password { get; set; }
        public string? Passphrase { get; set; }
        public string? KeyFileName { get; set; }
        public string? KeyContent { get; set; }
    }

    public sealed class Payload
    {
        public string AppVersion { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
        public string Machine { get; set; } = "";
        public List<BackupSession> Sessions { get; set; } = new();
        public Dictionary<string, KnownHostEntry> KnownHosts { get; set; } = new();
    }

    public sealed record ImportResult(int Added, int Replaced, int HostsAdded, int KeysRestored, int MissingKeys);

    public sealed class WrongPasswordException() : Exception("Mot de passe incorrect, ou fichier de sauvegarde modifié / endommagé.");

    // ---------- Contenu ----------

    /// <summary>Rassemble les sessions avec leurs secrets en clair et le contenu des clés privées.</summary>
    public static Payload Collect(IEnumerable<SessionProfile> sessions)
    {
        var p = new Payload
        {
            AppVersion = Updater.Current.ToString(3),
            CreatedUtc = DateTime.UtcNow,
            Machine = Environment.MachineName,
            KnownHosts = Core.KnownHosts.All(),
        };
        foreach (var s in sessions)
        {
            var profile = s.Clone();
            profile.ProtectedPassword = null;
            profile.ProtectedPassphrase = null;
            var b = new BackupSession
            {
                Profile = profile,
                Password = Secret.Unprotect(s.ProtectedPassword),
                Passphrase = Secret.Unprotect(s.ProtectedPassphrase),
            };
            if (!string.IsNullOrWhiteSpace(s.KeyPath) && File.Exists(s.KeyPath))
            {
                try
                {
                    b.KeyContent = Convert.ToBase64String(File.ReadAllBytes(s.KeyPath));
                    b.KeyFileName = Path.GetFileName(s.KeyPath);
                }
                catch { /* clé illisible : la session est quand même sauvegardée */ }
            }
            p.Sessions.Add(b);
        }
        return p;
    }

    /// <summary>
    /// Fusionne une sauvegarde : une session de même identifiant est remplacée, les autres sont ajoutées.
    /// Les secrets sont rechiffrés avec DPAPI ; une clé absente du disque est recréée dans %APPDATA%\Orbiter\keys.
    /// Les empreintes déjà connues ne sont pas modifiées.
    /// </summary>
    public static ImportResult Apply(Payload payload, List<SessionProfile> target)
    {
        int added = 0, replaced = 0, keys = 0, missing = 0;
        foreach (var b in payload.Sessions)
        {
            var p = b.Profile;
            if (string.IsNullOrWhiteSpace(p.Id)) p.Id = Guid.NewGuid().ToString("N");
            p.ProtectedPassword = Secret.Protect(b.Password);
            p.ProtectedPassphrase = Secret.Protect(b.Passphrase);
            p.Tunnels ??= new();

            if (b.KeyContent != null)
            {
                var bytes = Convert.FromBase64String(b.KeyContent);
                if (!(p.KeyPath != null && File.Exists(p.KeyPath) && SameContent(p.KeyPath, bytes)))
                {
                    p.KeyPath = RestoreKey(b.KeyFileName ?? "cle", bytes, out var written);
                    if (written) keys++;
                }
            }
            else if (p.Auth == AuthKind.PrivateKey && (p.KeyPath == null || !File.Exists(p.KeyPath)))
                missing++;

            var idx = target.FindIndex(s => s.Id == p.Id);
            if (idx >= 0) { target[idx] = p; replaced++; }
            else { target.Add(p); added++; }
        }
        var hosts = Core.KnownHosts.MergeMissing(payload.KnownHosts);
        return new ImportResult(added, replaced, hosts, keys, missing);
    }

    private static bool SameContent(string path, byte[] bytes)
    {
        try { return File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes); } catch { return false; }
    }

    private static string RestoreKey(string fileName, byte[] bytes, out bool written)
    {
        written = false;
        var dir = AppPaths.Ensure(Path.Combine(AppPaths.Roaming, "keys"));
        var safe = string.Concat(Path.GetFileName(fileName).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (safe.Length == 0) safe = "cle";
        var path = Path.Combine(dir, safe);
        for (var n = 2; File.Exists(path) && !SameContent(path, bytes); n++)
            path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(safe)}-{n}{Path.GetExtension(safe)}");
        if (!File.Exists(path)) { File.WriteAllBytes(path, bytes); written = true; }
        return path;
    }

    // ---------- Chiffrement ----------

    private static byte[] Aad(Envelope e) => Encoding.UTF8.GetBytes($"{e.Format}|{e.Version}|{e.Kdf}|{e.Iterations}|{e.Salt}");

    public static byte[] Encrypt(Payload payload, string password, int iterations = DefaultIterations)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(payload, Json.Options);
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
        var env = new Envelope
        {
            Format = Format, Version = FormatVersion, Kdf = Kdf, Iterations = iterations,
            Salt = Convert.ToBase64String(salt), Nonce = Convert.ToBase64String(nonce),
        };
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plain, cipher, tag, Aad(env));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
        env.Tag = Convert.ToBase64String(tag);
        env.Data = Convert.ToBase64String(cipher);
        return JsonSerializer.SerializeToUtf8Bytes(env, Json.Options);
    }

    public static Payload Decrypt(byte[] file, string password)
    {
        Envelope env;
        try { env = JsonSerializer.Deserialize<Envelope>(file, Json.Options) ?? throw new InvalidDataException(); }
        catch (JsonException) { throw new InvalidDataException("Ce fichier n'est pas une sauvegarde Orbiter."); }
        if (env.Format != Format) throw new InvalidDataException("Ce fichier n'est pas une sauvegarde Orbiter.");
        if (env.Version > FormatVersion) throw new InvalidDataException("Cette sauvegarde vient d'une version plus récente d'Orbiter : mettez Orbiter à jour.");
        if (env.Kdf != Kdf || env.Iterations is < 100_000 or > 10_000_000) throw new InvalidDataException("Paramètres de chiffrement non reconnus.");

        byte[] salt, nonce, tag, cipher;
        try
        {
            salt = Convert.FromBase64String(env.Salt);
            nonce = Convert.FromBase64String(env.Nonce);
            tag = Convert.FromBase64String(env.Tag);
            cipher = Convert.FromBase64String(env.Data);
        }
        catch (FormatException) { throw new InvalidDataException("Fichier de sauvegarde endommagé."); }
        if (nonce.Length != 12 || tag.Length != 16) throw new InvalidDataException("Fichier de sauvegarde endommagé.");

        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, env.Iterations, HashAlgorithmName.SHA256, 32);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, cipher, tag, plain, Aad(env));
            return JsonSerializer.Deserialize<Payload>(plain, Json.Options) ?? throw new InvalidDataException("Sauvegarde vide.");
        }
        catch (AuthenticationTagMismatchException) { throw new WrongPasswordException(); }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }
}
