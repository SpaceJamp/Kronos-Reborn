using System.Windows;
using KronosReborn.Data.Runtimes;
using KronosReborn.Data.Persistence;
using KronosReborn.UI;

namespace KronosReborn;

/// <summary>
/// The main window.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ThemeManager _themeManager;
    private readonly PersistenceService _persistence;
    private readonly Settings _settings;

    public MainWindow()
    {
        InitializeComponent();

        var dataDir = DataDirectory.Get();
        var catalogPath = System.IO.Path.Combine(dataDir, "catalog.json");
        var settingsPath = DataDirectory.SettingsPath;

        System.IO.Directory.CreateDirectory(dataDir);

        _persistence = new PersistenceService();
        var loadResult = _persistence.Load();
        _settings = loadResult.Settings;

        var catalog = new RuntimeCatalog(catalogPath);
        _themeManager = new ThemeManager(_settings);

        _viewModel = new MainViewModel(catalog, _persistence, _settings, _themeManager);

        DataContext = _viewModel;

        VersionText.Text = BuildInfo.InformationalVersion;

        Loaded += async (_, _) => await _viewModel.RefreshAsync();
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Save window state
        var updated = _settings.With(
            windowWidth: Width,
            windowHeight: Height,
            windowMaximized: WindowState == WindowState.Maximized);
        _persistence.SaveSettings(updated);
    }
}