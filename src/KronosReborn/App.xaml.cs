using System;
using System.Windows;

namespace KronosReborn;

/// <summary>
/// Application entry point.
/// </summary>
public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        InitializeTheme();
        base.OnStartup(e);
    }

    private void InitializeTheme()
    {
        if (Resources["LightTheme"] is ResourceDictionary lightTheme)
        {
            foreach (var key in lightTheme.Keys)
            {
                Resources[key] = lightTheme[key];
            }
        }
    }
}
