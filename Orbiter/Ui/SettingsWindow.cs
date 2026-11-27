using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Orbiter.Core;

namespace Orbiter.Ui;

/// <summary>Paramètres d'Orbiter : commandes enregistrées.</summary>
public sealed class SettingsWindow : Window
{
    private readonly ObservableCollection<SavedCommand> _commands;
    private readonly ListBox _list;

    private SettingsWindow(Window? owner)
    {
        Width = 640;
        SizeToContent = SizeToContent.Height;
        if (owner is { IsLoaded: true }) Owner = owner;
        _commands = new ObservableCollection<SavedCommand>(ThemeManager.Settings.CommandList.Select(c => c.Clone()));

        var root = new DockPanel();

        // pied de page
        var footer = new Border { CornerRadius = new CornerRadius(0, 0, 10, 10), Padding = new Thickness(20, 12, 20, 12) };
        footer.SetResourceReference(Border.BackgroundProperty, "DialogFooter");
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Annuler", IsCancel = true, MinWidth = 90, Style = (Style)FindResource("SecondaryButton") };
        var save = new Button { Content = "Enregistrer", IsDefault = true, MinWidth = 110, Margin = new Thickness(8, 0, 0, 0), Style = (Style)FindResource("PrimaryButton") };
        save.Click += (_, _) => Save();
        bar.Children.Add(cancel);
        bar.Children.Add(save);
        footer.Child = bar;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var body = new StackPanel { Margin = new Thickness(20, 4, 20, 16) };
        var title = new TextBlock { Text = "COMMANDES ENREGISTRÉES", FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 6, 0, 4) };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        body.Children.Add(title);
        var help = new TextBlock
        {
            Text = "Elles apparaissent dans le menu « Commandes » (bouton ▶ de la barre du haut) et s'envoient au terminal actif, " +
                   "ou à tous les terminaux connectés.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 10), FontSize = 12,
        };
        help.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        body.Children.Add(help);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var card = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Height = 300, Padding = new Thickness(2) };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        _list = new ListBox
        {
            ItemsSource = _commands, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            ItemTemplate = ItemTemplate(),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.MouseDoubleClick += (_, e) => { if (_list.SelectedItem != null) Edit(); };
        _list.KeyDown += (_, e) => { if (e.Key == Key.Delete) Remove(); };
        card.Child = _list;
        grid.Children.Add(card);

        var side = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        Button SideButton(string text, Action a, bool primary = false)
        {
            var b = new Button { Content = text, MinWidth = 110, Margin = new Thickness(0, 0, 0, 6), Style = (Style)FindResource(primary ? "PrimaryButton" : "SecondaryButton") };
            b.Click += (_, _) => a();
            side.Children.Add(b);
            return b;
        }
        SideButton("Ajouter…", Add, primary: true);
        var edit = SideButton("Modifier…", Edit);
        var del = SideButton("Supprimer", Remove);
        var up = SideButton("Monter", () => Move(-1));
        var down = SideButton("Descendre", () => Move(1));
        var reset = new Button { Content = "Exemples", MinWidth = 110, Margin = new Thickness(0, 14, 0, 0), Style = (Style)FindResource("SecondaryButton"),
            ToolTip = "Ajouter les commandes d'exemple" };
        reset.Click += (_, _) => { foreach (var c in SavedCommand.Examples()) _commands.Add(c); };
        side.Children.Add(reset);
        void Refresh()
        {
            var i = _list.SelectedIndex;
            edit.IsEnabled = del.IsEnabled = i >= 0;
            up.IsEnabled = i > 0;
            down.IsEnabled = i >= 0 && i < _commands.Count - 1;
        }
        _list.SelectionChanged += (_, _) => Refresh();
        _commands.CollectionChanged += (_, _) => Refresh();
        Refresh();
        Grid.SetColumn(side, 1);
        grid.Children.Add(side);
        body.Children.Add(grid);
        root.Children.Add(body);

        Content = root;
        DialogChrome.Apply(this, "Paramètres");
    }

    private static DataTemplate ItemTemplate()
    {
        var t = new DataTemplate(typeof(SavedCommand));
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(MarginProperty, new Thickness(4, 3, 4, 3));
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(SavedCommand.Name)));
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        panel.AppendChild(name);
        var cmd = new FrameworkElementFactory(typeof(TextBlock));
        cmd.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(SavedCommand.Preview)));
        cmd.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Cascadia Mono, Consolas"));
        cmd.SetValue(TextBlock.FontSizeProperty, 12.0);
        cmd.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        cmd.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        panel.AppendChild(cmd);
        t.VisualTree = panel;
        return t;
    }

    private void Add()
    {
        var c = CommandEditorWindow.Edit(this, null);
        if (c == null) return;
        _commands.Add(c);
        _list.SelectedItem = c;
    }

    private void Edit()
    {
        if (_list.SelectedItem is not SavedCommand cur) return;
        var c = CommandEditorWindow.Edit(this, cur);
        if (c == null) return;
        var i = _commands.IndexOf(cur);
        _commands[i] = c;
        _list.SelectedIndex = i;
    }

    private void Remove()
    {
        if (_list.SelectedItem is not SavedCommand cur) return;
        var i = _commands.IndexOf(cur);
        _commands.RemoveAt(i);
        if (_commands.Count > 0) _list.SelectedIndex = Math.Min(i, _commands.Count - 1);
    }

    private void Move(int delta)
    {
        var i = _list.SelectedIndex;
        var j = i + delta;
        if (i < 0 || j < 0 || j >= _commands.Count) return;
        _commands.Move(i, j);
        _list.SelectedIndex = j;
    }

    private void Save()
    {
        ThemeManager.Settings.Commands = _commands.ToList();
        ThemeManager.Settings.Save();
        DialogResult = true;
    }

    public static bool Open(Window? owner) => new SettingsWindow(owner).ShowDialog() == true;
}

/// <summary>Création / modification d'une commande enregistrée.</summary>
public sealed class CommandEditorWindow : Window
{
    private readonly TextBox _name = new() { Height = 30 };
    private readonly TextBox _cmd = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 110, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"), Padding = new Thickness(6, 5, 6, 5),
        VerticalContentAlignment = VerticalAlignment.Top,
    };
    private readonly CheckBox _run = new() { Content = "Exécuter immédiatement (appuie sur Entrée)", Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock _error = new() { Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private SavedCommand? _result;

    private CommandEditorWindow(Window owner, SavedCommand? cmd)
    {
        Width = 520;
        SizeToContent = SizeToContent.Height;
        Owner = owner;

        var root = new DockPanel();
        var footer = new Border { CornerRadius = new CornerRadius(0, 0, 10, 10), Padding = new Thickness(20, 12, 20, 12) };
        footer.SetResourceReference(Border.BackgroundProperty, "DialogFooter");
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Annuler", IsCancel = true, MinWidth = 90, Style = (Style)FindResource("SecondaryButton") };
        var ok = new Button { Content = "OK", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), Style = (Style)FindResource("PrimaryButton") };
        ok.Click += (_, _) => Validate();
        bar.Children.Add(cancel);
        bar.Children.Add(ok);
        footer.Child = bar;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var body = new StackPanel { Margin = new Thickness(20, 6, 20, 16) };
        body.Children.Add(new TextBlock { Text = "Nom", Margin = new Thickness(0, 0, 0, 4) });
        body.Children.Add(_name);
        body.Children.Add(new TextBlock { Text = "Commande", Margin = new Thickness(0, 12, 0, 4) });
        body.Children.Add(_cmd);
        var hint = new TextBlock
        {
            Text = "Plusieurs lignes possibles : chacune est exécutée à la suite. Décochez « Exécuter immédiatement » " +
                   "pour seulement coller la commande et la relire avant d'appuyer sur Entrée.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 6, 0, 0),
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        body.Children.Add(hint);
        body.Children.Add(_run);
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        body.Children.Add(_error);
        root.Children.Add(body);

        _name.Text = cmd?.Name ?? "";
        _cmd.Text = cmd?.Command ?? "";
        _run.IsChecked = cmd?.RunImmediately ?? true;

        Content = root;
        DialogChrome.Apply(this, cmd == null ? "Nouvelle commande" : "Modifier la commande");
        Loaded += (_, _) => { _name.Focus(); Keyboard.Focus(_name); };
        // Entrée valide depuis le nom ; dans la commande, Entrée ajoute une ligne
        _name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Validate(); e.Handled = true; } };
    }

    private void Validate()
    {
        var name = _name.Text.Trim();
        var command = _cmd.Text.Replace("\r\n", "\n").Trim('\n', ' ');
        string? err = command.Length == 0 ? "Saisissez une commande." : null;
        if (err != null)
        {
            _error.Text = err;
            _error.Visibility = Visibility.Visible;
            return;
        }
        if (name.Length == 0) name = command.Split('\n')[0];
        _result = new SavedCommand { Name = name, Command = command, RunImmediately = _run.IsChecked == true };
        DialogResult = true;
    }

    public static SavedCommand? Edit(Window owner, SavedCommand? cmd)
    {
        var w = new CommandEditorWindow(owner, cmd);
        return w.ShowDialog() == true ? w._result : null;
    }
}
