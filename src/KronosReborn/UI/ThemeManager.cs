using System;
using System.Windows;
using Microsoft.Win32;
using KronosReborn.Data.Persistence;

namespace KronosReborn.UI;

/// <summary>
/// Manages application theme (light/dark) with persistence.
/// </summary>
public sealed class ThemeManager
{
    private readonly Settings _initialSettings;
    private Settings _settings;
    private ThemeMode _currentMode = ThemeMode.Light;
    private bool _isSystemDark = false;

    public event Action<ThemeMode>? ThemeChanged;

    public ThemeManager(Settings settings)
    {
        _initialSettings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settings = settings;
        _currentMode = _settings.ThemeMode;
        DetectSystemTheme();
        ApplyTheme(_currentMode);
    }

    public ThemeMode CurrentMode => _currentMode;

    public void SetTheme(ThemeMode mode)
    {
        if (_currentMode == mode) return;

        _currentMode = mode;
        _settings = _settings.With(themeMode: mode);
        ApplyTheme(mode);
        ThemeChanged?.Invoke(mode);
    }

    public void ToggleTheme()
    {
        SetTheme(_currentMode == ThemeMode.Light ? ThemeMode.Dark : ThemeMode.Light);
    }

    private void DetectSystemTheme()
    {
        try
        {
            // Check Windows 10+ dark mode registry
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            _isSystemDark = value is int i && i == 0;
        }
        catch
        {
            _isSystemDark = false;
        }
    }

    private void ApplyTheme(ThemeMode mode)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var activeThemeDict = dictionaries.FirstOrDefault(d => d.Source == null && d.Contains("ActiveTheme"));

        if (activeThemeDict == null)
        {
            activeThemeDict = new ResourceDictionary { Source = null };
            activeThemeDict.Add("ActiveTheme", new ResourceDictionary());
            dictionaries.Add(activeThemeDict);
        }

        var themeDict = mode == ThemeMode.Dark
            ? (ResourceDictionary)Application.Current.Resources["DarkTheme"]
            : (ResourceDictionary)Application.Current.Resources["LightTheme"];

        // Clear and merge the theme
        activeThemeDict.Clear();
        foreach (var key in themeDict.Keys)
        {
            activeThemeDict[key] = themeDict[key];
        }

        // Also update the window background immediately
        foreach (Window window in Application.Current.Windows)
        {
            window.Background = (System.Windows.Media.Brush)themeDict["BackgroundBrush"];
        }
    }
}