using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Orbiter.Ui;

/// <summary>Petite boîte de saisie (texte ou mot de passe).</summary>
public class PromptWindow : Window
{
    private readonly TextBox? _text;
    private readonly PasswordBox? _pwd;

    private PromptWindow(string title, string message, bool password, string? initial, Window? owner)
    {
        Width = 420;
        SizeToContent = SizeToContent.Height;
        if (owner is { IsLoaded: true }) Owner = owner;

        var root = new DockPanel();

        var footer = new Border { CornerRadius = new CornerRadius(0, 0, 10, 10), Padding = new Thickness(20, 12, 20, 12) };
        footer.SetResourceReference(Border.BackgroundProperty, "DialogFooter");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Annuler", IsCancel = true, MinWidth = 90, Style = (Style)FindResource("SecondaryButton") };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), Style = (Style)FindResource("PrimaryButton") };
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        footer.Child = buttons;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var body = new StackPanel { Margin = new Thickness(20, 10, 20, 18) };
        var msg = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        msg.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        body.Children.Add(msg);

        Control input;
        if (password) input = _pwd = new PasswordBox();
        else input = _text = new TextBox { Text = initial ?? "" };
        input.Height = 32;
        body.Children.Add(input);
        root.Children.Add(body);

        Content = root;
        DialogChrome.Apply(this, title);

        Loaded += (_, _) =>
        {
            input.Focus();
            Keyboard.Focus(input);
            _text?.SelectAll();
        };
    }

    public static string? Ask(Window? owner, string title, string message, bool password = false, string? initial = null)
    {
        var w = new PromptWindow(title, message, password, initial, owner);
        if (w.ShowDialog() != true) return null;
        return password ? w._pwd!.Password : w._text!.Text;
    }
}
