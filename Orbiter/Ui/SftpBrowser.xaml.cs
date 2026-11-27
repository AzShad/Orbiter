using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Orbiter.Core;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace Orbiter.Ui;

public class RemoteItem
{
    private static readonly Brush FolderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xA1, 0x1B));
    private static readonly Brush FileBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x93, 0xA1));
    private static readonly Brush LinkBrush = new SolidColorBrush(Color.FromRgb(0x0A, 0x9C, 0x86));

    public string Name { get; init; } = "";
    public string FullName { get; init; } = "";
    public bool IsDirectory { get; init; }
    public bool IsLink { get; init; }
    public bool IsParent { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public string Permissions { get; init; } = "";
    public string Octal { get; init; } = "";
    public string Owner { get; init; } = "";

    public string Glyph => IsParent ? "" : IsDirectory ? "" : IsLink ? "" : "";
    public Brush GlyphBrush => IsDirectory || IsParent ? FolderBrush : IsLink ? LinkBrush : FileBrush;
    public string SizeText => IsDirectory || IsParent ? "" : FormatSize(Size);
    public string ModifiedText => IsParent ? "" : Modified.ToString("dd/MM/yyyy HH:mm");

    public static string FormatSize(double b)
    {
        string[] u = { "o", "Ko", "Mo", "Go", "To" };
        var i = 0;
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return i == 0 ? $"{b:0} {u[i]}" : $"{b:0.#} {u[i]}";
    }
}

public partial class SftpBrowser : UserControl, IDisposable
{
    private readonly SessionProfile _profile;
    private SftpClient? _client;
    private string _cwd = "/";
    private string _home = "/";
    private bool _disposed;

    private readonly SemaphoreSlim _transferGate = new(1, 1);
    private CancellationTokenSource? _transferCts;
    private long _done, _total;
    private string _transferLabel = "";
    private readonly Stopwatch _transferWatch = new();
    private readonly DispatcherTimer _progressTimer;
    private readonly List<FileSystemWatcher> _watchers = new();
    private static string _lastDownloadDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public SessionState State { get; private set; } = SessionState.Idle;
    public event Action<SessionState>? StateChanged;
    /// <summary>Demande d'ouvrir ce dossier dans le terminal associé (« cd »).</summary>
    public event Action<string>? ChangeDirectoryInTerminal;

    public SftpBrowser(SessionProfile profile, bool linkedToTerminal)
    {
        InitializeComponent();
        _profile = profile;
        CdButton.Visibility = linkedToTerminal ? Visibility.Visible : Visibility.Collapsed;
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _progressTimer.Tick += (_, _) => UpdateProgress();
        if (!linkedToTerminal) Loaded += (_, _) => { if (State == SessionState.Idle) Connect(); };
    }

    private void SetState(SessionState s)
    {
        State = s;
        StateChanged?.Invoke(s);
        switch (s)
        {
            case SessionState.Connecting:
                Overlay.Visibility = Visibility.Visible;
                OverlayText.Text = $"Connexion SFTP à {_profile.Host}…";
                ReconnectButton.Visibility = Visibility.Collapsed;
                break;
            case SessionState.Connected:
                Overlay.Visibility = Visibility.Collapsed;
                break;
            case SessionState.Disconnected:
                Overlay.Visibility = Visibility.Visible;
                ReconnectButton.Visibility = Visibility.Visible;
                break;
        }
    }

    // ---------- Connexion ----------

    public async void Connect()
    {
        if (_disposed || State is SessionState.Connecting or SessionState.Connected) return;
        ConnectionInfo? ci;
        try { ci = SshConnector.Prepare(_profile, Window.GetWindow(this)); }
        catch (Exception ex) { Fail(ex.Message); return; }
        if (ci == null) { Fail("Connexion annulée."); return; }

        SetState(SessionState.Connecting);
        var client = new SftpClient(ci) { KeepAliveInterval = TimeSpan.FromSeconds(30), OperationTimeout = TimeSpan.FromMinutes(2) };
        SshConnector.AttachHostKeyCheck(client, _profile.Host, _profile.Port, Dispatcher);
        try
        {
            await Task.Run(client.Connect);
        }
        catch (Exception ex)
        {
            client.Dispose();
            if (ex is SshAuthenticationException)
            {
                _profile.RuntimePassword = null;
                _profile.RuntimePassphrase = null;
                _profile.IgnoreStoredSecrets = true;
            }
            Fail("Échec SFTP : " + SshConnector.Describe(ex));
            return;
        }
        if (_disposed) { client.Dispose(); return; }

        _client = client;
        client.ErrorOccurred += (_, e) => Dispatcher.BeginInvoke(() => OnLost(client, e.Exception));
        _home = client.WorkingDirectory;
        SetState(SessionState.Connected);

        var start = string.IsNullOrWhiteSpace(_profile.InitialRemoteDir) ? _home : _profile.InitialRemoteDir!;
        if (State == SessionState.Connected && _cwd != "/" && _cwd != _home) start = _cwd; // reconnexion : on reste où on était
        await NavigateAsync(start);
    }

    private void Fail(string message)
    {
        SetState(SessionState.Disconnected);
        OverlayText.Text = message;
    }

    private void OnLost(SftpClient client, Exception ex)
    {
        if (client != _client || _disposed) return;
        _client = null;
        Task.Run(client.Dispose);
        Fail("Connexion SFTP perdue : " + SshConnector.Describe(ex));
    }

    private void Reconnect_Click(object sender, RoutedEventArgs e) => Connect();

    private SftpClient? Client
    {
        get
        {
            if (_client is { IsConnected: true }) return _client;
            if (_client != null) OnLost(_client, new SshConnectionException("Connexion fermée."));
            return null;
        }
    }

    // ---------- Navigation ----------

    private static string Combine(string dir, string name) => dir.EndsWith('/') ? dir + name : dir + "/" + name;

    private static string ParentDir(string path)
    {
        path = path.TrimEnd('/');
        var i = path.LastIndexOf('/');
        return i <= 0 ? "/" : path[..i];
    }

    private static string PermString(SftpFileAttributes a)
    {
        char T(bool b, char c) => b ? c : '-';
        var type = a.IsDirectory ? 'd' : a.IsSymbolicLink ? 'l' : '-';
        return $"{type}{T(a.OwnerCanRead, 'r')}{T(a.OwnerCanWrite, 'w')}{T(a.OwnerCanExecute, 'x')}" +
               $"{T(a.GroupCanRead, 'r')}{T(a.GroupCanWrite, 'w')}{T(a.GroupCanExecute, 'x')}" +
               $"{T(a.OthersCanRead, 'r')}{T(a.OthersCanWrite, 'w')}{T(a.OthersCanExecute, 'x')}";
    }

    private static string OctalString(SftpFileAttributes a)
    {
        int D(bool r, bool w, bool x) => (r ? 4 : 0) + (w ? 2 : 0) + (x ? 1 : 0);
        return $"{D(a.OwnerCanRead, a.OwnerCanWrite, a.OwnerCanExecute)}" +
               $"{D(a.GroupCanRead, a.GroupCanWrite, a.GroupCanExecute)}" +
               $"{D(a.OthersCanRead, a.OthersCanWrite, a.OthersCanExecute)}";
    }

    private static RemoteItem ToItem(ISftpFile f) => new()
    {
        Name = f.Name,
        FullName = f.FullName,
        IsDirectory = f.IsDirectory,
        IsLink = f.IsSymbolicLink,
        Size = f.Length,
        Modified = f.LastWriteTime,
        Permissions = PermString(f.Attributes),
        Octal = OctalString(f.Attributes),
        Owner = f.UserId.ToString(),
    };

    public async Task NavigateAsync(string path)
    {
        var c = Client;
        if (c == null) return;
        StatusText.Text = "Chargement…";
        try
        {
            var items = await Task.Run(() => c.ListDirectory(path)
                .Where(f => f.Name != "." && f.Name != "..")
                .Select(ToItem)
                .OrderByDescending(i => i.IsDirectory)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToList());
            _cwd = path;
            if (path != "/") items.Insert(0, new RemoteItem { Name = "..", FullName = ParentDir(path), IsParent = true, IsDirectory = true });
            List.ItemsSource = items;
            PathBox.Text = path;
            var count = items.Count(i => !i.IsParent);
            StatusText.Text = $"{count} élément(s)";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Erreur : " + ex.Message;
            PathBox.Text = _cwd;
        }
    }

    private Task RefreshAsync() => NavigateAsync(_cwd);

    private async void Up_Click(object sender, RoutedEventArgs e) => await NavigateAsync(ParentDir(_cwd));
    private async void Home_Click(object sender, RoutedEventArgs e) => await NavigateAsync(_home);
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var p = PathBox.Text.Trim();
        if (p.Length == 0) return;
        if (p == "~") p = _home;
        else if (p.StartsWith("~/")) p = Combine(_home, p[2..]);
        else if (!p.StartsWith('/')) p = Combine(_cwd, p);
        await NavigateAsync(p);
    }

    private void Cd_Click(object sender, RoutedEventArgs e) => ChangeDirectoryInTerminal?.Invoke(_cwd);

    private List<RemoteItem> Selected() => List.SelectedItems.Cast<RemoteItem>().Where(i => !i.IsParent).ToList();

    private async void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not RemoteItem item) return;
        await OpenItem(item);
    }

    private async Task OpenItem(RemoteItem item)
    {
        if (item.IsDirectory || item.IsParent) { await NavigateAsync(item.FullName); return; }
        if (item.IsLink)
        {
            // lien symbolique : dossier ou fichier ?
            var c = Client;
            if (c == null) return;
            bool isDir;
            try { isDir = await Task.Run(() => c.GetAttributes(item.FullName).IsDirectory); }
            catch { isDir = false; }
            if (isDir) { await NavigateAsync(item.FullName); return; }
        }
        await OpenForEdit(item);
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var sel = Selected();
        if (sel.Count == 1) await OpenItem(sel[0]);
    }

    private async void List_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter: if (List.SelectedItem is RemoteItem i) await OpenItem(i); e.Handled = true; break;
            case Key.Back: await NavigateAsync(ParentDir(_cwd)); e.Handled = true; break;
            case Key.Delete: Delete_Click(sender, e); e.Handled = true; break;
            case Key.F2: Rename_Click(sender, e); e.Handled = true; break;
            case Key.F5: await RefreshAsync(); e.Handled = true; break;
        }
    }

    // ---------- Opérations sur fichiers ----------

    private async Task RunOp(string label, Action<SftpClient> op)
    {
        var c = Client;
        if (c == null) return;
        StatusText.Text = label + "…";
        try
        {
            await Task.Run(() => op(c));
        }
        catch (Exception ex)
        {
            Dialogs.Warn(Window.GetWindow(this), label + " impossible", ex.Message);
        }
        await RefreshAsync();
    }

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptWindow.Ask(Window.GetWindow(this), "Nouveau dossier", $"Nom du dossier à créer dans {_cwd} :");
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunOp("Création du dossier", c => c.CreateDirectory(Combine(_cwd, name.Trim())));
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        var sel = Selected();
        if (sel.Count != 1) return;
        var item = sel[0];
        var name = PromptWindow.Ask(Window.GetWindow(this), "Renommer", $"Nouveau nom pour « {item.Name} » :", initial: item.Name);
        if (string.IsNullOrWhiteSpace(name) || name == item.Name) return;
        var target = name.StartsWith('/') ? name : Combine(ParentDir(item.FullName), name.Trim());
        await RunOp("Renommage", c => c.RenameFile(item.FullName, target));
    }

    private async void Chmod_Click(object sender, RoutedEventArgs e)
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        var mode = PromptWindow.Ask(Window.GetWindow(this), "Permissions",
            $"Nouveaux droits en octal (ex. 644, 755) pour {sel.Count} élément(s) :", initial: sel[0].Octal);
        if (mode == null) return;
        mode = mode.Trim();
        if (mode.Length != 3 || mode.Any(ch => ch < '0' || ch > '7'))
        {
            Dialogs.Warn(Window.GetWindow(this), "Droits invalides", "Format attendu : 3 chiffres de 0 à 7 (ex. 644, 755).");
            return;
        }
        var m = short.Parse(mode);
        await RunOp("Modification des droits", c => { foreach (var i in sel) c.ChangePermissions(i.FullName, m); });
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        
        if (!Dialogs.Confirm(Window.GetWindow(this), "Supprimer sur le serveur ?",
                (sel.Count == 1 ? $"« {sel[0].Name} » sera supprimé définitivement." : $"Ces {sel.Count} éléments seront supprimés définitivement.") + " Les dossiers sont supprimés avec tout leur contenu.",
                "Supprimer", "Annuler", DialogKind.Danger, danger: true))
            return;
        await RunOp("Suppression", c => { foreach (var i in sel) DeleteRecursive(c, i.FullName, i.IsDirectory); });
    }

    private static void DeleteRecursive(SftpClient c, string path, bool isDir)
    {
        if (!isDir) { c.DeleteFile(path); return; }
        foreach (var f in c.ListDirectory(path))
        {
            if (f.Name is "." or "..") continue;
            DeleteRecursive(c, f.FullName, f.IsDirectory);
        }
        c.DeleteDirectory(path);
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        var sel = Selected();
        var text = sel.Count > 0 ? string.Join("\n", sel.Select(s => s.FullName)) : _cwd;
        try { Clipboard.SetDataObject(text, true); } catch { }
    }

    // ---------- Transferts ----------

    private record Job(string Source, string Target, long Size, bool IsDir);

    private void UploadFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Title = "Fichiers à envoyer vers " + _cwd };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) _ = Upload(dlg.FileNames, _cwd);
    }

    private void UploadFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Multiselect = true, Title = "Dossier(s) à envoyer vers " + _cwd };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) _ = Upload(dlg.FolderNames, _cwd);
    }

    private void List_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && State == SessionState.Connected
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void List_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        var target = _cwd;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is RemoteItem { IsDirectory: true } dir)
            target = dir.FullName;
        _ = Upload(paths, target);
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        var sel = Selected();
        if (sel.Count == 0)
        {
            Dialogs.Info(Window.GetWindow(this), "Rien à télécharger", "Sélectionnez d'abord un ou plusieurs éléments.");
            return;
        }
        var dlg = new OpenFolderDialog { Title = "Dossier de destination", InitialDirectory = _lastDownloadDir };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        _lastDownloadDir = dlg.FolderName;
        await Download(sel, dlg.FolderName);
    }

    public async Task Upload(IEnumerable<string> localPaths, string remoteDir)
    {
        var paths = localPaths.ToList();
        var existing = (List.ItemsSource as IEnumerable<RemoteItem>)?.Select(i => i.Name).ToHashSet() ?? new();
        var clash = remoteDir == _cwd ? paths.Select(Path.GetFileName).Count(n => n != null && existing.Contains(n)) : 0;
        if (clash > 0 && !Dialogs.Confirm(Window.GetWindow(this), "Remplacer les fichiers ?",
                $"{clash} élément(s) portant le même nom existent déjà dans {remoteDir}.", "Remplacer", "Annuler", DialogKind.Warning))
            return;

        await RunTransfer("Envoi", c =>
        {
            var jobs = new List<Job>();
            foreach (var p in paths) PlanUpload(p, Combine(remoteDir, Path.GetFileName(p.TrimEnd('\\'))), jobs);
            return jobs;
        }, (c, job, ct) =>
        {
            if (job.IsDir)
            {
                if (!c.Exists(job.Target)) c.CreateDirectory(job.Target);
                return;
            }
            using var fs = new FileStream(job.Source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            using var ps = new ProgressStream(fs, n => Interlocked.Add(ref _done, n), ct);
            c.UploadFile(ps, job.Target, true);
        });
    }

    private static void PlanUpload(string local, string remote, List<Job> jobs)
    {
        if (Directory.Exists(local))
        {
            jobs.Add(new Job(local, remote, 0, true));
            foreach (var entry in Directory.EnumerateFileSystemEntries(local))
                PlanUpload(entry, Combine(remote, Path.GetFileName(entry)), jobs);
        }
        else if (File.Exists(local))
        {
            jobs.Add(new Job(local, remote, new FileInfo(local).Length, false));
        }
    }

    public async Task Download(IEnumerable<RemoteItem> items, string localDir)
    {
        var list = items.ToList();
        await RunTransfer("Téléchargement", c =>
        {
            var jobs = new List<Job>();
            foreach (var i in list) PlanDownload(c, i.FullName, i.IsDirectory, i.Size, Path.Combine(localDir, SafeName(i.Name)), jobs);
            return jobs;
        }, (c, job, ct) =>
        {
            if (job.IsDir) { Directory.CreateDirectory(job.Target); return; }
            var ok = false;
            try
            {
                using (var fs = new FileStream(job.Target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                using (var ps = new ProgressStream(fs, n => Interlocked.Add(ref _done, n), ct))
                    c.DownloadFile(job.Source, ps);
                ok = true;
            }
            finally
            {
                if (!ok) try { File.Delete(job.Target); } catch { }
            }
        });
    }

    private static string SafeName(string name)
    {
        foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        return name;
    }

    private static void PlanDownload(SftpClient c, string remote, bool isDir, long size, string local, List<Job> jobs)
    {
        if (!isDir)
        {
            var attrs = c.GetAttributes(remote); // suit les liens symboliques
            if (attrs.IsDirectory) isDir = true;
            else { jobs.Add(new Job(remote, local, attrs.Size, false)); return; }
        }
        jobs.Add(new Job(remote, local, 0, true));
        foreach (var f in c.ListDirectory(remote))
        {
            if (f.Name is "." or "..") continue;
            PlanDownload(c, f.FullName, f.IsDirectory, f.Length, Path.Combine(local, SafeName(f.Name)), jobs);
        }
    }

    private async Task RunTransfer(string verb, Func<SftpClient, List<Job>> plan, Action<SftpClient, Job, CancellationToken> exec)
    {
        var c = Client;
        if (c == null) return;
        if (_transferGate.CurrentCount == 0) StatusText.Text = "Transfert en attente…";
        await _transferGate.WaitAsync();
        var cts = _transferCts = new CancellationTokenSource();
        Interlocked.Exchange(ref _done, 0);
        _total = 0;
        _transferLabel = $"{verb} : préparation…";
        Progress.Value = 0;
        Progress.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Visible;
        _transferWatch.Restart();
        _progressTimer.Start();
        UpdateProgress();

        string? error = null;
        var files = 0;
        try
        {
            await Task.Run(() =>
            {
                var jobs = plan(c);
                _total = jobs.Sum(j => j.Size);
                var fileJobs = jobs.Count(j => !j.IsDir);
                foreach (var job in jobs)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    if (!job.IsDir)
                    {
                        files++;
                        _transferLabel = $"{verb} {files}/{fileJobs} : {Path.GetFileName(job.Source.Replace('/', '\\'))}";
                    }
                    exec(c, job, cts.Token);
                }
            }, cts.Token);
        }
        catch (OperationCanceledException) { error = "Transfert annulé."; }
        catch (Exception ex) when (ex.InnerException is OperationCanceledException) { error = "Transfert annulé."; }
        catch (Exception ex) { error = ex.Message; }
        finally
        {
            _progressTimer.Stop();
            Progress.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Collapsed;
            _transferCts = null;
            _transferGate.Release();
        }

        await RefreshAsync();
        var elapsed = _transferWatch.Elapsed.TotalSeconds;
        if (error == null)
            StatusText.Text = $"{verb} terminé : {files} fichier(s), {RemoteItem.FormatSize(_done)} en {elapsed:0.0} s";
        else
        {
            StatusText.Text = error;
            if (error != "Transfert annulé.")
                Dialogs.Warn(Window.GetWindow(this), verb + " interrompu", error);
        }
    }

    private void UpdateProgress()
    {
        var done = Interlocked.Read(ref _done);
        var secs = Math.Max(0.1, _transferWatch.Elapsed.TotalSeconds);
        Progress.Value = _total > 0 ? Math.Min(1, (double)done / _total) : 0;
        StatusText.Text = _total > 0
            ? $"{_transferLabel} — {RemoteItem.FormatSize(done)} / {RemoteItem.FormatSize(_total)} — {RemoteItem.FormatSize(done / secs)}/s"
            : _transferLabel;
    }

    private void CancelTransfer_Click(object sender, RoutedEventArgs e) => _transferCts?.Cancel();

    // ---------- Ouverture / édition à distance ----------

    private async Task OpenForEdit(RemoteItem item)
    {
        var dir = AppPaths.Ensure(Path.Combine(AppPaths.TempEdit, Guid.NewGuid().ToString("N")[..8]));
        var local = Path.Combine(dir, SafeName(item.Name));
        await Download(new[] { item }, dir);
        if (!File.Exists(local)) return;

        try { Process.Start(new ProcessStartInfo(local) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            // pas d'application associée : on propose le Bloc-notes
            try { Process.Start("notepad.exe", $"\"{local}\""); }
            catch { Dialogs.Error(Window.GetWindow(this), "Ouverture impossible", ex.Message); return; }
        }

        // Surveille les modifications pour proposer le renvoi sur le serveur
        var lastWrite = File.GetLastWriteTimeUtc(local);
        var remote = item.FullName;
        var watcher = new FileSystemWatcher(dir, Path.GetFileName(local)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size };
        var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        var asking = false;
        debounce.Tick += async (_, _) =>
        {
            debounce.Stop();
            if (asking || _disposed) return;
            DateTime now;
            try { now = File.GetLastWriteTimeUtc(local); } catch { return; }
            if (now == lastWrite) return;
            lastWrite = now;
            asking = true;
            var r = Dialogs.Confirm(Window.GetWindow(this), "Fichier modifié",
                $"« {item.Name} » a été modifié localement. Le renvoyer vers le serveur ?\n{remote}", "Renvoyer", "Ignorer");
            asking = false;
            if (!r) return;
            await RunTransfer("Envoi", _ => new List<Job> { new(local, remote, new FileInfo(local).Length, false) }, (c, job, ct) =>
            {
                using var fs = new FileStream(job.Source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var ps = new ProgressStream(fs, n => Interlocked.Add(ref _done, n), ct);
                c.UploadFile(ps, job.Target, true);
            });
        };
        watcher.Changed += (_, _) => Dispatcher.BeginInvoke(() => { debounce.Stop(); debounce.Start(); });
        watcher.EnableRaisingEvents = true;
        _watchers.Add(watcher);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _transferCts?.Cancel();
        foreach (var w in _watchers) w.Dispose();
        var c = _client;
        _client = null;
        if (c != null) Task.Run(c.Dispose);
    }
}
