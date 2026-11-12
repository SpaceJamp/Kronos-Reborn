using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
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

        // The magic background's rings turn from here rather than from a style animation: Avalonia's
        // style animator cannot animate a RenderTransform, and ten small steps a second redraw the window
        // a sixth as often as an animation would, for movement no one could tell apart.
        SpinRing.RenderTransform = _spin;
        SpinBackRing.RenderTransform = _spinBack;
        _motion = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) =>
        {
            _spin.Angle = (_spin.Angle + 0.15) % 360;          // one turn in four minutes
            _spinBack.Angle = (_spinBack.Angle - 0.24) % 360;  // and back in two and a half
        });
        _motion.Stop();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainViewModel.MagicBackground))
                    {
                        UpdateMotion();
                    }
                };
            }

            UpdateMotion();
        };

        // A runtime file dropped anywhere on the window is offered for import, as Import… does.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DroppedFile(e) is null ? DragDropEffects.None : DragDropEffects.Copy);
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (DroppedFile(e) is { } file && DataContext is MainViewModel vm)
            {
                Tabs.SelectedIndex = 0;
                vm.BeginImport(file);
            }
        });

        Opened += async (_, _) =>
        {
            // Restored here, not in the constructor: attaching the view model resets the Diagnostics
            // tab's binding, which sent a saved Diagnostics page back to Games.
            Tabs.SelectedIndex = Math.Clamp(settings.SelectedTab, 0, Tabs.ItemCount - 1);

            if (DataContext is MainViewModel vm)
            {
                await vm.RefreshAsync();
                if (vm.CheckForUpdates)
                {
                    await vm.Updates.CheckAsync(automatic: true);
                }
            }
        };

        Closing += (_, e) =>
        {
            if (DataContext is MainViewModel { } vm && !vm.CanClose())
            {
                e.Cancel = true;
                return;
            }

            // From the persisted settings, not the copy read at start: those may have changed since.
            persistence.SaveSettings(persistence.Settings.With(
                windowWidth: Width,
                windowHeight: Height,
                windowMaximized: WindowState == WindowState.Maximized,
                selectedTab: Tabs.SelectedIndex,
                searchText: (DataContext as MainViewModel)?.SearchText,
                showOnlyUpdates: (DataContext as MainViewModel)?.ShowOnlyUpdates));

            _motion.Stop();
            (DataContext as MainViewModel)?.Dispose();
            persistence.Dispose();
        };
    }

    private readonly RotateTransform _spin = new();
    private readonly RotateTransform _spinBack = new();
    private readonly DispatcherTimer _motion = new();

    /// <summary>
    /// Starts or stops the background's motion: it moves only while switched on, while this window is
    /// the active one and not minimised, and while Windows itself shows animations.
    /// </summary>
    private void UpdateMotion()
    {
        var moving = DataContext is MainViewModel { MagicBackground: true }
                     && IsActive
                     && WindowState != WindowState.Minimized
                     && WindowsShowsAnimations();

        MagicLayer.Classes.Set("on", moving);
        _motion.IsEnabled = moving;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // The properties, not the Activated events: IsActive could already be true when the event ran.
        if (change.Property == WindowStateProperty || change.Property == IsActiveProperty)
        {
            UpdateMotion();
        }
    }

    /// <summary>Windows' "Show animations in Windows" setting; true where there is no such setting.</summary>
    private static bool WindowsShowsAnimations()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        var enabled = 1;
        return !NativeMethods.SystemParametersInfo(NativeMethods.GetClientAreaAnimation, 0, ref enabled, 0) || enabled != 0;
    }

    private static class NativeMethods
    {
        public const uint GetClientAreaAnimation = 0x1042;

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);
    }

    /// <summary>Dark while the magic background is on, so text stays readable on it; the system's otherwise.</summary>
    public static FuncValueConverter<bool, ThemeVariant> MagicTheme { get; } =
        new(on => on ? ThemeVariant.Dark : ThemeVariant.Default);

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

    private static string? DroppedFile(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p is not null);

    /// <summary>Keys for the common actions. Keys a text box uses, such as Ctrl+Z in search, stay its own.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || DataContext is not MainViewModel vm)
        {
            return;
        }

        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            Tabs.SelectedIndex = 0;
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        ICommand? command = (e.Key, e.KeyModifiers) switch
        {
            (Key.F5, KeyModifiers.None) => vm.RefreshCommand,
            (Key.Z, KeyModifiers.Control) => vm.UndoCommand,
            (Key.Y, KeyModifiers.Control) => vm.RedoCommand,
            (Key.Enter, KeyModifiers.Control) => vm.ApplySwapCommand,
            _ => null,
        };

        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Runs a command for the game whose card the menu was opened on.</summary>
    private void ForCard(object? sender, Func<MainViewModel, ICommand> command)
    {
        if ((sender as MenuItem)?.DataContext is GameRow game && DataContext is MainViewModel vm
            && command(vm) is { } c && c.CanExecute(game))
        {
            c.Execute(game);
        }
    }

    private void OnOpenFolder(object? sender, RoutedEventArgs e) => ForCard(sender, vm => vm.OpenFolderCommand);
    private void OnUndoGame(object? sender, RoutedEventArgs e) => ForCard(sender, vm => vm.UndoCommand);
    private void OnRestoreGame(object? sender, RoutedEventArgs e) => ForCard(sender, vm => vm.RestoreOriginalCommand);
    private void OnHideGame(object? sender, RoutedEventArgs e) => ForCard(sender, vm => vm.HideGameCommand);
    private void OnRemoveGame(object? sender, RoutedEventArgs e) => ForCard(sender, vm => vm.RemoveGameCommand);

    public async Task OpenFolderAsync(string path)
    {
        if (Directory.Exists(path))
        {
            await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
        }
    }

    public async Task OpenUrlAsync(Uri url) => await Launcher.LaunchUriAsync(url);

    public void RunInstallerAndExit(string installerPath)
    {
        var arguments = UpdatesViewModel.InstallerArguments(
            AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installerPath, arguments)
        {
            UseShellExecute = true,
        });
        Close();
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
