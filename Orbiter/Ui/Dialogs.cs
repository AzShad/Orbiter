using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Orbiter.Ui;

/// <summary>Habillage moderne d'une fenêtre de dialogue : sans bordure Windows, coins arrondis, ombre, thème.</summary>
public static class DialogChrome
{
    private const double ShadowMargin = 18;

    public static void Apply(Window w, string? title)
    {
        var content = w.Content as UIElement;
        w.Content = null;

        w.WindowStyle = WindowStyle.None;
        w.AllowsTransparency = true;
        w.Background = Brushes.Transparent;
        w.ResizeMode = ResizeMode.NoResize;
        w.ShowInTaskbar = false;
        w.FontFamily = new FontFamily("Segoe UI");
        w.FontSize = 13;
        w.SetResourceReference(Control.ForegroundProperty, "Text");
        if (!double.IsNaN(w.Width)) w.Width += ShadowMargin * 2;
        if (w.Owner == null && Application.Current.MainWindow is { IsLoaded: true } mw && mw != w) w.Owner = mw;
        w.WindowStartupLocation = w.Owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;

        var card = new Border
        {
            Margin = new Thickness(ShadowMargin),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 26, ShadowDepth = 6, Direction = 270, Opacity = 0.28, Color = Colors.Black },
        };
        card.SetResourceReference(Border.BackgroundProperty, "DialogBg");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");

        var dock = new DockPanel { LastChildFill = true };
        if (title != null)
        {
            var header = new Grid { Margin = new Thickness(20, 12, 8, 0), Background = Brushes.Transparent };
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new TextBlock
            {
                Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            });
            var close = new Button
            {
                Content = "", FontSize = 10, ToolTip = "Fermer (Échap)",
                Style = (Style)w.FindResource("IconButton"), Padding = new Thickness(8), IsTabStop = false,
            };
            close.Click += (_, _) => w.Close();
            Grid.SetColumn(close, 1);
            header.Children.Add(close);
            DockPanel.SetDock(header, Dock.Top);
            dock.Children.Add(header);
            w.Title = title;
        }
        if (content != null) dock.Children.Add(content);
        card.Child = dock;
        w.Content = card;

        // déplacement de la fenêtre en tirant n'importe quelle zone vide
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is Border or Grid or DockPanel or StackPanel or TextBlock && e.ButtonState == MouseButtonState.Pressed)
                try { w.DragMove(); } catch { }
        };
        w.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { w.Close(); e.Handled = true; } };
    }
}

public enum DialogKind { Info, Question, Success, Warning, Danger }

public sealed record DialogButton(string Text, string Id, bool Primary = false, bool Danger = false, bool IsDefault = false);

/// <summary>Boîte de message moderne (remplace MessageBox).</summary>
public sealed class ModernDialog : Window
{
    public string? Result { get; private set; }

    private ModernDialog(string title, string message, DialogKind kind, UIElement? extra, DialogButton[] buttons)
    {
        Width = extra != null ? 520 : 440;
        SizeToContent = SizeToContent.Height;

        var (glyph, fg, bg) = kind switch
        {
            DialogKind.Success => ("", "Accent", "AccentSoft"),
            DialogKind.Question => ("", "Accent", "AccentSoft"),
            DialogKind.Warning => ("", "Warning", "WarningSoft"),
            DialogKind.Danger => ("", "Danger", "DangerSoft"),
            _ => ("", "Info", "InfoSoft"),
        };

        var root = new DockPanel();

        // pied de page avec les boutons
        var footer = new Border { CornerRadius = new CornerRadius(0, 0, 10, 10), Padding = new Thickness(20, 12, 20, 12) };
        footer.SetResourceReference(Border.BackgroundProperty, "DialogFooter");
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Button? focus = null;
        foreach (var b in buttons)
        {
            var btn = new Button
            {
                Content = b.Text,
                MinWidth = 90,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = b.IsDefault,
                Style = (Style)FindResource(b.Danger ? "DangerButton" : b.Primary ? "PrimaryButton" : "SecondaryButton"),
            };
            btn.Click += (_, _) => { Result = b.Id; DialogResult = true; };
            bar.Children.Add(btn);
            if (b.IsDefault || focus == null && b.Primary) focus = btn;
        }
        footer.Child = bar;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        // corps : icône + titre + texte
        var body = new Grid { Margin = new Thickness(22, 22, 22, 18) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition());

        var badge = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 16, 0) };
        badge.SetResourceReference(Border.BackgroundProperty, bg);
        var icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.ForegroundProperty, fg);
        badge.Child = icon;
        body.Children.Add(badge);

        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) });
        if (!string.IsNullOrEmpty(message))
        {
            var msg = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 19 };
            msg.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            text.Children.Add(msg);
        }
        if (extra != null)
        {
            if (extra is FrameworkElement fe) fe.Margin = new Thickness(0, 12, 0, 0);
            text.Children.Add(extra);
        }
        Grid.SetColumn(text, 1);
        body.Children.Add(text);
        root.Children.Add(body);

        Content = root;
        Title = title;
        DialogChrome.Apply(this, null);
        Loaded += (_, _) => focus?.Focus();
    }

    public static string? Show(Window? owner, string title, string message, DialogKind kind, UIElement? extra, params DialogButton[] buttons)
    {
        var d = new ModernDialog(title, message, kind, extra, buttons);
        var o = owner is { IsLoaded: true } ? owner : Application.Current?.MainWindow;
        if (o is { IsLoaded: true } && o != d) { d.Owner = o; d.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        d.ShowDialog();
        return d.Result;
    }
}

public static class Dialogs
{
    public static void Info(Window? owner, string title, string message) =>
        ModernDialog.Show(owner, title, message, DialogKind.Info, null, new DialogButton("OK", "ok", Primary: true, IsDefault: true));

    public static void Warn(Window? owner, string title, string message) =>
        ModernDialog.Show(owner, title, message, DialogKind.Warning, null, new DialogButton("OK", "ok", Primary: true, IsDefault: true));

    public static void Error(Window? owner, string title, string message) =>
        ModernDialog.Show(owner, title, message, DialogKind.Danger, null, new DialogButton("OK", "ok", Primary: true, IsDefault: true));

    public static bool Confirm(Window? owner, string title, string message, string yes = "Oui", string no = "Annuler",
        DialogKind kind = DialogKind.Question, bool danger = false) =>
        ModernDialog.Show(owner, title, message, kind, null,
            new DialogButton(no, "no"),
            new DialogButton(yes, "yes", Primary: !danger, Danger: danger, IsDefault: !danger)) == "yes";

    /// <summary>Bloc « clé : valeur » à police fixe, avec bouton copier, pour les empreintes.</summary>
    public static UIElement Details(params (string Label, string Value, bool Mono)[] rows)
    {
        var box = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 10, 12, 10), BorderThickness = new Thickness(1) };
        box.SetResourceReference(Border.BackgroundProperty, "CodeBg");
        box.SetResourceReference(Border.BorderBrushProperty, "Border");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = rows[i].Label, Margin = new Thickness(0, 3, 14, 3), FontSize = 12, VerticalAlignment = VerticalAlignment.Top };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            Grid.SetRow(label, i);
            grid.Children.Add(label);
            var value = new TextBox
            {
                Text = rows[i].Value, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0),
                Margin = new Thickness(0, 3, 0, 3), FontSize = rows[i].Mono ? 12.5 : 13,
                FontFamily = rows[i].Mono ? new FontFamily("Cascadia Mono, Consolas") : new FontFamily("Segoe UI"),
                Template = SimpleTextTemplate(),
            };
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);
        }
        box.Child = grid;
        return box;
    }

    private static ControlTemplate SimpleTextTemplate()
    {
        var t = new ControlTemplate(typeof(TextBox));
        var sv = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        sv.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        sv.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        t.VisualTree = sv;
        return t;
    }
}
