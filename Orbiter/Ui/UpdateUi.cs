using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Orbiter.Core;

namespace Orbiter.Ui;

/// <summary>Proposition de mise à jour : au démarrage (silencieux si rien de neuf) ou à la demande (À propos).</summary>
public static class UpdateUi
{
    private static bool _busy;

    /// <summary>Recherche automatique au démarrage (désactivée par UiPreview).</summary>
    public static bool AutoCheck { get; set; } = true;

    public static async Task CheckAsync(MainWindow owner, bool manual)
    {
        if (_busy || !manual && !AutoCheck) return;
        _busy = true;
        try
        {
            UpdateInfo? info;
            try { info = await Updater.CheckAsync(); }
            catch (Exception ex)
            {
                if (manual) Dialogs.Warn(owner, "Recherche impossible",
                    "Impossible de joindre GitHub pour vérifier les mises à jour.\n" + ex.Message);
                return;
            }

            if (info == null)
            {
                if (manual) Dialogs.Info(owner, "Orbiter est à jour", $"Vous utilisez la dernière version ({Updater.Current.ToString(3)}).");
                return;
            }
            var v = info.Version.ToString(3);
            if (!manual && ThemeManager.Settings.SkippedVersion == v) return;

            var choice = Offer(owner, info);
            if (choice == "skip")
            {
                ThemeManager.Settings.SkippedVersion = v;
                ThemeManager.Settings.Save();
                return;
            }
            if (choice != "update") return;

            if (info.AssetUrl == null)
            {
                OpenPage(info.PageUrl);
                return;
            }
            Install(owner, info);
        }
        finally { _busy = false; }
    }

    /// <summary>Boîte « nouvelle version disponible » avec les notes de version.</summary>
    public static string? Offer(Window? owner, UpdateInfo info)
    {
        var v = info.Version.ToString(3);
        var how = info.AssetUrl == null
            ? "La page de téléchargement va s'ouvrir dans le navigateur."
            : Updater.IsInstalled
                ? "La mise à jour s'installe automatiquement puis Orbiter redémarre."
                : "Le fichier portable est remplacé par la nouvelle version puis Orbiter redémarre.";
        return ModernDialog.Show(owner, $"Orbiter {v} est disponible",
            $"Vous utilisez la version {Updater.Current.ToString(3)}. {how}",
            DialogKind.Info, NotesBox(info.Notes),
            new DialogButton("Ignorer cette version", "skip"),
            new DialogButton("Plus tard", "later"),
            new DialogButton("Mettre à jour", "update", Primary: true, IsDefault: true));
    }

    private static UIElement NotesBox(string notes)
    {
        var box = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 10, 12, 10), BorderThickness = new Thickness(1) };
        box.SetResourceReference(Border.BackgroundProperty, "CodeBg");
        box.SetResourceReference(Border.BorderBrushProperty, "Border");
        var panel = new StackPanel();
        var head = new TextBlock { Text = "Nouveautés", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        panel.Children.Add(head);
        panel.Children.Add(new TextBlock
        {
            Text = notes.Length > 0 ? notes : "Pas de notes pour cette version.",
            TextWrapping = TextWrapping.Wrap, LineHeight = 19,
        });
        box.Child = new ScrollViewer { Content = panel, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        return box;
    }

    private static void Install(MainWindow owner, UpdateInfo info)
    {
        var win = new UpdateProgressWindow(info.Version.ToString(3)) { Owner = owner };
        string? file = null;
        Exception? error = null;
        var download = Task.Run(async () =>
        {
            try { file = await Updater.DownloadAsync(info, win.Progress, win.Token); }
            catch (Exception ex) { error = ex; }
        });
        win.Run(download);

        if (win.Cancelled || error is OperationCanceledException) return;
        if (error != null || file == null)
        {
            if (ModernDialog.Show(owner, "Échec de la mise à jour", error?.Message ?? "Téléchargement interrompu.", DialogKind.Danger,
                    null, new DialogButton("Fermer", "no"), new DialogButton("Ouvrir la page de téléchargement", "page", Primary: true)) == "page")
                OpenPage(info.PageUrl);
            return;
        }

        if (!owner.ConfirmQuit("Installer la mise à jour ?")) return;
        try { Updater.Apply(file); }
        catch (Exception ex)
        {
            Dialogs.Error(owner, "Échec de la mise à jour", ex.Message);
            return;
        }
        owner.QuitNow();
    }

    private static void OpenPage(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}

/// <summary>Fenêtre de progression du téléchargement, annulable.</summary>
public sealed class UpdateProgressWindow : Window
{
    private readonly CancellationTokenSource _cts = new();
    private readonly ProgressBar _bar = new() { Height = 6, Maximum = 1, Margin = new Thickness(0, 12, 0, 6) };
    private readonly TextBlock _pct = new() { Text = "Connexion…", FontSize = 12 };
    private bool _finished;

    public CancellationToken Token => _cts.Token;
    public IProgress<double> Progress { get; }
    public bool Cancelled { get; private set; }

    public UpdateProgressWindow(string version)
    {
        Width = 420;
        SizeToContent = SizeToContent.Height;
        Progress = new Progress<double>(p =>
        {
            _bar.Value = p;
            _pct.Text = $"{p:P0}";
        });
        _pct.SetResourceReference(TextBlock.ForegroundProperty, "Muted");

        var root = new StackPanel { Margin = new Thickness(20, 6, 20, 16) };
        root.Children.Add(new TextBlock { Text = $"Téléchargement d'Orbiter {version}…" });
        root.Children.Add(_bar);
        var foot = new DockPanel();
        var cancel = new Button { Content = "Annuler", MinWidth = 90, Style = (Style)FindResource("SecondaryButton") };
        cancel.Click += (_, _) => Close();
        DockPanel.SetDock(cancel, Dock.Right);
        foot.Children.Add(cancel);
        _pct.VerticalAlignment = VerticalAlignment.Center;
        foot.Children.Add(_pct);
        root.Children.Add(foot);
        Content = root;
        DialogChrome.Apply(this, "Mise à jour");
        Closing += (_, _) =>
        {
            if (_finished) return;
            Cancelled = true;
            _cts.Cancel();
        };
    }

    /// <summary>Affiche la fenêtre (modale) jusqu'à la fin de la tâche ou l'annulation.</summary>
    public void Run(Task task)
    {
        task.ContinueWith(_ => Dispatcher.BeginInvoke(() =>
        {
            _finished = true;
            if (IsLoaded) Close();
        }));
        Loaded += (_, _) => { if (task.IsCompleted) { _finished = true; Close(); } };
        ShowDialog();
    }
}
