using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Orbiter.Core;

namespace Orbiter.Ui;

/// <summary>En-tête de dossier dans la liste des sessions.</summary>
public sealed class FolderRow
{
    public string Name { get; init; } = "";
    public int Count { get; init; }
    public bool Collapsed { get; init; }
    public string Chevron => Collapsed ? "" : ""; // ▸ / ▾
    public override string ToString() => $"Dossier {Name}, {Count} session(s)" + (Collapsed ? ", replié" : "");
}

/// <summary>Dossiers de sessions : affichage groupé, repli, glisser-déposer, menus.</summary>
public partial class MainWindow
{
    private static AppSettings Settings => ThemeManager.Settings;

    /// <summary>Tous les dossiers connus : créés explicitement (même vides) ou utilisés par une session.</summary>
    private List<string> AllFolders() =>
        Settings.SessionFolders
            .Concat(_store.Sessions.Where(s => s.HasFolder).Select(s => s.Folder!))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private static bool SameFolder(string? a, string? b) =>
        string.Equals(string.IsNullOrWhiteSpace(a) ? null : a, string.IsNullOrWhiteSpace(b) ? null : b, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>Reconstruit la liste : sessions hors dossier d'abord, puis chaque dossier avec ses sessions.</summary>
    private void RefreshSessionList(SessionProfile? select = null)
    {
        var filter = SearchBox.Text.Trim();
        bool Match(SessionProfile s) => filter.Length == 0
            || s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || s.Host.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || s.Username.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || (s.Folder?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);
        var byName = StringComparer.CurrentCultureIgnoreCase;
        var matching = _store.Sessions.Where(Match).ToList();

        var items = new List<object>();
        items.AddRange(matching.Where(s => !s.HasFolder).OrderBy(s => s.Name, byName));
        foreach (var folder in AllFolders())
        {
            var inside = matching.Where(s => SameFolder(s.Folder, folder)).OrderBy(s => s.Name, byName).ToList();
            if (filter.Length > 0 && inside.Count == 0 && !folder.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            // pendant une recherche, les dossiers sont dépliés pour montrer les résultats
            var collapsed = filter.Length == 0 && Settings.CollapsedFolders.Contains(folder, byName);
            items.Add(new FolderRow { Name = folder, Count = inside.Count, Collapsed = collapsed });
            if (!collapsed) items.AddRange(inside);
        }
        SessionList.ItemsSource = items;
        if (select != null) SessionList.SelectedItem = items.OfType<SessionProfile>().FirstOrDefault(i => i.Id == select.Id);
    }

    private FolderRow? SelectedFolder => SessionList.SelectedItem as FolderRow;

    /// <summary>Dossier « courant » pour créer une session : dossier sélectionné, ou celui de la session sélectionnée.</summary>
    private string? CurrentFolder => SelectedFolder?.Name ?? SelectedProfile?.Folder;

    private void ToggleFolder(string folder)
    {
        var list = Settings.CollapsedFolders;
        var i = list.FindIndex(f => SameFolder(f, folder));
        if (i >= 0) list.RemoveAt(i); else list.Add(folder);
        Settings.Save();
        RefreshSessionList();
        SessionList.SelectedItem = SessionList.Items.OfType<FolderRow>().FirstOrDefault(r => SameFolder(r.Name, folder));
    }

    private void SetAllCollapsed(bool collapsed)
    {
        Settings.CollapsedFolders = collapsed ? AllFolders() : new();
        Settings.Save();
        RefreshSessionList();
    }

    // ---------- Créer / renommer / supprimer ----------

    /// <summary>Demande un nom de dossier valide ; null si annulé.</summary>
    private string? AskFolderName(string title, string? initial = null)
    {
        var message = "Nom du dossier :";
        while (true)
        {
            var name = PromptWindow.Ask(this, title, message, initial: initial)?.Trim();
            if (name == null) return null;
            if (name.Length == 0) { message = "Le nom ne peut pas être vide. Nom du dossier :"; continue; }
            if (name.Length > 60) { message = "60 caractères au maximum. Nom du dossier :"; initial = name[..60]; continue; }
            return name;
        }
    }

    private string? CreateFolder()
    {
        var name = AskFolderName("Nouveau dossier");
        if (name == null) return null;
        var existing = AllFolders().FirstOrDefault(f => SameFolder(f, name));
        if (existing != null) return existing;
        Settings.SessionFolders.Add(name);
        Settings.Save();
        RefreshSessionList();
        SessionList.SelectedItem = SessionList.Items.OfType<FolderRow>().FirstOrDefault(r => SameFolder(r.Name, name));
        return name;
    }

    private void NewFolder_Click(object sender, RoutedEventArgs e) => CreateFolder();

    private void RenameFolder(string folder)
    {
        var name = AskFolderName("Renommer le dossier", folder);
        if (name == null || name == folder) return;
        var other = AllFolders().FirstOrDefault(f => SameFolder(f, name) && !SameFolder(f, folder));
        if (other != null && !Dialogs.Confirm(this, "Fusionner les dossiers ?",
                $"Le dossier « {other} » existe déjà. Y déplacer les sessions de « {folder} » ?", "Fusionner", "Annuler")) return;
        var target = other ?? name;
        foreach (var s in _store.Sessions.Where(s => SameFolder(s.Folder, folder))) s.Folder = target;
        Settings.SessionFolders.RemoveAll(f => SameFolder(f, folder));
        if (!Settings.SessionFolders.Any(f => SameFolder(f, target))) Settings.SessionFolders.Add(target);
        if (Settings.CollapsedFolders.RemoveAll(f => SameFolder(f, folder)) > 0) Settings.CollapsedFolders.Add(target);
        Settings.Save();
        SaveStore();
        RefreshSessionList();
    }

    private void DeleteFolder(string folder)
    {
        var n = _store.Sessions.Count(s => SameFolder(s.Folder, folder));
        if (n > 0 && !Dialogs.Confirm(this, "Supprimer le dossier ?",
                $"Le dossier « {folder} » sera supprimé. Ses {n} session(s) sont conservées et placées hors dossier.",
                "Supprimer le dossier", "Annuler", DialogKind.Warning)) return;
        foreach (var s in _store.Sessions.Where(s => SameFolder(s.Folder, folder))) s.Folder = null;
        Settings.SessionFolders.RemoveAll(f => SameFolder(f, folder));
        Settings.CollapsedFolders.RemoveAll(f => SameFolder(f, folder));
        Settings.Save();
        SaveStore();
        RefreshSessionList();
    }

    private void MoveToFolder(SessionProfile p, string? folder)
    {
        var s = _store.Sessions.FirstOrDefault(x => x.Id == p.Id);
        if (s == null || SameFolder(s.Folder, folder)) return;
        s.Folder = string.IsNullOrWhiteSpace(folder) ? null : folder;
        // le dossier de destination est déplié pour voir la session arriver
        if (s.Folder != null) Settings.CollapsedFolders.RemoveAll(f => SameFolder(f, s.Folder));
        Settings.Save();
        SaveStore();
        RefreshSessionList(s);
    }

    // ---------- Souris : repli au clic, glisser-déposer ----------

    private Point _dragStart;
    private SessionProfile? _dragCandidate;
    private FolderRow? _clickedFolder;
    private bool _dragging;

    private static object? ItemAt(DependencyObject? source)
    {
        while (source != null && source is not ListBoxItem) source = VisualTreeHelper.GetParent(source);
        return (source as ListBoxItem)?.DataContext;
    }

    private void SessionList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(SessionList);
        var item = ItemAt(e.OriginalSource as DependencyObject);
        _dragCandidate = item as SessionProfile;
        _clickedFolder = item as FolderRow;
        _dragging = false;
    }

    private void SessionList_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_clickedFolder != null && !_dragging && ItemAt(e.OriginalSource as DependencyObject) is FolderRow f && f.Name == _clickedFolder.Name)
            ToggleFolder(f.Name);
        _clickedFolder = null;
        _dragCandidate = null;
    }

    private void SessionList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate == null) return;
        var d = e.GetPosition(SessionList) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragging = true;
        var p = _dragCandidate;
        _dragCandidate = null;
        DragDrop.DoDragDrop(SessionList, new DataObject(typeof(SessionProfile), p), DragDropEffects.Move);
    }

    /// <summary>Dossier visé par un dépôt : en-tête de dossier, session d'un dossier, ou hors dossier.</summary>
    private static string? DropFolder(DragEventArgs e) => ItemAt(e.OriginalSource as DependencyObject) switch
    {
        FolderRow f => f.Name,
        SessionProfile s => s.Folder,
        _ => null,
    };

    private void SessionList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetData(typeof(SessionProfile)) is SessionProfile p && !SameFolder(p.Folder, DropFolder(e))
            ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void SessionList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(SessionProfile)) is SessionProfile p) MoveToFolder(p, DropFolder(e));
    }

    // ---------- Menu contextuel ----------

    private void SessionList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var item = ItemAt(e.OriginalSource as DependencyObject);
        if (item != null) SessionList.SelectedItem = item;
        var menu = SessionList.ContextMenu!;
        menu.Items.Clear();
        MenuItem Item(string header, Action a, bool bold = false)
        {
            var mi = new MenuItem { Header = header };
            if (bold) mi.FontWeight = FontWeights.SemiBold;
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
            return mi;
        }

        switch (item)
        {
            case SessionProfile p:
                Item("Ouvrir", () => OpenSession(p, p.Kind), bold: true);
                Item("Ouvrir en SSH (terminal)", () => OpenSession(p, SessionKind.Ssh));
                Item("Ouvrir en SFTP (fichiers)", () => OpenSession(p, SessionKind.Sftp));
                menu.Items.Add(new Separator());
                Item("Modifier…", () => EditSession_Click(this, new RoutedEventArgs()));
                Item("Dupliquer", () => DuplicateSession_Click(this, new RoutedEventArgs()));
                var move = new MenuItem { Header = "Déplacer vers" };
                var none = new MenuItem { Header = "Hors dossier", IsCheckable = true, IsChecked = !p.HasFolder };
                none.Click += (_, _) => MoveToFolder(p, null);
                move.Items.Add(none);
                foreach (var f in AllFolders())
                {
                    var mi = new MenuItem { Header = f, IsCheckable = true, IsChecked = SameFolder(p.Folder, f) };
                    var target = f;
                    mi.Click += (_, _) => MoveToFolder(p, target);
                    move.Items.Add(mi);
                }
                move.Items.Add(new Separator());
                var nf = new MenuItem { Header = "Nouveau dossier…" };
                nf.Click += (_, _) => { if (CreateFolder() is { } f) MoveToFolder(p, f); };
                move.Items.Add(nf);
                menu.Items.Add(move);
                Item("Supprimer", () => DeleteSession_Click(this, new RoutedEventArgs()));
                break;

            case FolderRow f:
                Item(f.Collapsed ? "Déplier" : "Replier", () => ToggleFolder(f.Name), bold: true);
                Item("Nouvelle session dans ce dossier…", () => NewSessionIn(f.Name));
                menu.Items.Add(new Separator());
                Item("Renommer le dossier…", () => RenameFolder(f.Name));
                Item("Supprimer le dossier", () => DeleteFolder(f.Name));
                menu.Items.Add(new Separator());
                Item("Tout replier", () => SetAllCollapsed(true));
                Item("Tout déplier", () => SetAllCollapsed(false));
                break;

            default:
                Item("Nouvelle session…", () => NewSessionIn(null));
                Item("Nouveau dossier…", () => CreateFolder());
                if (AllFolders().Count > 0)
                {
                    menu.Items.Add(new Separator());
                    Item("Tout replier", () => SetAllCollapsed(true));
                    Item("Tout déplier", () => SetAllCollapsed(false));
                }
                break;
        }
    }
}
