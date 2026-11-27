using System.Windows;

namespace Orbiter.Ui;

/// <summary>Validation de l'empreinte d'un serveur. Renvoie "save", "once" ou null (refus).</summary>
public static class HostKeyDialog
{
    public static string? AskNew(Window? owner, string host, int port, string alg, string fp) =>
        ModernDialog.Show(owner,
            "Nouveau serveur",
            "C'est la première connexion à ce serveur. Vérifiez que l'empreinte correspond bien à celle " +
            "communiquée par l'administrateur avant de lui faire confiance.",
            DialogKind.Question,
            Dialogs.Details(("Serveur", $"{host}:{port}", false), ("Type de clé", alg, true), ("Empreinte", "SHA256:" + fp, true)),
            new DialogButton("Refuser", "no"),
            new DialogButton("Cette fois seulement", "once"),
            new DialogButton("Faire confiance", "save", Primary: true, IsDefault: true));

    public static string? AskChanged(Window? owner, string host, int port, string oldAlg, string oldFp, string alg, string fp) =>
        ModernDialog.Show(owner,
            "L'empreinte du serveur a changé",
            "La clé présentée par ce serveur ne correspond pas à celle mémorisée. Il peut s'agir d'une " +
            "réinstallation du serveur… ou d'une interception de la connexion (attaque « man-in-the-middle »). " +
            "En cas de doute, refusez et contactez l'administrateur.",
            DialogKind.Danger,
            Dialogs.Details(("Serveur", $"{host}:{port}", false),
                ("Ancienne", $"{oldAlg}  SHA256:{oldFp}", true),
                ("Nouvelle", $"{alg}  SHA256:{fp}", true)),
            new DialogButton("Remplacer l'empreinte", "save", Danger: true),
            new DialogButton("Cette fois seulement", "once"),
            new DialogButton("Refuser", "no", Primary: true, IsDefault: true));
}
