using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using KronosReborn.Data.Persistence;
using Microsoft.Win32;

namespace KronosReborn.UI;

/// <summary>
/// Manages application theme (light/dark) with persistence.
/// </summary>
public sealed class ThemeManager
{
    private readonly Settings _initialSettings;
    private Settings _settings;
    private ThemeMode _currentMode = ThemeMode.Light;
    private bool _isSystemDark;

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
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            _isSystemDark = value is int i && i == 0;
        }
        catch
        {
            _isSystemDark = false;
        }
    }

    private static void ApplyTheme(ThemeMode mode)
    {
        var appResources = Application.Current.Resources;

        var themeKey = mode == ThemeMode.Dark ? "DarkTheme" : "LightTheme";
        if (!appResources.Contains(themeKey))
        {
            return;
        }

        var themeDict = (ResourceDictionary)appResources[themeKey];

        foreach (var key in themeDict.Keys)
        {
            appResources[key] = themeDict[key];
        }

        foreach (Window window in Application.Current.Windows)
        {
            window.Background = (System.Windows.Media.Brush)themeDict["BackgroundBrush"];
            window.Foreground = (System.Windows.Media.Brush)themeDict["TextPrimaryBrush"];

            if (window.Content is FrameworkElement root)
            {
                root.InvalidateVisual();
                root.UpdateLayout();
            }

            window.InvalidateVisual();
            window.UpdateLayout();
        }
    }
}
