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
        // Initialize theme resources BEFORE the MainWindow is created from StartupUri
        InitializeTheme();

        base.OnStartup(e);
    }

    private void InitializeTheme()
    {
        // Copy LightTheme resources directly into Application.Resources
        // so DynamicResource references in MainWindow.xaml can resolve.
        if (Resources["LightTheme"] is ResourceDictionary lightTheme)
        {
            foreach (var key in lightTheme.Keys)
            {
                Resources[key] = lightTheme[key];
            }
        }
    }
}