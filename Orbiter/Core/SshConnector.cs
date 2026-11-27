using System.Windows;
using System.Windows.Threading;
using Orbiter.Ui;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Orbiter.Core;

public static class SshConnector
{
    private static readonly object HostKeyGate = new();

    /// <summary>
    /// Résout les identifiants (demande à l'utilisateur si besoin) et construit la ConnectionInfo.
    /// Doit être appelé sur le thread UI. Renvoie null si l'utilisateur annule.
    /// </summary>
    public static ConnectionInfo? Prepare(SessionProfile p, Window? owner)
    {
        var methods = new List<AuthenticationMethod>();

        if (p.Auth == AuthKind.PrivateKey)
        {
            if (string.IsNullOrWhiteSpace(p.KeyPath) || !File.Exists(p.KeyPath))
                throw new FileNotFoundException($"Clé privée introuvable : {p.KeyPath}");

            var pass = p.RuntimePassphrase ?? (p.IgnoreStoredSecrets ? null : Secret.Unprotect(p.ProtectedPassphrase));
            PrivateKeyFile? key = null;
            var retryMsg = $"La clé {Path.GetFileName(p.KeyPath)} est protégée.\nPassphrase :";
            while (key == null)
            {
                try
                {
                    key = string.IsNullOrEmpty(pass) ? new PrivateKeyFile(p.KeyPath) : new PrivateKeyFile(p.KeyPath, pass);
                }
                catch (SshPassPhraseNullOrEmptyException)
                {
                    pass = PromptWindow.Ask(owner, "Passphrase de la clé", retryMsg, password: true);
                    if (pass == null) return null;
                }
                catch (Exception) when (!string.IsNullOrEmpty(pass))
                {
                    pass = PromptWindow.Ask(owner, "Passphrase de la clé",
                        $"Passphrase incorrecte pour {Path.GetFileName(p.KeyPath)}.\nPassphrase :", password: true);
                    if (pass == null) return null;
                }
            }
            p.RuntimePassphrase = pass;
            methods.Add(new PrivateKeyAuthenticationMethod(p.Username, key));
        }
        else
        {
            if (p.IsTransient)
            {
                // Connexion rapide : on essaie aussi les clés par défaut non chiffrées de ~/.ssh
                var keys = DefaultKeys().ToArray();
                if (keys.Length > 0) methods.Add(new PrivateKeyAuthenticationMethod(p.Username, keys));
            }

            var pwd = p.RuntimePassword ?? (p.IgnoreStoredSecrets ? null : Secret.Unprotect(p.ProtectedPassword));
            if (pwd == null)
            {
                pwd = PromptWindow.Ask(owner, "Mot de passe", $"Mot de passe pour {p.Username}@{p.Host} :", password: true);
                if (pwd == null) return null;
            }
            p.RuntimePassword = pwd;

            methods.Add(new PasswordAuthenticationMethod(p.Username, pwd));
            var kbd = new KeyboardInteractiveAuthenticationMethod(p.Username);
            kbd.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts) prompt.Response = pwd;
            };
            methods.Add(kbd);
        }

        return new ConnectionInfo(p.Host, p.Port, p.Username, methods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
    }

    private static IEnumerable<IPrivateKeySource> DefaultKeys()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        foreach (var name in new[] { "id_ed25519", "id_ecdsa", "id_rsa" })
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) continue;
            PrivateKeyFile? k = null;
            try { k = new PrivateKeyFile(path); } catch { /* chiffrée ou format non supporté */ }
            if (k != null) yield return k;
        }
    }

    /// <summary>Vérifie l'empreinte du serveur, demande confirmation si inconnue ou modifiée.</summary>
    public static void AttachHostKeyCheck(BaseClient client, string host, int port, Dispatcher ui)
    {
        client.HostKeyReceived += (_, e) => e.CanTrust = VerifyHostKey(host, port, e.HostKeyName, e.FingerPrintSHA256, ui);
    }

    private static bool VerifyHostKey(string host, int port, string alg, string fp, Dispatcher ui)
    {
        lock (HostKeyGate)
        {
            var known = KnownHosts.Get(host, port);
            if (known != null && known.FingerprintSha256 == fp) return true;
            if (KnownHosts.IsTrustedForSession(host, port, fp)) return true;

            return ui.Invoke(() =>
            {
                var owner = Application.Current.MainWindow;
                var r = known == null
                    ? HostKeyDialog.AskNew(owner, host, port, alg, fp)
                    : HostKeyDialog.AskChanged(owner, host, port, known.Algorithm, known.FingerprintSha256, alg, fp);

                switch (r)
                {
                    case "save":
                        KnownHosts.Save(host, port, alg, fp);
                        return true;
                    case "once":
                        KnownHosts.TrustForSession(host, port, fp);
                        return true;
                    default:
                        return false;
                }
            });
        }
    }

    public static string Describe(Exception ex) => ex switch
    {
        SshAuthenticationException => "Authentification refusée (identifiant, mot de passe ou clé incorrects).",
        SshConnectionException { Message: var m } when m.Contains("host key", StringComparison.OrdinalIgnoreCase)
            => "Connexion refusée : empreinte du serveur non approuvée.",
        System.Net.Sockets.SocketException se => $"Impossible de joindre le serveur ({se.SocketErrorCode}) : {se.Message}",
        SshOperationTimeoutException => "Délai de connexion dépassé.",
        AggregateException { InnerException: { } inner } => Describe(inner),
        _ => ex.Message,
    };
}
