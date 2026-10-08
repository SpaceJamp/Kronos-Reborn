using System.Windows;
using KronosReborn.Data.Persistence;
using KronosReborn.Data.Runtimes;
using KronosReborn.UI;

namespace KronosReborn;

/// <summary>
/// The main window.
/// </summary>
/// <remarks>
/// Owns a <see cref="PersistenceService"/> for as long as the window is open and disposes it when the
/// window closes. A WPF window cannot implement <see cref="IDisposable"/> in the ordinary sense —
/// its lifetime is managed by the application, not by whoever holds a reference — so the analyzer's
/// suggestion is suppressed and the disposal is done explicitly instead.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "A WPF Window is not disposed by its owner; it disposes its own resources on close.")]
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ThemeManager _themeManager;
    private readonly PersistenceService _persistence;
    private readonly Settings _settings;

    public MainWindow()
    {
        InitializeComponent();

        var paths = DataPaths.ForCurrentUser();
        paths.EnsureCreated();

        _persistence = new PersistenceService(paths);
        var loadResult = _persistence.Load();
        _settings = loadResult.Settings;

        var catalog = new RuntimeCatalog(paths.CatalogPath);
        _themeManager = new ThemeManager(_settings);

        _viewModel = new MainViewModel(catalog, _persistence, _settings, _themeManager, paths, new RuntimeFilePicker(this));

        DataContext = _viewModel;

        // Restore the window geometry saved on close. It was previously written to settings but
        // never read back, so the size never survived a restart.
        if (_settings.WindowWidth is { } width && width > 0)
        {
            Width = width;
        }
        if (_settings.WindowHeight is { } height && height > 0)
        {
            Height = height;
        }
        if (_settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }

        VersionText.Text = BuildInfo.InformationalVersion;

        Loaded += async (_, _) => await _viewModel.RefreshAsync();
        Closing += OnClosing;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ApplyGrouping(_viewModel.GroupMode);
    }

    /// <summary>
    /// Asks the user for a runtime file, using the system dialog.
    /// </summary>
    /// <remarks>
    /// This is the one place a modal dialog is right. Choosing a file is a request for information
    /// from the system, not a question put to the user; everything the program then needs to say about
    /// that file — its version, who signed it, whether it can be used — is shown inline, because a
    /// dialog that could equally be answered on the main window should not be one.
    /// </remarks>
    private sealed class RuntimeFilePicker(Window owner) : IFilePicker
    {
        public string? AskForRuntimeFile()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose a graphics runtime file to add to the collection",
                Filter = "Runtime files (*.dll)|*.dll|All files (*.*)|*.*",
                CheckFileExists = true,
            };

            return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
        }
    }


    /// <summary>
    /// Keeps the list's grouping in step with the toolbar toggle.
    /// </summary>
    /// <remarks>
    /// Grouping is applied to the list's own view rather than to a CollectionViewSource declared in
    /// the resources. A CollectionViewSource there is a Freezable, and one declared in a resource
    /// dictionary does not reliably inherit the DataContext, which is a well-known way to end up with
    /// a view over nothing while the bound collection is full. Asking for the default view of the
    /// collection the list is already showing has no such failure mode.
    /// </remarks>
    private void ApplyGrouping(GroupMode mode)
    {
        if (GameListView.ItemsSource is not System.Collections.IEnumerable source)
        {
            return;
        }

        if (System.Windows.Data.CollectionViewSource.GetDefaultView(source)
            is not System.ComponentModel.ICollectionView view)
        {
            return;
        }

        view.GroupDescriptions.Clear();

        if (mode == GroupMode.ByLibrary)
        {
            view.GroupDescriptions.Add(
                new System.Windows.Data.PropertyGroupDescription(nameof(GameRow.LibraryName)));
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // The list gets a new collection after each refresh, and a new collection has a new view, so
        // the grouping has to be applied again or it silently reverts to flat.
        if (e.PropertyName == nameof(MainViewModel.GroupMode)
            || e.PropertyName == nameof(MainViewModel.Games))
        {
            ApplyGrouping(_viewModel.GroupMode);
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Save window state
        var updated = _settings.With(
            windowWidth: Width,
            windowHeight: Height,
            windowMaximized: WindowState == WindowState.Maximized);
        _persistence.SaveSettings(updated);

        // Nothing reads either of these once the window is closing.
        _viewModel.Dispose();
        _persistence.Dispose();
    }
}
