using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Orbiter.Core;

namespace Orbiter.Ui;

public partial class MainWindow : Window
{
    private readonly SessionStore _store = SessionStore.Load();
    private readonly List<OpenTab> _tabs = new();
    private OpenTab? _active;

    private sealed class OpenTab
    {
        public required SessionView View { get; init; }
        public required Border Header { get; init; }
        public required Ellipse Dot { get; init; }
        public required TextBlock Title { get; init; }
    }

    private static readonly Brush DotIdle = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
    private static readonly Brush DotConnecting = new SolidColorBrush(Color.FromRgb(0xE5, 0xA5, 0x0A));
    private static readonly Brush DotConnected = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Brush DotDisconnected = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));


    public MainWindow()
    {
        InitializeComponent();
        RefreshSessionList();
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        ThemeManager.ApplyTitleBar(this);
        ThemeManager.Changed += UpdateThemeButton;
        UpdateThemeButton();
        Loaded += async (_, _) =>
        {
            await Task.Delay(1500);
            await UpdateUi.CheckAsync(this, manual: false);
        };
    }

    // ---------- Liste des sessions ----------

    private void RefreshSessionList(SessionProfile? select = null)
    {
        var filter = SearchBox.Text.Trim();
        var items = _store.Sessions
            .Where(s => filter.Length == 0
                        || s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || s.Host.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || s.Username.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        SessionList.ItemsSource = items;
        if (select != null) SessionList.SelectedItem = items.FirstOrDefault(i => i.Id == select.Id);
    }

    private SessionProfile? SelectedProfile => SessionList.SelectedItem as SessionProfile;

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshSessionList();
    }

    private void NewSession_Click(object sender, RoutedEventArgs e)
    {
        var p = SessionEditorWindow.Edit(this, null);
        if (p == null) return;
        _store.Sessions.Add(p);
        SaveStore();
        RefreshSessionList(p);
        if (Dialogs.Confirm(this, "Session enregistrée", $"La session « {p.Name} » a été ajoutée. Se connecter maintenant ?",
                "Se connecter", "Plus tard", DialogKind.Success))
            OpenSession(p, p.Kind);
    }

    private void EditSession_Click(object sender, RoutedEventArgs e)
    {
        var current = SelectedProfile;
        if (current == null) return;
        var edited = SessionEditorWindow.Edit(this, current);
        if (edited == null) return;
        var idx = _store.Sessions.FindIndex(s => s.Id == current.Id);
        if (idx >= 0) _store.Sessions[idx] = edited;
        SaveStore();
        RefreshSessionList(edited);
    }

    private void DuplicateSession_Click(object sender, RoutedEventArgs e)
    {
        var current = SelectedProfile;
        if (current == null) return;
        var copy = current.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = current.Name + " (copie)";
        _store.Sessions.Add(copy);
        SaveStore();
        RefreshSessionList(copy);
    }

    private void DeleteSession_Click(object sender, RoutedEventArgs e)
    {
        var current = SelectedProfile;
        if (current == null) return;
        if (!Dialogs.Confirm(this, "Supprimer la session ?", $"La session « {current.Name} » sera retirée de la liste. " +
                "Les fichiers du serveur ne sont pas touchés.", "Supprimer", "Annuler", DialogKind.Danger, danger: true)) return;
        _store.Sessions.RemoveAll(s => s.Id == current.Id);
        SaveStore();
        RefreshSessionList();
    }

    private void SaveStore()
    {
        try { _store.Save(); }
        catch (Exception ex)
        {
            Dialogs.Error(this, "Enregistrement impossible", "Impossible d'enregistrer les sessions : " + ex.Message);
        }
    }

    private void SessionList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is SessionProfile p) OpenSession(p, p.Kind);
    }

    private void SessionList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SelectedProfile is { } p) { OpenSession(p, p.Kind); e.Handled = true; }
        else if (e.Key == Key.Delete) { DeleteSession_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.F2) { EditSession_Click(sender, e); e.Handled = true; }
    }

    private void OpenSession_Click(object sender, RoutedEventArgs e) { if (SelectedProfile is { } p) OpenSession(p, p.Kind); }
    private void OpenSsh_Click(object sender, RoutedEventArgs e) { if (SelectedProfile is { } p) OpenSession(p, SessionKind.Ssh); }
    private void OpenSftp_Click(object sender, RoutedEventArgs e) { if (SelectedProfile is { } p) OpenSession(p, SessionKind.Sftp); }

    // ---------- Connexion rapide ----------

    private static readonly Regex QuickRx = new(@"^(?:(?<u>[^@\s]+)@)?(?<h>\[[^\]]+\]|[^:\s]+)(?::(?<p>\d+))?$");

    private void QuickBox_TextChanged(object sender, TextChangedEventArgs e) =>
        QuickHint.Visibility = QuickBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void QuickBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { QuickConnect(SessionKind.Ssh); e.Handled = true; }
    }

    private void QuickSsh_Click(object sender, RoutedEventArgs e) => QuickConnect(SessionKind.Ssh);
    private void QuickSftp_Click(object sender, RoutedEventArgs e) => QuickConnect(SessionKind.Sftp);

    private void QuickConnect(SessionKind kind)
    {
        var text = QuickBox.Text.Trim();
        var m = QuickRx.Match(text);
        if (!m.Success)
        {
            Dialogs.Warn(this, "Adresse invalide", "Format attendu : utilisateur@hôte ou utilisateur@hôte:port");
            return;
        }
        var user = m.Groups["u"].Success ? m.Groups["u"].Value : null;
        user ??= PromptWindow.Ask(this, "Connexion rapide", $"Nom d'utilisateur pour {m.Groups["h"].Value} :", initial: Environment.UserName.ToLowerInvariant());
        if (string.IsNullOrWhiteSpace(user)) return;

        var p = new SessionProfile
        {
            Name = $"{user}@{m.Groups["h"].Value}",
            Host = m.Groups["h"].Value.Trim('[', ']'),
            Port = m.Groups["p"].Success ? int.Parse(m.Groups["p"].Value) : 22,
            Username = user,
            Kind = kind,
            Auth = AuthKind.Password,
            IsTransient = true,
        };
        OpenSession(p, kind, alreadyCopied: true);
    }

    // ---------- Onglets ----------

    private void OpenSession(SessionProfile profile, SessionKind kind, bool alreadyCopied = false)
    {
        var p = alreadyCopied ? profile : profile.Clone();
        p.Kind = kind;

        var view = new SessionView(p);
        var dot = new Ellipse { Width = 8, Height = 8, Fill = DotIdle, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        var glyph = new TextBlock
        {
            Text = kind == SessionKind.Ssh ? "" : "",
            FontFamily = (FontFamily)FindResource("Icons"),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,

        };
        var sameName = _tabs.Count(t => t.View.Profile.Name == p.Name && t.View.Profile.Kind == kind);
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var title = new TextBlock
        {
            Text = p.Name + (sameName > 0 ? $" ({sameName + 1})" : ""),
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 220,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var close = new Button
        {
            Content = "",
            FontFamily = (FontFamily)FindResource("Icons"),
            FontSize = 9,
            Style = (Style)FindResource("FlatButton"),
            Padding = new Thickness(5),
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "Fermer (Ctrl+W)",
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(dot);
        panel.Children.Add(glyph);
        panel.Children.Add(title);
        panel.Children.Add(close);
        var header = new Border
        {
            Child = panel,

            CornerRadius = new CornerRadius(6, 6, 0, 0),
            Padding = new Thickness(10, 4, 4, 4),
            Margin = new Thickness(0, 0, 3, 0),
            Cursor = Cursors.Hand,
            ToolTip = $"{p.KindLabel} · {p.Endpoint}",
        };

        header.SetResourceReference(Border.BackgroundProperty, "TabBg");
        var tab = new OpenTab { View = view, Header = header, Dot = dot, Title = title };
        _tabs.Add(tab);

        header.MouseLeftButtonDown += (_, _) => Activate(tab);
        header.MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle) CloseTab(tab); };
        close.Click += (_, _) => CloseTab(tab);
        header.ContextMenu = BuildTabMenu(tab);

        view.StateChanged += s =>
        {
            dot.Fill = s switch
            {
                SessionState.Connecting => DotConnecting,
                SessionState.Connected => DotConnected,
                SessionState.Disconnected => DotDisconnected,
                _ => DotIdle,
            };
            if (_active == tab) UpdateToolbar();
        };

        TabStrip.Children.Add(header);
        view.Visibility = Visibility.Collapsed;
        SessionHost.Children.Add(view);
        Activate(tab);
    }

    private ContextMenu BuildTabMenu(OpenTab tab)
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, Action a)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
            return mi;
        }
        Item("Reconnecter", () => tab.View.Reconnect());
        Item("Dupliquer l'onglet", () => OpenSession(tab.View.Profile, tab.View.Profile.Kind));
        if (tab.View.Profile.Kind == SessionKind.Ssh)
        {
            Item("Afficher / masquer le panneau SFTP", () => tab.View.ShowSftpPanel(!tab.View.SftpPanelVisible));
            Item("Ouvrir un onglet SFTP", () => OpenSession(tab.View.Profile, SessionKind.Sftp));
        }
        if (tab.View.Profile.IsTransient)
        {
            menu.Items.Add(new Separator());
            Item("Enregistrer comme session…", () => SaveTransient(tab.View.Profile));
        }
        menu.Items.Add(new Separator());
        Item("Fermer", () => CloseTab(tab));
        Item("Fermer les autres onglets", () => { foreach (var t in _tabs.Where(t => t != tab).ToList()) CloseTab(t); });
        return menu;
    }

    private void SaveTransient(SessionProfile p)
    {
        var draft = p.Clone();
        draft.Id = Guid.NewGuid().ToString("N");
        draft.IsTransient = false;
        var saved = SessionEditorWindow.Edit(this, draft);
        if (saved == null) return;
        _store.Sessions.Add(saved);
        SaveStore();
        RefreshSessionList(saved);
    }

    private void Activate(OpenTab tab)
    {
        _active = tab;
        foreach (var t in _tabs)
        {
            var on = t == tab;
            t.Header.SetResourceReference(Border.BackgroundProperty, on ? "TabSelectedBg" : "TabBg");
            t.Title.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            t.View.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
        Welcome.Visibility = Visibility.Collapsed;
        tab.Header.BringIntoView();
        tab.View.FocusContent();
        UpdateToolbar();
        Title = $"{tab.Title.Text} — Orbiter";
    }

    private void CloseTab(OpenTab tab)
    {
        if (tab.View.State == SessionState.Connected && _tabs.Count > 0 &&
            Keyboard.Modifiers != ModifierKeys.Shift &&
            !Dialogs.Confirm(this, "Fermer la session ?", $"« {tab.Title.Text} » est encore connectée. La connexion sera fermée.",
                "Fermer", "Annuler", DialogKind.Warning))
            return;

        var idx = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        TabStrip.Children.Remove(tab.Header);
        SessionHost.Children.Remove(tab.View);
        tab.View.Dispose();

        if (_tabs.Count == 0)
        {
            _active = null;
            Welcome.Visibility = Visibility.Visible;
            Title = "Orbiter";
            UpdateToolbar();
        }
        else if (_active == tab)
        {
            Activate(_tabs[Math.Min(idx, _tabs.Count - 1)]);
        }
    }

    private void UpdateToolbar()
    {
        ReconnectBtn.IsEnabled = _active != null;
        SftpToggleBtn.IsEnabled = _active?.View.Profile.Kind == SessionKind.Ssh;
    }

    private void Reconnect_Click(object sender, RoutedEventArgs e) => _active?.View.Reconnect();

    private void ToggleSftp_Click(object sender, RoutedEventArgs e)
    {
        if (_active?.View is { } v) v.ShowSftpPanel(!v.SftpPanelVisible);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || _tabs.Count == 0) return;
        if (e.Key == Key.Tab && _active != null)
        {
            var i = (_tabs.IndexOf(_active) + 1) % _tabs.Count;
            Activate(_tabs[i]);
            e.Handled = true;
        }
    }

    private void Theme_Click(object sender, RoutedEventArgs e) =>
        ThemeManager.Set(ThemeManager.IsDark ? ThemeChoice.Light : ThemeChoice.Dark);

    private void UpdateThemeButton()
    {
        ThemeGlyph.Text = ThemeManager.IsDark ? "\uE706" : "\uE708"; // soleil / lune
        ThemeBtn.ToolTip = ThemeManager.IsDark ? "Passer en thème clair" : "Passer en thème sombre";
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var v = typeof(MainWindow).Assembly.GetName().Version;
        var choice = ModernDialog.Show(this, $"Orbiter {v?.ToString(3)}", "Client SSH / SFTP pour Windows.", DialogKind.Info,
            Dialogs.Details(
                ("Terminal", "SSH xterm-256color en onglets", false),
                ("Fichiers", "Explorateur SFTP, glisser-déposer, édition à distance", false),
                ("Tunnels", "Local (-L), distant (-R), SOCKS (-D)", false),
                ("Sécurité", "Secrets chiffrés avec Windows DPAPI", false),
                ("Données", AppPaths.Roaming, true),
                ("Composants", "SSH.NET (MIT), xterm.js (MIT), Microsoft WebView2", false)),
            new DialogButton("Rechercher les mises à jour", "update"),
            new DialogButton("Fermer", "ok", Primary: true, IsDefault: true));
        if (choice == "update") _ = UpdateUi.CheckAsync(this, manual: true);
    }

    // ---------- Commandes enregistrées ----------

    private bool _sendToAll;

    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsWindow.Open(this);

    private void Commands_Click(object sender, RoutedEventArgs e)
    {
        // menu aligné sur le bord droit du bouton (il ne déborde pas de la fenêtre)
        var menu = new ContextMenu
        {
            PlacementTarget = CommandsBtn,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Custom,
            CustomPopupPlacementCallback = (popup, target, _) => new[]
            {
                new System.Windows.Controls.Primitives.CustomPopupPlacement(
                    new Point(target.Width - popup.Width, target.Height + 2), System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal),
            },
        };
        var targets = CommandTargets();
        var commands = ThemeManager.Settings.CommandList;

        var head = new MenuItem
        {
            Header = targets.Count == 0 ? "Aucun terminal SSH connecté"
                : _sendToAll ? $"Envoyer à {targets.Count} terminal(aux) connecté(s)" : $"Envoyer à « {targets[0].Profile.Name} »",
            IsEnabled = false,
        };
        menu.Items.Add(head);
        menu.Items.Add(new Separator());
        foreach (var c in commands)
        {
            var mi = new MenuItem
            {
                Header = c.Name, InputGestureText = c.Preview, IsEnabled = targets.Count > 0,
                ToolTip = c.Command + (c.RunImmediately ? "" : "\n(collée sans être exécutée)"),
            };
            var cmd = c;
            mi.Click += (_, _) => { foreach (var t in CommandTargets()) t.Terminal!.SendCommand(cmd.Command, cmd.RunImmediately); };
            menu.Items.Add(mi);
        }
        if (commands.Count == 0) menu.Items.Add(new MenuItem { Header = "Aucune commande enregistrée", IsEnabled = false });
        menu.Items.Add(new Separator());
        var all = new MenuItem { Header = "Envoyer à tous les terminaux connectés", IsCheckable = true, IsChecked = _sendToAll, StaysOpenOnClick = true };
        all.Click += (_, _) =>
        {
            _sendToAll = all.IsChecked;
            var n = CommandTargets().Count;
            head.Header = n == 0 ? "Aucun terminal SSH connecté"
                : _sendToAll ? $"Envoyer à {n} terminal(aux) connecté(s)" : $"Envoyer à « {CommandTargets()[0].Profile.Name} »";
        };
        menu.Items.Add(all);
        var manage = new MenuItem { Header = "Gérer les commandes…" };
        manage.Click += (_, _) => SettingsWindow.Open(this);
        menu.Items.Add(manage);
        menu.IsOpen = true;
    }

    /// <summary>Terminaux qui recevront la commande : l'onglet actif, ou tous les terminaux connectés.</summary>
    private List<SessionView> CommandTargets()
    {
        static bool Live(SessionView v) => v.Terminal is { State: SessionState.Connected };
        if (_sendToAll) return _tabs.Select(t => t.View).Where(Live).ToList();
        return _active?.View is { } a && Live(a) ? new List<SessionView> { a } : new List<SessionView>();
    }

    private void Backup_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = BackupBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var export = new MenuItem { Header = "Exporter une sauvegarde chiffrée…" };
        export.Click += (_, _) => BackupUi.Export(this, _store.Sessions);
        var import = new MenuItem { Header = "Restaurer une sauvegarde…" };
        import.Click += (_, _) =>
        {
            if (!BackupUi.Import(this, _store.Sessions)) return;
            SaveStore();
            RefreshSessionList();
        };
        menu.Items.Add(export);
        menu.Items.Add(import);
        menu.IsOpen = true;
    }

    private bool _quitConfirmed;

    /// <summary>Demande confirmation s'il reste des sessions connectées (avant une mise à jour).</summary>
    public bool ConfirmQuit(string title)
    {
        var live = _tabs.Count(t => t.View.State == SessionState.Connected);
        _quitConfirmed = live == 0 || Dialogs.Confirm(this, title,
            $"Orbiter va redémarrer : {live} session(s) encore connectée(s) seront fermées.", "Continuer", "Annuler", DialogKind.Warning);
        return _quitConfirmed;
    }

    /// <summary>Ferme Orbiter sans nouvelle question (la confirmation a déjà eu lieu).</summary>
    public void QuitNow()
    {
        _quitConfirmed = true;
        Close();
        Application.Current.Shutdown();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var live = _tabs.Count(t => t.View.State == SessionState.Connected);
        if (live > 0 && !_quitConfirmed && !Dialogs.Confirm(this, "Quitter Orbiter ?", $"{live} session(s) encore connectée(s) seront fermées.",
                "Quitter", "Annuler", DialogKind.Warning))
        {
            e.Cancel = true;
            return;
        }
        foreach (var t in _tabs) t.View.Dispose();
    }
}
