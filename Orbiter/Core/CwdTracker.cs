using System.Text;

namespace Orbiter.Core;

/// <summary>
/// Repère dans la sortie du terminal la séquence OSC 7 (« ESC ] 7 ; file://hôte/chemin BEL » ou terminée par « ESC \ »)
/// par laquelle le shell indique son dossier courant. Les séquences coupées entre deux lectures sont reconstituées.
/// </summary>
public sealed class Osc7Scanner
{
    private static readonly byte[] Start = { 0x1B, (byte)']', (byte)'7', (byte)';' };
    private const int MaxSequence = 4096;
    private byte[] _carry = Array.Empty<byte>();

    /// <summary>Analyse un bloc de sortie ; renvoie le dernier dossier annoncé dans ce bloc, ou null.</summary>
    public string? Feed(ReadOnlySpan<byte> chunk)
    {
        ReadOnlySpan<byte> data;
        byte[]? joined = null;
        if (_carry.Length > 0)
        {
            joined = new byte[_carry.Length + chunk.Length];
            _carry.CopyTo(joined, 0);
            chunk.CopyTo(joined.AsSpan(_carry.Length));
            data = joined;
        }
        else data = chunk;
        _carry = Array.Empty<byte>();

        string? found = null;
        var pos = 0;
        while (pos < data.Length)
        {
            var rel = data[pos..].IndexOf((byte)0x1B);
            if (rel < 0) break;
            var i = pos + rel;
            var rest = data[i..];
            if (rest.Length < Start.Length)
            {
                // début possible d'une séquence coupée : on garde la fin pour la lecture suivante
                if (Start.AsSpan(0, rest.Length).SequenceEqual(rest)) _carry = rest.ToArray();
                break;
            }
            if (!rest[..Start.Length].SequenceEqual(Start)) { pos = i + 1; continue; }

            var body = rest[Start.Length..];
            var end = -1;
            var termLen = 0;
            for (var k = 0; k < body.Length && k < MaxSequence; k++)
            {
                if (body[k] == 0x07) { end = k; termLen = 1; break; }
                if (body[k] == 0x1B && k + 1 < body.Length && body[k + 1] == (byte)'\\') { end = k; termLen = 2; break; }
            }
            if (end < 0)
            {
                if (body.Length >= MaxSequence) { pos = i + 1; continue; } // séquence anormalement longue : ignorée
                _carry = rest.ToArray(); // séquence incomplète, complétée à la lecture suivante
                break;
            }
            if (Parse(body[..end]) is { } path) found = path;
            pos = i + Start.Length + end + termLen;
        }
        return found;
    }

    /// <summary>« file://hôte/chemin » -> « /chemin » (décodage des %XX en UTF-8).</summary>
    public static string? Parse(ReadOnlySpan<byte> uri)
    {
        var s = Encoding.UTF8.GetString(uri);
        if (!s.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return null;
        var slash = s.IndexOf('/', "file://".Length);
        if (slash < 0) return null;
        var path = s[slash..];
        if (path.Contains('%'))
        {
            try { path = Uri.UnescapeDataString(path); } catch { }
        }
        return path.Length > 0 && !path.Contains('\0') ? path : null;
    }
}

/// <summary>Script installé sur le serveur (à la demande) pour que le shell annonce son dossier courant.</summary>
public static class CwdHook
{
    public const string FileName = ".orbiter_cwd.sh";
    public const string RcLine = "[ -f ~/.orbiter_cwd.sh ] && . ~/.orbiter_cwd.sh";

    public const string Script =
        "# Orbiter : indique le dossier courant du terminal (séquence OSC 7, invisible à l'écran)\n" +
        "# pour que le panneau SFTP d'Orbiter puisse le suivre. Supprimez ce fichier pour désactiver.\n" +
        "__orbiter_cwd() { printf '\\033]7;file://%s%s\\033\\\\' \"${HOSTNAME:-$HOST}\" \"$PWD\"; }\n" +
        "if [ -n \"$BASH_VERSION\" ]; then\n" +
        "  case \";$PROMPT_COMMAND;\" in\n" +
        "    *\";__orbiter_cwd;\"*) ;;\n" +
        "    *) PROMPT_COMMAND=\"__orbiter_cwd${PROMPT_COMMAND:+;$PROMPT_COMMAND}\" ;;\n" +
        "  esac\n" +
        "elif [ -n \"$ZSH_VERSION\" ]; then\n" +
        "  autoload -Uz add-zsh-hook && add-zsh-hook precmd __orbiter_cwd\n" +
        "fi\n";

    public const string RcBlock = "\n# Orbiter : suivi du dossier du terminal par le panneau SFTP\n" + RcLine + "\n";

    /// <summary>
    /// Commande à lancer une fois dans le terminal pour activer le suivi sans se reconnecter :
    /// Ctrl+U efface ce qui serait déjà tapé sur la ligne, l'espace initial l'exclut de l'historique (HISTCONTROL=ignorespace).
    /// </summary>
    public const string ActivateNow = "\u0015 . ~/.orbiter_cwd.sh";
}
