using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using KronosReborn.Data.Libraries;
using KronosReborn.Data.Persistence;
using KronosReborn.Data.Runtimes;
using Microsoft.Win32;

namespace KronosReborn.UI;

/// <summary>
/// A game row in the UI, combining discovery and runtime scan results.
/// </summary>
public sealed class GameRow : INotifyPropertyChanged
{
    private GameRuntimeSnapshot? _snapshot;
    private bool _isSelected;

    public string GameId { get; }
    public string GameName { get; }
    public string InstallPath { get; }
    public string LibraryName { get; }
    public bool ExistsOnDisk { get; }

    public GameRow(GameRecord record, GameRuntimeSnapshot? snapshot = null)
    {
        GameId = record.Id;
        GameName = record.DisplayName;
        InstallPath = record.InstallPath;
        LibraryName = record.LibraryName;
        ExistsOnDisk = record.Exists;
        Snapshot = snapshot;
    }

    public GameRuntimeSnapshot? Snapshot
    {
        get => _snapshot;
        set
        {
            _snapshot = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DlssVersion));
            OnPropertyChanged(nameof(DlssGVersion));
            OnPropertyChanged(nameof(DlssRVersion));
            OnPropertyChanged(nameof(FsrVersion));
            OnPropertyChanged(nameof(XessVersion));
            OnPropertyChanged(nameof(HasAnyRuntime));
            OnPropertyChanged(nameof(HasUnknownVersions));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusColor));
        }
    }

    public string? DlssVersion => Snapshot?.Runtimes.FirstOrDefault(r => r.Kind == RuntimeKind.Dlss)?.Version;
    public string? DlssGVersion => Snapshot?.Runtimes.FirstOrDefault(r => r.Kind == RuntimeKind.DlssG)?.Version;
    public string? DlssRVersion => Snapshot?.Runtimes.FirstOrDefault(r => r.Kind == RuntimeKind.DlssR)?.Version;
    public string? FsrVersion => Snapshot?.Runtimes.FirstOrDefault(r => r.Kind == RuntimeKind.Fsr)?.Version;
    public string? XessVersion => Snapshot?.Runtimes.FirstOrDefault(r => r.Kind == RuntimeKind.Xess)?.Version;

    public bool HasAnyRuntime => Snapshot?.HasAnyRuntime == true;
    public bool HasUnknownVersions => Snapshot?.HasUnknownVersions == true;
    public bool IsMissing => !ExistsOnDisk;

    public string StatusText
    {
        get
        {
            if (IsMissing) return "Missing";
            if (!HasAnyRuntime) return "No runtimes";
            if (HasUnknownVersions) return "Unknown version";
            return "OK";
        }
    }

    public string StatusColor
    {
        get
        {
            if (IsMissing) return "#E74C3C";
            if (!HasAnyRuntime) return "#F39C12";
            if (HasUnknownVersions) return "#F39C12";
            return "#27AE60";
        }
    }

    public string LastChangedText => Snapshot?.LastChangedByUs?.ToString("g") ?? "Never";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Grouping mode for the game list.
/// </summary>
public enum GroupMode
{
    /// <summary>Flat list, no grouping.</summary>
    Flat,

    /// <summary>Group by library name.</summary>
    ByLibrary,
}

/// <summary>
/// Main view model for the application.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly SteamLibraryScanner _scanner = new();
    private readonly GameRuntimeScanner _runtimeScanner = new();
    private readonly RuntimeCatalog _catalog;
    private readonly PersistenceService _persistence;
    private readonly Settings _settings;
    private readonly ThemeManager _themeManager;
    private ObservableCollection<GameRow> _games = new();
    private GroupMode _groupMode = GroupMode.ByLibrary;
    private bool _isScanning;
    private string _statusText = "Ready";
    private int _selectedCount;

    public MainViewModel(
        RuntimeCatalog catalog,
        PersistenceService persistence,
        Settings settings,
        ThemeManager themeManager)
    {
        _catalog = catalog;
        _persistence = persistence;
        _settings = settings;
        _themeManager = themeManager;

        // Initialize group mode from settings
        _groupMode = settings.GroupByLibrary ? GroupMode.ByLibrary : GroupMode.Flat;

        RefreshCommand = new RelayCommand(async _ => await RefreshAsync(), _ => !IsScanning);
        ToggleGroupCommand = new RelayCommand(_ => GroupMode = GroupMode == GroupMode.Flat ? GroupMode.ByLibrary : GroupMode.Flat);
        ToggleThemeCommand = new RelayCommand(_ => _themeManager.ToggleTheme());
        OpenSettingsCommand = new RelayCommand(_ => OpenSettings());

        _games.CollectionChanged += OnGamesCollectionChanged;

        // Subscribe to theme changes to persist
        _themeManager.ThemeChanged += OnThemeChanged;
    }

    private void OnThemeChanged(ThemeMode mode)
    {
        var updated = _settings.With(themeMode: mode);
        _persistence.SaveSettings(updated);
    }

    public ObservableCollection<GameRow> Games
    {
        get => _games;
        private set
        {
            if (_games != null)
            {
                _games.CollectionChanged -= OnGamesCollectionChanged;
            }
            _games = value;
            if (_games != null)
            {
                _games.CollectionChanged += OnGamesCollectionChanged;
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(VisibleGames));
            UpdateSelectedCount();
        }
    }

    private void OnGamesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateSelectedCount();
    }

    private void UpdateSelectedCount()
    {
        SelectedCount = _games?.Count(g => g.IsSelected) ?? 0;
    }

    public IEnumerable<GameRow> VisibleGames => _games;

    public GroupMode GroupMode
    {
        get => _groupMode;
        set
        {
            _groupMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GroupModeText));
        }
    }

    public string GroupModeText => GroupMode == GroupMode.Flat ? "Flat list" : "Group by library";

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            _isScanning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNotScanning));
            ((RelayCommand)RefreshCommand).RaiseCanExecuteChanged();
        }
    }

    public bool IsNotScanning => !IsScanning;

    public string StatusText
    {
        get => _statusText;
        private set
        {
            _statusText = value;
            OnPropertyChanged();
        }
    }

    public int SelectedCount
    {
        get => _selectedCount;
        private set
        {
            _selectedCount = value;
            OnPropertyChanged();
        }
    }

    public string BuildInfoText
    {
        get
        {
            var commit = BuildInfo.GitCommitShort ?? "unknown";
            var branch = BuildInfo.GitBranch;

            if (BuildInfo.HasBuildTimestamp == false)
            {
                return $"Build {commit}{(branch is null ? string.Empty : $", {branch}")}, date unknown";
            }

            var date = BuildInfo.BuildDateTime!.Value.ToString("g", System.Globalization.CultureInfo.CurrentCulture);

            return $"Build {commit}, {date}{(branch is null ? string.Empty : $", {branch}")}";
        }
    }

    public ICommand RefreshCommand { get; }
    public ICommand ToggleGroupCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand OpenSettingsCommand { get; }

    public async System.Threading.Tasks.Task RefreshAsync()
    {
        IsScanning = true;
        StatusText = "Discovering libraries...";

        try
        {
            // Discover Steam installations
            var locator = new SteamInstallLocator(new WindowsSteamRegistry());
            var discovery = locator.Discover();

            var allGames = new List<GameRecord>();
            var allProblems = new List<ScanProblem>();

            foreach (var root in discovery.InstallRoots)
            {
                var result = _scanner.Scan(root);
                allGames.AddRange(result.Games);
                allProblems.AddRange(result.Problems);
            }

            StatusText = $"Scanning {allGames.Count} games for runtimes...";

            // Scan runtimes
            var snapshots = _runtimeScanner.ScanMany(allGames);

            // Build rows
            var rows = new List<GameRow>();
            for (int i = 0; i < allGames.Count; i++)
            {
                rows.Add(new GameRow(allGames[i], snapshots[i]));
            }

            Games = new ObservableCollection<GameRow>(rows);

            // Add to catalog
            foreach (var snap in snapshots)
            {
                _catalog.AddFromSnapshot(snap, "Steam"); // TODO: pass actual library name
            }

            var problemCount = allProblems.Count;
            var missingCount = rows.Count(r => r.IsMissing);
            var unknownCount = rows.Count(r => r.HasUnknownVersions);

            StatusText = $"{rows.Count} games ({missingCount} missing, {unknownCount} unknown version){ (problemCount > 0 ? $", {problemCount} problem(s)" : "") }";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void OpenSettings()
    {
        // TODO: Open settings window
        StatusText = "Settings not implemented yet";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Simple relay command implementation.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}