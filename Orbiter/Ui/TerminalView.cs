using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Orbiter.Core;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Orbiter.Ui;

public enum SessionState { Idle, Connecting, Connected, Disconnected }

/// <summary>Terminal SSH : xterm.js dans WebView2 relié à un ShellStream SSH.NET.</summary>
public sealed class TerminalView : Grid, IDisposable
{
    private readonly SessionProfile _profile;
    private readonly WebView2 _web = new();
    private SshClient? _client;
    private ShellStream? _shell;
    private int _generation;
    private uint _cols = 120, _rows = 30;
    private bool _webReady, _initStarted, _disposed;
    private readonly List<string> _preReady = new();

    private readonly object _outLock = new();
    private MemoryStream _pending = new();
    private bool _flushScheduled;

    public SessionState State { get; private set; } = SessionState.Idle;
    public event Action<SessionState>? StateChanged;
    /// <summary>Déclenché à chaque connexion réussie.</summary>
    public event Action? Connected;

    public TerminalView(SessionProfile profile)
    {
        _profile = profile;
        _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 0x16, 0x18, 0x1d);
        Children.Add(_web);
        Loaded += async (_, _) => await InitWebAsync();
    }

    private async Task InitWebAsync()
    {
        if (_initStarted) return;
        _initStarted = true;
        try
        {
            var env = await App.WebEnvironment;
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            Dialogs.Error(Window.GetWindow(this), "Terminal indisponible",
                "Impossible d'initialiser le moteur du terminal (WebView2). " +
                "Installez « Microsoft Edge WebView2 Runtime » depuis le site de Microsoft.\n\n" + ex.Message);
            return;
        }
        if (_disposed) return;
        var core = _web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.SetVirtualHostNameToFolderMapping("orbiter.local", AppPaths.WebRoot, CoreWebView2HostResourceAccessKind.Allow);
        core.WebMessageReceived += OnWebMessage;
        core.Navigate("https://orbiter.local/terminal.html");
    }

    // ---------- Messages JS -> C# ----------

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var m = doc.RootElement;
        switch (m.GetProperty("t").GetString())
        {
            case "ready":
                _cols = m.GetProperty("cols").GetUInt32();
                _rows = m.GetProperty("rows").GetUInt32();
                _webReady = true;
                foreach (var s in _preReady) PostText(s);
                _preReady.Clear();
                if (State == SessionState.Idle) Connect();
                break;
            case "in":
                OnInput(m.GetProperty("d").GetString() ?? "");
                break;
            case "resize":
                _cols = m.GetProperty("cols").GetUInt32();
                _rows = m.GetProperty("rows").GetUInt32();
                try { _shell?.ChangeWindowSize(_cols, _rows, 0, 0); } catch { }
                break;
            case "copy":
                var text = m.GetProperty("d").GetString();
                if (!string.IsNullOrEmpty(text)) TrySetClipboard(text);
                break;
            case "paste":
                string? clip = null;
                try { if (Clipboard.ContainsText()) clip = Clipboard.GetText(); } catch { }
                if (!string.IsNullOrEmpty(clip)) Post(new { t = "paste", d = clip });
                break;
        }
    }

    private static void TrySetClipboard(string text)
    {
        for (var i = 0; i < 5; i++)
        {
            try { Clipboard.SetDataObject(text, true); return; }
            catch { Thread.Sleep(20); }
        }
    }

    private void OnInput(string data)
    {
        if (State == SessionState.Connected && _shell != null)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(data);
                _shell.Write(bytes, 0, bytes.Length);
                _shell.Flush();
            }
            catch (Exception ex) { OnShellClosed(_generation, ex); }
        }
        else if (State == SessionState.Disconnected && (data == "r" || data == "R"))
        {
            Connect();
        }
    }

    // ---------- Envoi C# -> JS ----------

    private void Post(object msg)
    {
        if (_disposed || _web.CoreWebView2 == null) return;
        try { _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(msg)); } catch { }
    }

    private void PostText(string s)
    {
        if (!_webReady) { _preReady.Add(s); return; }
        Post(new { t = "text", d = s });
    }

    private void Info(string s) => PostText($"\x1b[38;5;43m{s}\x1b[0m\r\n");
    private void Warn(string s) => PostText($"\x1b[33m{s}\x1b[0m\r\n");
    private void Error(string s) => PostText($"\x1b[31m{s}\x1b[0m\r\n");

    /// <summary>Envoie du texte au shell distant comme si l'utilisateur l'avait tapé.</summary>
    public void SendInput(string text)
    {
        OnInput(text);
        FocusTerminal();
    }

    public void FocusTerminal()
    {
        _web.Focus();
        Post(new { t = "focus" });
    }

    private void Enqueue(byte[] buf, int n)
    {
        lock (_outLock)
        {
            _pending.Write(buf, 0, n);
            if (_flushScheduled) return;
            _flushScheduled = true;
        }
        Dispatcher.BeginInvoke(Flush, DispatcherPriority.Background);
    }

    private void Flush()
    {
        byte[] data;
        lock (_outLock)
        {
            data = _pending.ToArray();
            _pending = new MemoryStream();
            _flushScheduled = false;
        }
        if (data.Length > 0) Post(new { t = "out", d = Convert.ToBase64String(data) });
    }

    // ---------- Connexion SSH ----------

    private void SetState(SessionState s)
    {
        State = s;
        StateChanged?.Invoke(s);
    }

    public async void Connect()
    {
        if (_disposed || State is SessionState.Connecting or SessionState.Connected) return;
        if (!_webReady) return; // la connexion démarrera quand le terminal sera prêt

        ConnectionInfo? ci;
        try
        {
            ci = SshConnector.Prepare(_profile, Window.GetWindow(this));
        }
        catch (Exception ex)
        {
            Error(ex.Message);
            SetState(SessionState.Disconnected);
            Warn("[Appuyez sur R pour réessayer]");
            return;
        }
        if (ci == null)
        {
            Warn("Connexion annulée. [Appuyez sur R pour réessayer]");
            SetState(SessionState.Disconnected);
            return;
        }

        SetState(SessionState.Connecting);
        Info($"Connexion à {_profile.Host}:{_profile.Port} en tant que {_profile.Username}…");

        var client = new SshClient(ci) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
        SshConnector.AttachHostKeyCheck(client, _profile.Host, _profile.Port, Dispatcher);
        ShellStream shell;
        try
        {
            var cols = _cols; var rows = _rows;
            shell = await Task.Run(() =>
            {
                client.Connect();
                var modes = new Dictionary<TerminalModes, uint> { { TerminalModes.ECHO, 1 }, { TerminalModes.IUTF8, 1 } };
                return client.CreateShellStream("xterm-256color", cols, rows, 0, 0, 65536, modes);
            });
        }
        catch (Exception ex)
        {
            client.Dispose();
            if (_disposed) return;
            Error("Échec : " + SshConnector.Describe(ex));
            if (ex is SshAuthenticationException)
            {
                // on oublie ce qui a été saisi pour redemander au prochain essai
                _profile.RuntimePassword = null;
                _profile.RuntimePassphrase = null;
                _profile.IgnoreStoredSecrets = true;
            }
            SetState(SessionState.Disconnected);
            Warn("[Appuyez sur R pour réessayer]");
            return;
        }

        if (_disposed) { shell.Dispose(); client.Dispose(); return; }

        _client = client;
        _shell = shell;
        var gen = ++_generation;
        client.ErrorOccurred += (_, e) => Dispatcher.BeginInvoke(() => OnShellClosed(gen, e.Exception));

        Info($"Connecté ({client.ConnectionInfo.ServerVersion}).");
        StartTunnels(client);
        SetState(SessionState.Connected);

        var reader = new Thread(() => ReadLoop(shell, gen)) { IsBackground = true, Name = "ssh-read" };
        reader.Start();
        Connected?.Invoke();
        FocusTerminal();
    }

    private void ReadLoop(ShellStream shell, int gen)
    {
        var buf = new byte[32 * 1024];
        Exception? error = null;
        try
        {
            while (true)
            {
                var n = shell.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                Enqueue(buf, n);
            }
        }
        catch (Exception ex) { error = ex; }
        Dispatcher.BeginInvoke(() => OnShellClosed(gen, error));
    }

    private void OnShellClosed(int gen, Exception? error)
    {
        if (gen != _generation || State != SessionState.Connected) return;
        Flush();
        var client = _client; var shell = _shell;
        _client = null; _shell = null;
        Task.Run(() => { try { shell?.Dispose(); } catch { } try { client?.Dispose(); } catch { } });
        if (_disposed) return;
        PostText("\r\n");
        if (error != null && error is not ObjectDisposedException) Error("Connexion perdue : " + SshConnector.Describe(error));
        Warn("[Session terminée — appuyez sur R pour vous reconnecter]");
        SetState(SessionState.Disconnected);
    }

    private void StartTunnels(SshClient client)
    {
        foreach (var t in _profile.Tunnels)
        {
            try
            {
                ForwardedPort fp = t.Kind switch
                {
                    TunnelKind.Local => new ForwardedPortLocal(t.BindHost, t.BindPort, t.DestHost, t.DestPort),
                    TunnelKind.Remote => new ForwardedPortRemote(t.BindHost, t.BindPort, t.DestHost, t.DestPort),
                    _ => new ForwardedPortDynamic(t.BindHost, t.BindPort),
                };
                var label = t.Display;
                fp.Exception += (_, e) => Dispatcher.BeginInvoke(() =>
                    Warn($"\r\n[tunnel {label}] {e.Exception.Message}"));
                client.AddForwardedPort(fp);
                fp.Start();
                Info($"Tunnel actif : {label}");
            }
            catch (Exception ex)
            {
                Error($"Tunnel {t.Display} impossible : {ex.Message}");
            }
        }
    }

    public void Reconnect()
    {
        if (State == SessionState.Connected)
        {
            _generation++;
            var client = _client; var shell = _shell;
            _client = null; _shell = null;
            Task.Run(() => { try { shell?.Dispose(); } catch { } try { client?.Dispose(); } catch { } });
            SetState(SessionState.Disconnected);
            PostText("\r\n");
        }
        Connect();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _generation++;
        var client = _client; var shell = _shell;
        _client = null; _shell = null;
        Task.Run(() => { try { shell?.Dispose(); } catch { } try { client?.Dispose(); } catch { } });
        try { _web.Dispose(); } catch { }
    }
}
