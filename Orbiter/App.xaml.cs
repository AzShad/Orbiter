using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Orbiter.Core;

namespace Orbiter;

public partial class App : Application
{
    private static Task<CoreWebView2Environment>? _webEnv;

    /// <summary>Environnement WebView2 partagé par tous les terminaux.</summary>
    public static Task<CoreWebView2Environment> WebEnvironment =>
        _webEnv ??= CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewData);

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        ExtractWebAssets();
        Updater.CleanupOld(e.Args);
        Updater.CleanupDownloads();
        base.OnStartup(e);
        ThemeManager.Initialize();
        var main = new Ui.MainWindow();
        MainWindow = main;
        main.Show();
    }

    /// <summary>Copie les fichiers du terminal (xterm.js…) embarqués dans l'exe vers %LOCALAPPDATA%.</summary>
    private static void ExtractWebAssets()
    {
        var asm = Assembly.GetExecutingAssembly();
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith("web/")))
        {
            var target = Path.Combine(AppPaths.WebRoot, res["web/".Length..]);
            using var src = asm.GetManifestResourceStream(res)!;
            using var ms = new MemoryStream();
            src.CopyTo(ms);
            var bytes = ms.ToArray();
            if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) continue;
            File.WriteAllBytes(target, bytes);
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Ui.Dialogs.Error(MainWindow, "Une erreur est survenue", e.Exception.Message);
        e.Handled = true;
    }
}
