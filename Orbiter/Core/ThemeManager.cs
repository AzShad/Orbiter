using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Orbiter.Core;

public enum ThemeChoice { System, Light, Dark }

public class AppSettings
{
    private static readonly string FilePath = Path.Combine(AppPaths.Roaming, "settings.json");
    public ThemeChoice Theme { get; set; } = ThemeChoice.System;
    /// <summary>Version que l'utilisateur a choisi d'ignorer (« Ignorer cette version »).</summary>
    public string? SkippedVersion { get; set; }
    /// <summary>Commandes enregistrées ; null = jamais configurées (exemples proposés).</summary>
    public List<SavedCommand>? Commands { get; set; }

    /// <summary>Dossiers de sessions créés (y compris vides) et dossiers repliés dans la liste.</summary>
    public List<string> SessionFolders { get; set; } = new();
    public List<string> CollapsedFolders { get; set; } = new();

    [JsonIgnore]
    public List<SavedCommand> CommandList => Commands ??= SavedCommand.Examples();

    public static AppSettings Load() => Json.Load<AppSettings>(FilePath);
    public void Save() { try { Json.Save(FilePath, this); } catch { } }
}

/// <summary>Bascule entre les palettes claire et sombre, y compris la barre de titre Windows.</summary>
public static class ThemeManager
{
    public static AppSettings Settings { get; } = AppSettings.Load();
    public static bool IsDark { get; private set; }
    public static event Action? Changed;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    public static void Initialize()
    {
        Apply(Settings.Theme);
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && Settings.Theme == ThemeChoice.System)
                Application.Current.Dispatcher.BeginInvoke(() => Apply(ThemeChoice.System));
        };
    }

    public static void Set(ThemeChoice choice)
    {
        Settings.Theme = choice;
        Settings.Save();
        Apply(choice);
    }

    private static void Apply(ThemeChoice choice)
    {
        var dark = choice switch
        {
            ThemeChoice.Dark => true,
            ThemeChoice.Light => false,
            _ => SystemPrefersDark(),
        };
        IsDark = dark;
        var dicts = Application.Current.Resources.MergedDictionaries;
        dicts[0] = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Orbiter;component/Theme/{(dark ? "Dark" : "Light")}.xaml"),
        };
        foreach (Window w in Application.Current.Windows) ApplyTitleBar(w);
        Changed?.Invoke();
    }

    /// <summary>Barre de titre sombre (Windows 10 20H1+ / 11).</summary>
    public static void ApplyTitleBar(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero)
        {
            w.SourceInitialized += (_, _) => ApplyTitleBar(w);
            return;
        }
        var v = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref v, sizeof(int));
    }
}
