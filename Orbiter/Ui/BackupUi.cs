using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Orbiter.Core;

namespace Orbiter.Ui;

/// <summary>Exporter / restaurer une sauvegarde chiffrée des sessions.</summary>
public static class BackupUi
{
    public static void Export(Window owner, IReadOnlyList<SessionProfile> sessions)
    {
        if (sessions.Count == 0)
        {
            Dialogs.Info(owner, "Rien à sauvegarder", "Aucune session enregistrée pour l'instant.");
            return;
        }
        var password = BackupPasswordWindow.Ask(owner);
        if (password == null) return;

        var dlg = new SaveFileDialog
        {
            Title = "Enregistrer la sauvegarde Orbiter",
            FileName = $"Orbiter-sauvegarde-{DateTime.Now:yyyy-MM-dd}{Backup.Extension}",
            Filter = $"Sauvegarde Orbiter (*{Backup.Extension})|*{Backup.Extension}",
            DefaultExt = Backup.Extension,
            AddExtension = true,
        };
        if (dlg.ShowDialog(owner) != true) return;

        try
        {
            var payload = Backup.Collect(sessions);
            var bytes = Busy(owner, () => Backup.Encrypt(payload, password));
            File.WriteAllBytes(dlg.FileName, bytes);
            var withSecret = payload.Sessions.Count(s => s.Password != null || s.Passphrase != null);
            var withKey = payload.Sessions.Count(s => s.KeyContent != null);
            ModernDialog.Show(owner, "Sauvegarde enregistrée",
                "Gardez précieusement le mot de passe de sauvegarde : sans lui, ce fichier ne peut pas être restauré.",
                DialogKind.Success,
                Dialogs.Details(
                    ("Fichier", dlg.FileName, true),
                    ("Sessions", payload.Sessions.Count.ToString(), false),
                    ("Mots de passe", $"{withSecret} mémorisé(s)", false),
                    ("Clés privées", $"{withKey} incluse(s)", false),
                    ("Empreintes", $"{payload.KnownHosts.Count} serveur(s)", false)),
                new DialogButton("OK", "ok", Primary: true, IsDefault: true));
        }
        catch (Exception ex)
        {
            Dialogs.Error(owner, "Échec de la sauvegarde", ex.Message);
        }
    }

    /// <summary>Restaure une sauvegarde dans la liste ; renvoie true si la liste a changé.</summary>
    public static bool Import(Window owner, List<SessionProfile> sessions)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Restaurer une sauvegarde Orbiter",
            Filter = $"Sauvegarde Orbiter (*{Backup.Extension})|*{Backup.Extension}|Tous les fichiers (*.*)|*.*",
        };
        if (dlg.ShowDialog(owner) != true) return false;

        byte[] file;
        try { file = File.ReadAllBytes(dlg.FileName); }
        catch (Exception ex) { Dialogs.Error(owner, "Lecture impossible", ex.Message); return false; }

        Backup.Payload? payload = null;
        var message = "Mot de passe de la sauvegarde :";
        while (payload == null)
        {
            var pwd = PromptWindow.Ask(owner, "Restaurer une sauvegarde", message, password: true);
            if (pwd == null) return false;
            try { payload = Busy(owner, () => Backup.Decrypt(file, pwd)); }
            catch (Backup.WrongPasswordException) { message = "Mot de passe incorrect. Réessayez :"; }
            catch (Exception ex) { Dialogs.Error(owner, "Sauvegarde illisible", ex.Message); return false; }
        }

        var replaced = payload.Sessions.Count(b => sessions.Any(s => s.Id == b.Profile.Id));
        var created = payload.CreatedUtc.ToLocalTime().ToString("dd/MM/yyyy à HH:mm");
        var summary = $"Sauvegarde du {created}" + (payload.Machine.Length > 0 ? $" (PC {payload.Machine})" : "") +
                      $" : {payload.Sessions.Count} session(s)." +
                      (replaced > 0 ? $"\n{replaced} session(s) déjà présente(s) seront remplacées par la version de la sauvegarde." : "") +
                      "\nLes autres sessions actuelles ne sont pas touchées.";
        if (!Dialogs.Confirm(owner, "Restaurer cette sauvegarde ?", summary, "Restaurer", "Annuler")) return false;

        try
        {
            var r = Backup.Apply(payload, sessions);
            var details = new List<(string, string, bool)>
            {
                ("Ajoutées", r.Added.ToString(), false),
                ("Remplacées", r.Replaced.ToString(), false),
                ("Empreintes", $"{r.HostsAdded} ajoutée(s)", false),
            };
            if (r.KeysRestored > 0) details.Add(("Clés privées", $"{r.KeysRestored} recréée(s) dans {Path.Combine(AppPaths.Roaming, "keys")}", false));
            ModernDialog.Show(owner, "Sauvegarde restaurée",
                r.MissingKeys > 0 ? $"{r.MissingKeys} session(s) utilisent une clé privée absente de ce PC : indiquez-la dans la session." : "",
                r.MissingKeys > 0 ? DialogKind.Warning : DialogKind.Success,
                Dialogs.Details(details.ToArray()),
                new DialogButton("OK", "ok", Primary: true, IsDefault: true));
            return true;
        }
        catch (Exception ex)
        {
            Dialogs.Error(owner, "Échec de la restauration", ex.Message);
            return false;
        }
    }

    /// <summary>Calcul de la clé (volontairement lent) avec le curseur d'attente.</summary>
    private static T Busy<T>(Window owner, Func<T> work)
    {
        var old = owner.Cursor;
        owner.Cursor = Cursors.Wait;
        try { return work(); }
        finally { owner.Cursor = old; }
    }
}

/// <summary>Choix du mot de passe de sauvegarde : saisie + confirmation, longueur minimale.</summary>
public sealed class BackupPasswordWindow : Window
{
    private readonly PasswordBox _pwd = new() { Height = 32 };
    private readonly PasswordBox _confirm = new() { Height = 32 };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };

    private BackupPasswordWindow(Window? owner)
    {
        Width = 460;
        SizeToContent = SizeToContent.Height;
        if (owner is { IsLoaded: true }) Owner = owner;

        var root = new DockPanel();
        var footer = new Border { CornerRadius = new CornerRadius(0, 0, 10, 10), Padding = new Thickness(20, 12, 20, 12) };
        footer.SetResourceReference(Border.BackgroundProperty, "DialogFooter");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Annuler", IsCancel = true, MinWidth = 90, Style = (Style)FindResource("SecondaryButton") };
        var ok = new Button { Content = "Continuer", IsDefault = true, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), Style = (Style)FindResource("PrimaryButton") };
        ok.Click += (_, _) => Validate();
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        footer.Child = buttons;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var body = new StackPanel { Margin = new Thickness(20, 10, 20, 18) };
        var intro = new TextBlock
        {
            Text = "La sauvegarde contient vos sessions, mots de passe mémorisés, clés privées et empreintes serveur, " +
                   "chiffrés avec ce mot de passe. Elle peut être restaurée sur n'importe quel PC.\n" +
                   $"Au moins {Backup.MinPasswordLength} caractères ; une phrase de plusieurs mots est idéale. " +
                   "Sans ce mot de passe, la sauvegarde est irrécupérable.",
            TextWrapping = TextWrapping.Wrap, LineHeight = 19, Margin = new Thickness(0, 0, 0, 12),
        };
        intro.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        body.Children.Add(intro);
        body.Children.Add(new TextBlock { Text = "Mot de passe de sauvegarde", Margin = new Thickness(0, 0, 0, 4) });
        body.Children.Add(_pwd);
        body.Children.Add(new TextBlock { Text = "Confirmer le mot de passe", Margin = new Thickness(0, 10, 0, 4) });
        body.Children.Add(_confirm);
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        body.Children.Add(_error);
        root.Children.Add(body);

        Content = root;
        DialogChrome.Apply(this, "Mot de passe de la sauvegarde");
        Loaded += (_, _) => { _pwd.Focus(); Keyboard.Focus(_pwd); };
    }

    private void Validate()
    {
        string? err = null;
        if (_pwd.Password.Length < Backup.MinPasswordLength) err = $"Le mot de passe doit contenir au moins {Backup.MinPasswordLength} caractères.";
        else if (_pwd.Password != _confirm.Password) err = "Les deux mots de passe ne correspondent pas.";
        if (err == null) { DialogResult = true; return; }
        _error.Text = err;
        _error.Visibility = Visibility.Visible;
    }

    public static string? Ask(Window? owner)
    {
        var w = new BackupPasswordWindow(owner);
        return w.ShowDialog() == true ? w._pwd.Password : null;
    }
}
