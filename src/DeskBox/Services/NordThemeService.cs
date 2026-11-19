using DeskBox.Models;
using Microsoft.Win32;
using Application = System.Windows.Application;
using ResourceDictionary = System.Windows.ResourceDictionary;

namespace DeskBox.Services;

/// <summary>
/// Manages Nord Frost UI theme dictionaries (Snow Storm Light / Polar Night Dark)
/// with instant runtime switching via WPF DynamicResource.
/// </summary>
public static class NordThemeService
{
    private static readonly Uri LightThemeUri = new("pack://application:,,,/Themes/NordLight.xaml", UriKind.Absolute);
    private static readonly Uri DarkThemeUri = new("pack://application:,,,/Themes/NordDark.xaml", UriKind.Absolute);

    private static ResourceDictionary? _activeThemeDict;
    private static UiThemeMode _currentMode = UiThemeMode.System;
    private static bool _listeningToSystem;

    public static bool IsDarkActive { get; private set; } = true;
    public static UiThemeMode CurrentMode => _currentMode;

    public static event Action? ThemeChanged;

    public static void ApplyTheme(UiThemeMode mode)
    {
        _currentMode = mode;
        EnsureSystemListener();

        bool useDark = mode switch
        {
            UiThemeMode.Light => false,
            UiThemeMode.Dark => true,
            _ => !IsWindowsLightThemeEnabled()
        };

        var app = Application.Current;
        if (app == null)
        {
            IsDarkActive = useDark;
            return;
        }

        var nextUri = useDark ? DarkThemeUri : LightThemeUri;
        var merged = app.Resources.MergedDictionaries;

        if (_activeThemeDict == null && merged.Count > 0 &&
            merged[0].Source != null &&
            (merged[0].Source.OriginalString.Contains("NordDark.xaml", StringComparison.OrdinalIgnoreCase) ||
             merged[0].Source.OriginalString.Contains("NordLight.xaml", StringComparison.OrdinalIgnoreCase)))
        {
            _activeThemeDict = merged[0];
        }

        if (_activeThemeDict != null && IsDarkActive == useDark && merged.Contains(_activeThemeDict))
            return;

        var newDict = new ResourceDictionary { Source = nextUri };
        if (_activeThemeDict != null)
        {
            int idx = merged.IndexOf(_activeThemeDict);
            if (idx >= 0)
            {
                merged[idx] = newDict;
            }
            else
            {
                merged.Insert(0, newDict);
            }
        }
        else
        {
            merged.Insert(0, newDict);
        }

        _activeThemeDict = newDict;
        IsDarkActive = useDark;
        ThemeChanged?.Invoke();
    }

    public static bool IsWindowsLightThemeEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int intVal)
                return intVal != 0;
        }
        catch
        {
        }
        return false;
    }

    private static void EnsureSystemListener()
    {
        if (_listeningToSystem)
            return;

        _listeningToSystem = true;
        SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category != UserPreferenceCategory.General || _currentMode != UiThemeMode.System)
                return;

            var app = Application.Current;
            if (app == null)
                return;

            app.Dispatcher.BeginInvoke(() =>
            {
                if (_currentMode == UiThemeMode.System)
                {
                    ApplyTheme(UiThemeMode.System);
                }
            });
        };
    }
}
