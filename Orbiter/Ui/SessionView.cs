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
