using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Orbiter.Core;
using Renci.SshNet;
using Renci.SshNet.Common;

// Test de fumée des chemins SSH.NET utilisés par Orbiter.
var scratch = args[0];
var pwd = File.ReadAllText(Path.Combine(scratch, "testpw.txt")).Trim();
const string host = "127.0.0.1"; const int port = 2222; const string user = "novatest";
int failures = 0;
void Check(bool ok, string what) { Console.WriteLine((ok ? "[OK]   " : "[FAIL] ") + what); if (!ok) failures++; }

ConnectionInfo PasswordInfo()
{
    var kbd = new KeyboardInteractiveAuthenticationMethod(user);
    kbd.AuthenticationPrompt += (_, e) => { foreach (var p in e.Prompts) p.Response = pwd; };
    return new ConnectionInfo(host, port, user, new PasswordAuthenticationMethod(user, pwd), kbd) { Timeout = TimeSpan.FromSeconds(10) };
}

// 1. Shell + redimensionnement + fermeture
using (var client = new SshClient(PasswordInfo()))
{
    string? fp = null;
    client.HostKeyReceived += (_, e) => { fp = e.HostKeyName + " SHA256:" + e.FingerPrintSHA256; e.CanTrust = true; };
    client.Connect();
    Check(client.IsConnected && fp != null, $"Connexion SSH mot de passe ({client.ConnectionInfo.ServerVersion}, {fp})");

    var modes = new Dictionary<TerminalModes, uint> { { TerminalModes.ECHO, 1 }, { TerminalModes.IUTF8, 1 } };
    var shell = client.CreateShellStream("xterm-256color", 100, 30, 0, 0, 65536, modes);
    var sb = new StringBuilder();
    var closed = new ManualResetEventSlim();
    new Thread(() =>
    {
        var buf = new byte[32768];
        try { int n; while ((n = shell.Read(buf, 0, buf.Length)) > 0) lock (sb) sb.Append(Encoding.UTF8.GetString(buf, 0, n)); }
        catch (Exception ex) { Console.WriteLine("  read ex: " + ex.GetType().Name); }
        closed.Set();
    }) { IsBackground = true }.Start();

    bool WaitFor(string s, int ms = 5000)
    {
        var end = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < end) { lock (sb) if (sb.ToString().Contains(s)) return true; Thread.Sleep(50); }
        return false;
    }
    void Send(string s) { var b = Encoding.UTF8.GetBytes(s); shell.Write(b, 0, b.Length); shell.Flush(); }

    Send("echo TERM=$TERM; echo COLS=$(tput cols); echo é✓\r");
    Check(WaitFor("TERM=xterm-256color") && WaitFor("COLS=100"), "Shell interactif (TERM, largeur 100)");
    Check(WaitFor("é✓"), "UTF-8 aller-retour");
    shell.ChangeWindowSize(132, 40, 0, 0);
    Thread.Sleep(300);
    Send("echo COLS2=$(tput cols)\r");
    Check(WaitFor("COLS2=132"), "Redimensionnement (ChangeWindowSize → 132)");

    // 2. Tunnel local
    var fwd = new ForwardedPortLocal("127.0.0.1", 18022, "127.0.0.1", 2222);
    client.AddForwardedPort(fwd); fwd.Start();
    using (var tcp = new TcpClient("127.0.0.1", 18022))
    {
        tcp.ReceiveTimeout = 5000;
        var buf = new byte[64]; var n = tcp.GetStream().Read(buf, 0, buf.Length);
        Check(Encoding.ASCII.GetString(buf, 0, n).StartsWith("SSH-2.0"), "Tunnel local -L 18022 → 2222 (bannière SSH reçue)");
    }
    // 3. SOCKS dynamique
    var dyn = new ForwardedPortDynamic("127.0.0.1", 18080);
    client.AddForwardedPort(dyn); dyn.Start();
    using (var tcp = new TcpClient("127.0.0.1", 18080))
    {
        tcp.ReceiveTimeout = 5000;
        var s = tcp.GetStream();
        // SOCKS4 CONNECT 127.0.0.1:2222
        s.Write(new byte[] { 4, 1, 0x08, 0xAE, 127, 0, 0, 1, 0 });
        var resp = new byte[8]; var got = 0; while (got < 8) got += s.Read(resp, got, 8 - got);
        var banner = new byte[64]; var n = s.Read(banner, 0, banner.Length);
        Check(resp[1] == 0x5A && Encoding.ASCII.GetString(banner, 0, n).StartsWith("SSH-2.0"), "Tunnel SOCKS -D 18080");
    }

    Send("exit\r");
    Check(closed.Wait(5000), "Fin de session détectée (Read renvoie 0 après exit)");
}

// 4. Clés privées
var keyPath = Path.Combine(scratch, "nt_key");
var ppPath = Path.Combine(scratch, "nt_key_pp");
using (var c = new SshClient(new ConnectionInfo(host, port, user, new PrivateKeyAuthenticationMethod(user, new PrivateKeyFile(keyPath)))))
{
    c.HostKeyReceived += (_, e) => e.CanTrust = true;
    c.Connect();
    Check(c.IsConnected, "Authentification par clé ed25519");
}
try { _ = new PrivateKeyFile(ppPath); Check(false, "Clé chiffrée sans passphrase doit échouer"); }
catch (SshPassPhraseNullOrEmptyException) { Check(true, "Clé chiffrée sans passphrase → SshPassPhraseNullOrEmptyException"); }
try { _ = new PrivateKeyFile(ppPath, "mauvaise"); Check(false, "Mauvaise passphrase doit échouer"); }
catch (Exception ex) { Check(true, $"Mauvaise passphrase → {ex.GetType().Name}"); }
using (var c = new SshClient(new ConnectionInfo(host, port, user, new PrivateKeyAuthenticationMethod(user, new PrivateKeyFile(ppPath, "phrase123")))))
{
    c.HostKeyReceived += (_, e) => e.CanTrust = true;
    c.Connect();
    Check(c.IsConnected, "Authentification par clé RSA avec passphrase");
}
// mauvais mot de passe
try
{
    using var c = new SshClient(new ConnectionInfo(host, port, user, new PasswordAuthenticationMethod(user, "faux")) { Timeout = TimeSpan.FromSeconds(10) });
    c.HostKeyReceived += (_, e) => e.CanTrust = true;
    c.Connect();
    Check(false, "Mauvais mot de passe doit échouer");
}
catch (SshAuthenticationException) { Check(true, "Mauvais mot de passe → SshAuthenticationException"); }
// host key refusée
try
{
    using var c = new SshClient(PasswordInfo());
    c.HostKeyReceived += (_, e) => e.CanTrust = false;
    c.Connect();
    Check(false, "Empreinte refusée doit échouer");
}
catch (SshConnectionException ex) { Check(true, $"Empreinte refusée → {ex.Message}"); }

// 5. SFTP
using (var sftp = new SftpClient(PasswordInfo()))
{
    sftp.HostKeyReceived += (_, e) => e.CanTrust = true;
    sftp.Connect();
    Check(sftp.IsConnected, $"Connexion SFTP (home = {sftp.WorkingDirectory})");
    var dir = sftp.WorkingDirectory + "/nt_test";
    if (!sftp.Exists(dir)) sftp.CreateDirectory(dir);
    sftp.CreateDirectory(dir + "/sous dossier");

    var data = RandomNumberGenerator.GetBytes(8 * 1024 * 1024);
    var local = Path.Combine(Path.GetTempPath(), "nt_up.bin");
    File.WriteAllBytes(local, data);
    long done = 0;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    using (var ps = new ProgressStream(File.OpenRead(local), n => Interlocked.Add(ref done, n), CancellationToken.None))
        sftp.UploadFile(ps, dir + "/sous dossier/fichier é.bin", true);
    Check(done == data.Length, $"Upload 8 Mo via ProgressStream ({done} octets, {sw.ElapsedMilliseconds} ms)");

    done = 0;
    var back = Path.Combine(Path.GetTempPath(), "nt_down.bin");
    using (var ps = new ProgressStream(File.Create(back), n => Interlocked.Add(ref done, n), CancellationToken.None))
        sftp.DownloadFile(dir + "/sous dossier/fichier é.bin", ps);
    Check(File.ReadAllBytes(back).AsSpan().SequenceEqual(data), $"Download identique (SHA ok, {done} octets)");

    // Annulation
    var big = Path.Combine(Path.GetTempPath(), "nt_big.bin");
    File.WriteAllBytes(big, RandomNumberGenerator.GetBytes(64 * 1024 * 1024));
    var cts = new CancellationTokenSource();
    long bdone = 0;
    try
    {
        using var ps = new ProgressStream(File.OpenRead(big), n => { if (Interlocked.Add(ref bdone, n) > 4_000_000) cts.Cancel(); }, cts.Token);
        sftp.UploadFile(ps, dir + "/big.bin", true);
        Check(false, "L'upload annulé doit lever une exception");
    }
    catch (Exception ex)
    {
        Check(ex is OperationCanceledException || ex.InnerException is OperationCanceledException, $"Annulation d'upload → {ex.GetType().Name}");
    }
    var list = sftp.ListDirectory(dir).Select(f => f.Name).ToList();
    Check(sftp.IsConnected && list.Contains("sous dossier"), "Client SFTP toujours utilisable après annulation");

    sftp.ChangePermissions(dir + "/sous dossier/fichier é.bin", 640);
    var a = sftp.GetAttributes(dir + "/sous dossier/fichier é.bin");
    Check(a.OwnerCanRead && a.OwnerCanWrite && !a.OwnerCanExecute && a.GroupCanRead && !a.GroupCanWrite && !a.OthersCanRead, "chmod 640");

    sftp.RenameFile(dir + "/sous dossier/fichier é.bin", dir + "/renommé.bin");
    Check(sftp.Exists(dir + "/renommé.bin"), "Renommage");

    void Del(string p, bool isDir)
    {
        if (!isDir) { sftp.DeleteFile(p); return; }
        foreach (var f in sftp.ListDirectory(p)) if (f.Name is not ("." or "..")) Del(f.FullName, f.IsDirectory);
        sftp.DeleteDirectory(p);
    }
    Del(dir, true);
    Check(!sftp.Exists(dir), "Suppression récursive");
    File.Delete(local); File.Delete(back); File.Delete(big);
}

Console.WriteLine(failures == 0 ? "\nTOUS LES TESTS PASSENT" : $"\n{failures} ÉCHEC(S)");
return failures;
