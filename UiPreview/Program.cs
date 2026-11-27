using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Orbiter.Ui;

namespace UiPreview;

/// <summary>Affiche chaque écran de Orbiter en clair et en sombre et l'enregistre en PNG (aucun serveur nécessaire).</summary>
public static class Program
{
    private static string _out = "";

    [STAThread]
    public static void Main(string[] args)
    {
        _out = args[0];
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(Dict("Light"));
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Orbiter;component/Theme/Controls.xaml") });
        app.Startup += async (_, _) =>
        {
            try { await RunAll(app); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(_out, "preview_error.txt"), ex.ToString()); }
            app.Shutdown();
        };
        app.Run();
    }

    private static ResourceDictionary Dict(string name) =>
        new() { Source = new Uri($"pack://application:,,,/Orbiter;component/Theme/{name}.xaml") };

    private static async Task RunAll(Application app)
    {
        foreach (var theme in new[] { "Light", "Dark" })
        {
            app.Resources.MergedDictionaries[0] = Dict(theme);
            var main = new MainWindow { Width = 1100, Height = 700 };
            app.MainWindow = main;
            main.Show();
            await Task.Delay(900);
            Render(main, $"{theme}_main.png");

            await Modal(theme + "_prompt", () => PromptWindow.Ask(main, "Mot de passe", "Mot de passe pour novatest@serveur :", password: true));
            await Modal(theme + "_hostkey_new", () => HostKeyDialog.AskNew(main, "srv-prod.exemple.fr", 22, "ssh-ed25519", "Bocnf8F9cJ60ma0XHvKhfsAkEjFPXgWJrZvycYy0qVA"));
            await Modal(theme + "_hostkey_changed", () => HostKeyDialog.AskChanged(main, "srv-prod.exemple.fr", 22,
                "ssh-ed25519", "Bocnf8F9cJ60ma0XHvKhfsAkEjFPXgWJrZvycYy0qVA", "ssh-rsa", "q1Z9xv3Lw0mH7eTtYp2c5kR8sNfB4uJdGaVoXiEyCzM"));
            await Modal(theme + "_confirm_delete", () => Dialogs.Confirm(main, "Supprimer la session ?",
                "La session « Serveur web » sera retirée de la liste. Les fichiers du serveur ne sont pas touchés.",
                "Supprimer", "Annuler", DialogKind.Danger, danger: true));
            await Modal(theme + "_saved", () => Dialogs.Confirm(main, "Session enregistrée",
                "La session « Serveur web » a été ajoutée. Se connecter maintenant ?", "Se connecter", "Plus tard", DialogKind.Success));
            await Modal(theme + "_editor", () => SessionEditorWindow.Edit(main, null));
            main.Close();
        }
    }

    private static Task Modal(string name, Action open)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var w = Application.Current.Windows.OfType<Window>().LastOrDefault(x => x != Application.Current.MainWindow);
            if (w != null) { Render(w, name + ".png"); w.Close(); }
        };
        timer.Start();
        open();
        return Task.Delay(200);
    }

    private static void Render(Window w, string file)
    {
        var root = (FrameworkElement)w.Content;
        var width = (int)Math.Ceiling(root.ActualWidth);
        var height = (int)Math.Ceiling(root.ActualHeight);
        var rtb = new RenderTargetBitmap((int)(width * 1.25), (int)(height * 1.25), 120, 120, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            // fond neutre derrière les fenêtres transparentes pour voir l'ombre
            dc.DrawRectangle(w.AllowsTransparency ? new SolidColorBrush(Color.FromRgb(0x80, 0x84, 0x8C)) : w.Background, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, width, height));
        }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(Path.Combine(_out, file));
        enc.Save(fs);
    }
}
