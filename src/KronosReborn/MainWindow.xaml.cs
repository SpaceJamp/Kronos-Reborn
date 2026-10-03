using System.Windows;
using KronosReborn.Data.Runtimes;
using KronosReborn.UI;

namespace KronosReborn;

/// <summary>
/// The main window.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        var catalogPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KronosReborn",
            "catalog.json");

        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(catalogPath)!);

        var catalog = new RuntimeCatalog(catalogPath);
        _viewModel = new MainViewModel(catalog);

        DataContext = _viewModel;

        VersionText.Text = BuildInfo.InformationalVersion;

        Loaded += async (_, _) => await _viewModel.RefreshAsync();
    }
}