using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Orbiter.Core;

namespace Orbiter.Ui;

public partial class SessionEditorWindow : Window
{
    private readonly SessionProfile _profile;
    private readonly ObservableCollection<TunnelDef> _tunnels;

    private readonly List<string> _folders;

    private SessionEditorWindow(SessionProfile profile, bool isNew, IEnumerable<string>? folders)
    {
        InitializeComponent();
        _profile = profile;
        _folders = folders?.ToList() ?? new();
        FolderBox.Text = profile.Folder ?? "";
        FolderPick.IsEnabled = _folders.Count > 0;
        DialogChrome.Apply(this, isNew ? "Nouvelle session" : $"Modifier « {profile.Name} »");

        NameBox.Text = profile.Name;
        HostBox.Text = profile.Host;
        PortBox.Text = profile.Port.ToString();
        UserBox.Text = profile.Username;
        KindSsh.IsChecked = profile.Kind == SessionKind.Ssh;
        KindSftp.IsChecked = profile.Kind == SessionKind.Sftp;
        AuthPassword.IsChecked = profile.Auth == AuthKind.Password;
        AuthKey.IsChecked = profile.Auth == AuthKind.PrivateKey;

        var pwd = Secret.Unprotect(profile.ProtectedPassword);
        PasswordBox.Password = pwd ?? profile.RuntimePassword ?? "";
        RememberPassword.IsChecked = pwd != null || isNew;
        KeyPathBox.Text = profile.KeyPath ?? "";
        var pass = Secret.Unprotect(profile.ProtectedPassphrase);
        PassphraseBox.Password = pass ?? "";
        RememberPassphrase.IsChecked = pass != null;

        AutoSftpBox.IsChecked = profile.AutoSftpPanel;
        RemoteDirBox.Text = profile.InitialRemoteDir ?? "";
        _tunnels = new ObservableCollection<TunnelDef>(profile.Clone().Tunnels);
        TunnelList.ItemsSource = _tunnels;

        UpdateEnabled();
        TunnelKind_Changed(this, null!);
        MaxHeight = SystemParameters.WorkArea.Height - 20;
        Loaded += (_, _) => (string.IsNullOrEmpty(HostBox.Text) ? HostBox : NameBox).Focus();
    }

    /// <summary>Ouvre l'éditeur. Renvoie le profil modifié, ou null si annulé.</summary>
    /// <param name="folder">Dossier proposé pour une nouvelle session.</param>
    /// <param name="folders">Dossiers existants, proposés par le bouton à côté du champ.</param>
    public static SessionProfile? Edit(Window owner, SessionProfile? existing, string? folder = null, IEnumerable<string>? folders = null)
    {
        var isNew = existing == null;
        var draft = existing?.Clone() ?? new SessionProfile
        {
            Username = Environment.UserName.ToLowerInvariant(),
            Folder = folder,
        };
        var w = new SessionEditorWindow(draft, isNew, folders) { Owner = owner };
        return w.ShowDialog() == true ? w._profile : null;
    }

    private void FolderPick_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = FolderPick, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var none = new MenuItem { Header = "Hors dossier", IsCheckable = true, IsChecked = FolderBox.Text.Trim().Length == 0 };
        none.Click += (_, _) => FolderBox.Text = "";
        menu.Items.Add(none);
        foreach (var f in _folders)
        {
            var mi = new MenuItem { Header = f, IsCheckable = true, IsChecked = string.Equals(f, FolderBox.Text.Trim(), StringComparison.CurrentCultureIgnoreCase) };
            mi.Click += (_, _) => FolderBox.Text = f;
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    private void Kind_Changed(object sender, RoutedEventArgs e) => UpdateEnabled();
    private void Auth_Changed(object sender, RoutedEventArgs e) => UpdateEnabled();

    private void UpdateEnabled()
    {
        if (KeyPanel == null || PasswordPanel == null || TunnelSection == null || AutoSftpBox == null) return;
        var key = AuthKey.IsChecked == true;
        KeyPanel.IsEnabled = key;
        PasswordPanel.IsEnabled = !key;
        var ssh = KindSsh.IsChecked == true;
        TunnelSection.IsEnabled = ssh;
        AutoSftpBox.IsEnabled = ssh;
    }

    private void TunnelKind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TunnelHelp == null || TunnelDestHost == null) return;
        var idx = TunnelKind.SelectedIndex;
        TunnelDestHost.IsEnabled = TunnelDestPort.IsEnabled = idx != 2;
        TunnelHelp.Text = idx switch
        {
            0 => "Local : un port de votre PC (ex. 127.0.0.1:8080) est relayé vers hôte:port vu depuis le serveur (ex. localhost:80).",
            1 => "Distant : un port ouvert sur le serveur SSH est relayé vers hôte:port vu depuis votre PC.",
            _ => "SOCKS : proxy SOCKS4/5 local ; configurez votre navigateur sur 127.0.0.1:port pour sortir par le serveur.",
        };
    }

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var dlg = new OpenFileDialog
        {
            Title = "Choisir une clé privée",
            Filter = "Clés privées|*.*",
            InitialDirectory = Directory.Exists(sshDir) ? sshDir : null,
        };
        if (dlg.ShowDialog(this) == true) KeyPathBox.Text = dlg.FileName;
    }

    private static bool TryPort(string text, out uint port) =>
        uint.TryParse(text.Trim(), out port) && port is > 0 and < 65536;

    private void AddTunnel_Click(object sender, RoutedEventArgs e)
    {
        var kind = (TunnelKind)TunnelKind.SelectedIndex;
        if (!TryPort(TunnelBindPort.Text, out var bindPort))
        {
            Dialogs.Warn(this, "Vérifiez la session", "Port d'écoute invalide.");
            return;
        }
        uint destPort = 0;
        if (kind != Core.TunnelKind.Dynamic &&
            (string.IsNullOrWhiteSpace(TunnelDestHost.Text) || !TryPort(TunnelDestPort.Text, out destPort)))
        {
            Dialogs.Warn(this, "Vérifiez la session", "Hôte ou port de destination invalide.");
            return;
        }
        _tunnels.Add(new TunnelDef
        {
            Kind = kind,
            BindHost = string.IsNullOrWhiteSpace(TunnelBindHost.Text) ? "127.0.0.1" : TunnelBindHost.Text.Trim(),
            BindPort = bindPort,
            DestHost = TunnelDestHost.Text.Trim(),
            DestPort = destPort,
        });
        TunnelBindPort.Clear();
        TunnelDestHost.Clear();
        TunnelDestPort.Clear();
    }

    private void RemoveTunnel_Click(object sender, RoutedEventArgs e)
    {
        if (TunnelList.SelectedItem is TunnelDef t) _tunnels.Remove(t);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim();
        var user = UserBox.Text.Trim();
        if (host.Length == 0) { Dialogs.Warn(this, "Vérifiez la session", "Indiquez l'hôte."); HostBox.Focus(); return; }
        if (user.Length == 0) { Dialogs.Warn(this, "Vérifiez la session", "Indiquez le nom d'utilisateur."); UserBox.Focus(); return; }
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is <= 0 or > 65535)
        {
            Dialogs.Warn(this, "Vérifiez la session", "Port invalide."); PortBox.Focus(); return;
        }
        var useKey = AuthKey.IsChecked == true;
        if (useKey && !File.Exists(KeyPathBox.Text.Trim()))
        {
            Dialogs.Warn(this, "Vérifiez la session", "Fichier de clé privée introuvable."); KeyPathBox.Focus(); return;
        }

        var p = _profile;
        p.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? $"{user}@{host}" : NameBox.Text.Trim();
        p.Host = host;
        p.Port = port;
        var folder = FolderBox.Text.Trim();
        // même casse qu'un dossier existant, pour ne pas créer « Prod » et « prod »
        p.Folder = folder.Length == 0 ? null
            : _folders.FirstOrDefault(f => string.Equals(f, folder, StringComparison.CurrentCultureIgnoreCase)) ?? (folder.Length > 60 ? folder[..60] : folder);
        p.Username = user;
        p.Kind = KindSftp.IsChecked == true ? SessionKind.Sftp : SessionKind.Ssh;
        p.Auth = useKey ? AuthKind.PrivateKey : AuthKind.Password;
        p.ProtectedPassword = !useKey && RememberPassword.IsChecked == true ? Secret.Protect(PasswordBox.Password) : null;
        p.KeyPath = KeyPathBox.Text.Trim().Length > 0 ? KeyPathBox.Text.Trim() : null;
        p.ProtectedPassphrase = useKey && RememberPassphrase.IsChecked == true ? Secret.Protect(PassphraseBox.Password) : null;
        p.RuntimePassword = null;
        p.RuntimePassphrase = useKey && PassphraseBox.Password.Length > 0 ? PassphraseBox.Password : null;
        p.IgnoreStoredSecrets = false;
        p.AutoSftpPanel = AutoSftpBox.IsChecked == true;
        p.InitialRemoteDir = RemoteDirBox.Text.Trim().Length > 0 ? RemoteDirBox.Text.Trim() : null;
        p.Tunnels = _tunnels.ToList();
        p.IsTransient = false;
        DialogResult = true;
    }
}
