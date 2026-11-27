using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Orbiter.Core;

namespace Orbiter.Ui;

/// <summary>Contenu d'un onglet : terminal SSH (+ panneau SFTP) ou explorateur SFTP seul.</summary>
public sealed class SessionView : Grid, IDisposable
{
    public SessionProfile Profile { get; }
    public TerminalView? Terminal { get; }
    public SftpBrowser? Sftp { get; private set; }
    private readonly ColumnDefinition? _sftpColumn;
    private readonly GridSplitter? _splitter;

    public event Action<SessionState>? StateChanged;

    public SessionState State => Terminal?.State ?? Sftp?.State ?? SessionState.Idle;

    public SessionView(SessionProfile profile)
    {
        Profile = profile;

        if (profile.Kind == SessionKind.Sftp)
        {
            Sftp = new SftpBrowser(profile, linkedToTerminal: false);
            Sftp.StateChanged += s => StateChanged?.Invoke(s);
            Children.Add(Sftp);
            return;
        }

        Terminal = new TerminalView(profile);
        Terminal.StateChanged += s => StateChanged?.Invoke(s);

        _sftpColumn = new ColumnDefinition { Width = new GridLength(0) };
        ColumnDefinitions.Add(_sftpColumn);
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _splitter = new GridSplitter
        {
            Width = 5,
            HorizontalAlignment = HorizontalAlignment.Stretch,

            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            Visibility = Visibility.Collapsed,
        };
        _splitter.SetResourceReference(BackgroundProperty, "Border");
        SetColumn(_splitter, 1);
        SetColumn(Terminal, 2);
        Children.Add(_splitter);
        Children.Add(Terminal);

        if (profile.AutoSftpPanel)
            Terminal.Connected += () => ShowSftpPanel(true);
        Terminal.DirectoryChanged += _ => FollowNow();
    }

    // ---------- Le panneau SFTP suit le dossier du terminal ----------

    private bool _follow = ThemeManager.Settings.SftpFollowTerminal;

    private void FollowNow()
    {
        if (!_follow || Sftp is not { State: SessionState.Connected } s || Terminal?.CurrentDirectory is not { } dir) return;
        if (dir != s.CurrentDir) _ = s.NavigateAsync(dir);
    }

    private async void ToggleFollow()
    {
        if (Sftp == null || Terminal == null) return;
        if (_follow) { SetFollow(false); return; }

        // le shell annonce déjà son dossier : rien à installer
        if (Terminal.CurrentDirectory != null || Terminal.State != SessionState.Connected) { SetFollow(true); FollowNow(); return; }

        var owner = Window.GetWindow(this);
        var choice = ModernDialog.Show(owner, "Suivre le dossier du terminal",
            "Ce serveur n'indique pas encore à Orbiter dans quel dossier se trouve le terminal. Orbiter peut y installer un petit script " +
            $"(~/{CwdHook.FileName}), chargé par ~/.bashrc (et ~/.zshrc s'il existe). Il ne change rien à l'affichage du terminal " +
            "et fonctionne avec bash et zsh. Pour le désactiver plus tard, supprimez ce fichier.",
            DialogKind.Question, Dialogs.Details(("Ajouté à ~/.bashrc", CwdHook.RcLine, true)),
            new DialogButton("Annuler", "no"),
            new DialogButton("Installer sur ce serveur", "yes", Primary: true, IsDefault: true));
        if (choice != "yes") return;
        try
        {
            await Sftp.InstallCwdHookAsync();
        }
        catch (Exception ex)
        {
            Dialogs.Error(owner, "Installation impossible", ex.Message);
            return;
        }
        SetFollow(true);
        // active le suivi tout de suite dans le shell ouvert (les prochaines connexions le chargent toutes seules)
        Terminal.SendCommand(CwdHook.ActivateNow, run: true);
    }

    private void SetFollow(bool on)
    {
        _follow = on;
        Sftp?.ShowFollowing(on);
        ThemeManager.Settings.SftpFollowTerminal = on;
        ThemeManager.Settings.Save();
    }

    public bool SftpPanelVisible => _sftpColumn != null && _sftpColumn.Width.Value > 0;

    /// <summary>Affiche (et connecte si besoin) le panneau SFTP à gauche du terminal.</summary>
    public void ShowSftpPanel(bool show)
    {
        if (Terminal == null || _sftpColumn == null || _splitter == null) return;
        if (show)
        {
            if (Sftp == null)
            {
                Sftp = new SftpBrowser(Profile, linkedToTerminal: true);
                Sftp.ChangeDirectoryInTerminal += path =>
                    Terminal.SendInput("cd '" + path.Replace("'", "'\\''") + "'\r");
                Sftp.ShowFollowing(_follow);
                Sftp.FollowClicked += ToggleFollow;
                Sftp.StartDirectoryProvider = () => _follow ? Terminal.CurrentDirectory : null;
                SetColumn(Sftp, 0);
                Children.Add(Sftp);
            }
            if (_sftpColumn.Width.Value == 0) _sftpColumn.Width = new GridLength(440);
            _splitter.Visibility = Visibility.Visible;
            Sftp.Visibility = Visibility.Visible;
            if (Sftp.State is SessionState.Idle or SessionState.Disconnected) Sftp.Connect();
        }
        else
        {
            _sftpColumn.Width = new GridLength(0);
            _splitter.Visibility = Visibility.Collapsed;
            if (Sftp != null) Sftp.Visibility = Visibility.Collapsed;
        }
    }

    public void Reconnect()
    {
        if (Terminal != null) Terminal.Reconnect();
        else Sftp?.Connect();
    }

    public void FocusContent()
    {
        if (Terminal != null) Dispatcher.BeginInvoke(Terminal.FocusTerminal);
    }

    public void Dispose()
    {
        Terminal?.Dispose();
        Sftp?.Dispose();
    }
}
