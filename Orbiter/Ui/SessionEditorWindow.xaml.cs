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

    private SessionEditorWindow(SessionProfile profile, bool isNew)
    {
        InitializeComponent();
        _profile = profile;
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
    public static SessionProfile? Edit(Window owner, SessionProfile? existing)
    {
        var isNew = existing == null;
        var draft = existing?.Clone() ?? new SessionProfile
        {
            Username = Environment.UserName.ToLowerInvariant(),
        };
        var w = new SessionEditorWindow(draft, isNew) { Owner = owner };
        return w.ShowDialog() == true ? w._profile : null;
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
