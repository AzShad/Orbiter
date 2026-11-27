using System.Text.Json.Serialization;

namespace Orbiter.Core;

public enum SessionKind { Ssh, Sftp }
public enum AuthKind { Password, PrivateKey }
public enum TunnelKind { Local, Remote, Dynamic }

public class TunnelDef
{
    public TunnelKind Kind { get; set; } = TunnelKind.Local;
    public string BindHost { get; set; } = "127.0.0.1";
    public uint BindPort { get; set; }
    public string DestHost { get; set; } = "";
    public uint DestPort { get; set; }

    [JsonIgnore]
    public string Display => Kind switch
    {
        TunnelKind.Local => $"L  {BindHost}:{BindPort}  →  {DestHost}:{DestPort}",
        TunnelKind.Remote => $"R  (serveur) {BindHost}:{BindPort}  →  {DestHost}:{DestPort}",
        _ => $"D  SOCKS {BindHost}:{BindPort}",
    };

    public override string ToString() => Display;
}

public class SessionProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public SessionKind Kind { get; set; } = SessionKind.Ssh;
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";
    public AuthKind Auth { get; set; } = AuthKind.Password;

    /// <summary>Mot de passe chiffré DPAPI (base64), vide si non mémorisé.</summary>
    public string? ProtectedPassword { get; set; }
    public string? KeyPath { get; set; }
    /// <summary>Passphrase de la clé chiffrée DPAPI (base64).</summary>
    public string? ProtectedPassphrase { get; set; }

    public bool AutoSftpPanel { get; set; } = true;
    public string? InitialRemoteDir { get; set; }
    public List<TunnelDef> Tunnels { get; set; } = new();

    [JsonIgnore] public bool IsTransient { get; set; }
    /// <summary>Mot de passe en mémoire seulement (saisi à la connexion).</summary>
    [JsonIgnore] public string? RuntimePassword { get; set; }
    [JsonIgnore] public string? RuntimePassphrase { get; set; }
    /// <summary>Après un échec d'authentification : redemander au lieu d'utiliser le secret mémorisé.</summary>
    [JsonIgnore] public bool IgnoreStoredSecrets { get; set; }

    [JsonIgnore] public string Endpoint => $"{Username}@{Host}" + (Port != 22 ? $":{Port}" : "");
    [JsonIgnore] public string KindGlyph => Kind == SessionKind.Ssh ? "" : "";
    [JsonIgnore] public string KindLabel => Kind == SessionKind.Ssh ? "SSH" : "SFTP";

    public SessionProfile Clone()
    {
        var c = (SessionProfile)MemberwiseClone();
        c.Tunnels = Tunnels.Select(t => new TunnelDef
        {
            Kind = t.Kind, BindHost = t.BindHost, BindPort = t.BindPort, DestHost = t.DestHost, DestPort = t.DestPort
        }).ToList();
        return c;
    }
}
