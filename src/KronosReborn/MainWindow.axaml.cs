using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using KronosReborn.Data.Logging;
using KronosReborn.Data.Persistence;
using KronosReborn.UI;

namespace KronosReborn;

/// <summary>
/// The window, and the few things only a window can do for the view model: dialogs and the clipboard.
/// </summary>
public sealed partial class MainWindow : Window, IFilePicker
{
    private readonly PersistenceService? _persistence;
    private ListBox? _selectedIn;

    /// <summary>For Avalonia's designer only.</summary>
    public MainWindow() => InitializeComponent();

    public MainWindow(PersistenceService persistence)
    {
        _persistence = persistence;
        InitializeComponent();

        // The size and state saved on close.
        var settings = persistence.Settings;
        if (settings.WindowWidth is > 0 and var width)
        {
            Width = width;
        }
        if (settings.WindowHeight is > 0 and var height)
        {
            Height = height;
        }
        if (settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }

        Opened += async (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                await vm.RefreshAsync();
            }
        };

        Closing += (_, _) =>
        {
            // From the persisted settings, not the copy read at start: those may have changed since.
            persistence.SaveSettings(persistence.Settings.With(
                windowWidth: Width,
                windowHeight: Height,
                windowMaximized: WindowState == WindowState.Maximized));

            (DataContext as MainViewModel)?.Dispose();
            persistence.Dispose();
        };
    }

    /// <summary>Covers already decoded, by path: the grid is rebuilt on every search keystroke.</summary>
    private static readonly Dictionary<string, Bitmap> Posters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Turns a poster path into an image, or nothing when it cannot be read.</summary>
    public static FuncValueConverter<string?, Bitmap?> PosterImage { get; } = new(path =>
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        if (Posters.TryGetValue(path, out var cached))
        {
            return cached;
        }

        try
        {
            // Decoded to card size, so a library of large covers does not hold full-size bitmaps.
            using var stream = File.OpenRead(path);
            return Posters[path] = Bitmap.DecodeToWidth(stream, 300);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Instance.Warning($"Poster could not be read: {path} ({ex.Message})", category: "UI");
            return null;
        }
    });

    /// <summary>
    /// One game is selected across every library's grid: choosing in one clears the others.
    /// </summary>
    private void OnGameSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list || list.SelectedItem is not GameRow game || DataContext is not MainViewModel vm)
        {
            return;
        }

        if (_selectedIn is not null && !ReferenceEquals(_selectedIn, list))
        {
            _selectedIn.SelectedItem = null;
        }

        _selectedIn = list;
        vm.SelectedGame = game;
    }

    public async Task<string?> AskForRuntimeFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a graphics runtime file to add to the collection",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Runtime files") { Patterns = ["*.dll"] }, FilePickerFileTypes.All],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> AskForGameFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> AskWhereToSaveDiagnosticsAsync(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save diagnostics",
            SuggestedFileName = suggestedName,
            DefaultExtension = "zip",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("Zip archive") { Patterns = ["*.zip"] }],
        });

        return file?.TryGetLocalPath();
    }

    public async Task SetClipboardTextAsync(string text)
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}
