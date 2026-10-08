using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using KronosReborn.Data.Actions;
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

    public string LastChangedText =>
        Snapshot?.LastChangedByUs?.ToString("g", System.Globalization.CultureInfo.CurrentCulture)
        ?? "Never";

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
/// A runtime version in the collection that can be offered for a game.
/// </summary>
/// <param name="EntryId">Catalog entry ID, passed back to the planner.</param>
/// <param name="Kind">Which runtime this is.</param>
/// <param name="Version">The version string.</param>
/// <param name="Trust">Whether it is considered trustworthy for installation.</param>
public sealed record RuntimeOption(
    string EntryId,
    RuntimeKind Kind,
    string Version,
    RuntimeTrust Trust)
{
    /// <summary>The text shown in the picker.</summary>
    public string Label
    {
        get
        {
            var untrusted = Trust == RuntimeTrust.Untrusted ? "  ⚠ untrusted" : "";
            return $"{RuntimeKindNames.Display(Kind)} {Version}{untrusted}";
        }
    }
}

/// <summary>
/// One line of a swap preview.
/// </summary>
/// <param name="GameName">The game this line is about.</param>
/// <param name="Summary">What would change.</param>
/// <param name="HasWarning">Whether this line involves a downgrade or an untrusted source.</param>
public sealed record PreviewLine(string GameName, string Summary, bool HasWarning);

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
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly SteamLibraryScanner _scanner = new();
    private readonly GameRuntimeScanner _runtimeScanner = new();
    private readonly RuntimeCatalog _catalog;
    private readonly PersistenceService _persistence;
    private readonly Settings _settings;
    private readonly ThemeManager _themeManager;
    private readonly DataPaths _paths;
    private readonly SwapExecutor _executor;
    private readonly UndoEngine _undo;
    private CancellationTokenSource _operationCancellation = new();

    private ObservableCollection<GameRow> _games = new();
    private ObservableCollection<PreviewLine> _preview = new();
    private IReadOnlyList<RuntimeOption> _runtimeOptions = [];
    private IReadOnlyList<SwapPlan> _pendingPlans = [];
    private IReadOnlyList<ScanProblem> _problems = [];

    private GroupMode _groupMode = GroupMode.ByLibrary;
    private GameRow? _selectedGame;
    private RuntimeOption? _selectedRuntime;
    private bool _isScanning;
    private bool _isWorking;
    private string _statusText = "Ready";
    private int _selectedCount;

    public MainViewModel(
        RuntimeCatalog catalog,
        PersistenceService persistence,
        Settings settings,
        ThemeManager themeManager,
        DataPaths paths)
    {
        _catalog = catalog;
        _persistence = persistence;
        _settings = settings;
        _themeManager = themeManager;
        _paths = paths;

        _executor = new SwapExecutor(persistence, catalog, paths);
        _undo = new UndoEngine(persistence, paths);

        // Initialize group mode from settings
        _groupMode = settings.GroupByLibrary ? GroupMode.ByLibrary : GroupMode.Flat;

        RefreshCommand = new RelayCommand(async _ => await RefreshAsync(), _ => !IsBusy);
        ToggleGroupCommand = new RelayCommand(_ => GroupMode = GroupMode == GroupMode.Flat ? GroupMode.ByLibrary : GroupMode.Flat);
        ToggleThemeCommand = new RelayCommand(_ => _themeManager.ToggleTheme());
        OpenSettingsCommand = new RelayCommand(_ => OpenSettings());
        PreviewSwapCommand = new RelayCommand(_ => PreviewSwap(), _ => CanSwap);
        ApplySwapCommand = new RelayCommand(_ => _ = ApplySwapAsync(), _ => _pendingPlans.Count > 0 && !IsWorking);
        UndoCommand = new RelayCommand(_ => UndoSelected(), _ => SelectedGame is not null && !IsWorking);
        CancelCommand = new RelayCommand(
            _ => _operationCancellation.Cancel(), _ => IsBusy);

        _games.CollectionChanged += OnGamesCollectionChanged;

        // Subscribe to theme changes to persist
        _themeManager.ThemeChanged += OnThemeChanged;
    }

    private void OnThemeChanged(ThemeMode mode)
    {
        _persistence.SaveSettings(_settings.With(themeMode: mode));
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
        // A row's own PropertyChanged has to be tracked too: ticking a checkbox changes no
        // collection event, so the selected count used to stay at whatever it was after the last
        // refresh and the "N selected" label silently lied.
        if (e.OldItems is not null)
        {
            foreach (GameRow row in e.OldItems)
            {
                row.PropertyChanged -= OnGameRowChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (GameRow row in e.NewItems)
            {
                row.PropertyChanged -= OnGameRowChanged;
                row.PropertyChanged += OnGameRowChanged;
            }
        }

        UpdateSelectedCount();
    }

    private void OnGameRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameRow.IsSelected))
        {
            UpdateSelectedCount();
        }
    }

    private void UpdateSelectedCount()
    {
        SelectedCount = _games?.Count(g => g.IsSelected) ?? 0;
        RaiseSwapCommandStates();
    }

    public IEnumerable<GameRow> VisibleGames => _games;

    public GroupMode GroupMode
    {
        get => _groupMode;
        set
        {
            if (_groupMode == value)
            {
                return;
            }

            _groupMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GroupModeText));

            // R12: the choice persists. It previously changed the label and nothing else — neither
            // the grouping in the list nor the stored setting.
            _persistence.SaveSettings(_settings.With(groupByLibrary: value == GroupMode.ByLibrary));
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
            RaiseBusyStates();
        }
    }

    public bool IsNotScanning => !IsScanning;

    /// <summary>Whether a swap or undo is running.</summary>
    public bool IsWorking
    {
        get => _isWorking;
        private set
        {
            _isWorking = value;
            OnPropertyChanged();
            RaiseBusyStates();
        }
    }

    /// <summary>Whether any long-running work is in progress.</summary>
    public bool IsBusy => IsScanning || IsWorking;

    private void RaiseBusyStates()
    {
        ((RelayCommand)RefreshCommand).RaiseCanExecuteChanged();
        ((RelayCommand)CancelCommand).RaiseCanExecuteChanged();
        RaiseSwapCommandStates();
    }

    private void RaiseSwapCommandStates()
    {
        ((RelayCommand)PreviewSwapCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ApplySwapCommand).RaiseCanExecuteChanged();
        ((RelayCommand)UndoCommand).RaiseCanExecuteChanged();
    }

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

    /// <summary>
    /// Build identity shown in the status bar.
    /// </summary>
    /// <remarks>
    /// An instance property because WPF binds to it, although it reads no instance state. The
    /// analyzer's suggestion to make it static cannot be followed without breaking the binding.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Bound from XAML, which requires an instance property.")]
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
    public ICommand PreviewSwapCommand { get; }
    public ICommand ApplySwapCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand CancelCommand { get; }

    /// <summary>The game whose runtimes the panel is showing.</summary>
    public GameRow? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (ReferenceEquals(_selectedGame, value))
            {
                return;
            }

            _selectedGame = value;
            OnPropertyChanged();
            RefreshRuntimeOptions();
            ClearPreview();
            RaiseSwapCommandStates();
        }
    }

    /// <summary>Runtime versions the collection holds that could apply to the selected game.</summary>
    public IReadOnlyList<RuntimeOption> RuntimeOptions
    {
        get => _runtimeOptions;
        private set
        {
            _runtimeOptions = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasRuntimeOptions));
        }
    }

    public bool HasRuntimeOptions => RuntimeOptions.Count > 0;

    /// <summary>The version chosen to install.</summary>
    public RuntimeOption? SelectedRuntime
    {
        get => _selectedRuntime;
        set
        {
            _selectedRuntime = value;
            OnPropertyChanged();
            ClearPreview();
            RaiseSwapCommandStates();
        }
    }

    /// <summary>What would change, shown before anything is touched.</summary>
    public ObservableCollection<PreviewLine> Preview => _preview;

    public bool HasPreview => _preview.Count > 0;

    /// <summary>Whether a swap can be previewed right now.</summary>
    private bool CanSwap =>
        !IsBusy && SelectedGame is { ExistsOnDisk: true } && SelectedRuntime is not null;

    /// <summary>Anything that went wrong during discovery, for the user to read.</summary>
    public IReadOnlyList<ScanProblem> Problems
    {
        get => _problems;
        private set
        {
            _problems = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasProblems));
        }
    }

    public bool HasProblems => Problems.Count > 0;

    /// <summary>
    /// Rediscovers games and re-reads the runtime versions each one runs.
    /// </summary>
    /// <remarks>
    /// The scanning is filesystem work over every game on the machine, so it runs on a background
    /// thread. It used to be declared async but contained no await, which meant it all happened on
    /// the dispatcher: the window froze for the duration, the busy state never rendered, and Cancel
    /// could never be clicked because nothing was pumping the message loop. R8 asks for discovery to
    /// be interruptible, which requires the UI thread to be free to show that it can be interrupted.
    /// </remarks>
    public async System.Threading.Tasks.Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsScanning = true;
        StatusText = "Discovering libraries...";

        var cancellation = BeginOperation();

        try
        {
            var token = cancellation.Token;

            var outcome = await System.Threading.Tasks.Task.Run(
                () => DiscoverAndScan(token),
                token).ConfigureAwait(true);

            token.ThrowIfCancellationRequested();

            Games = new ObservableCollection<GameRow>(outcome.Rows);
            Problems = outcome.Problems;

            // R48: versions found inside games are added to the collection without being asked for.
            foreach (var snapshot in outcome.Snapshots)
            {
                _catalog.AddFromSnapshot(snapshot, "Steam");
            }

            RefreshRuntimeOptions();

            var missing = outcome.Rows.Count(r => r.IsMissing);
            var unknown = outcome.Rows.Count(r => r.HasUnknownVersions);
            var problemCount = outcome.Problems.Count;

            StatusText =
                $"{outcome.Rows.Count} games ({missing} missing, {unknown} unknown version)"
                + (problemCount > 0 ? $", {problemCount} problem(s)" : string.Empty);
        }
        catch (OperationCanceledException)
        {
            // R8: a cancelled discovery leaves the program usable and says what happened. The games
            // already listed are left alone rather than being discarded.
            StatusText = "Refresh cancelled.";
        }
        catch (Exception ex)
        {
            // R56/R57: name what failed and why, and keep running.
            StatusText = $"Discovery failed: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>
    /// Starts a new cancellable operation, cancelling and discarding any previous one.
    /// </summary>
    /// <remarks>
    /// One token source serves whichever long operation is running, because the Cancel button means
    /// "stop what is happening now" rather than naming a particular phase.
    /// </remarks>
    private CancellationTokenSource BeginOperation()
    {
        var previous = _operationCancellation;
        var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;

        previous.Cancel();
        previous.Dispose();

        return cancellation;
    }

    private sealed record RefreshOutcome(
        List<GameRow> Rows,
        List<GameRuntimeSnapshot> Snapshots,
        List<ScanProblem> Problems);

    /// <summary>
    /// The filesystem half of a refresh. Runs off the UI thread; touches no bound property.
    /// </summary>
    private RefreshOutcome DiscoverAndScan(CancellationToken token)
    {
        var games = new List<GameRecord>();
        var locator = new SteamInstallLocator(new WindowsSteamRegistry());
        var discovery = locator.Discover();

        var results = new List<ScanResult>();
        var problems = new List<ScanProblem>(discovery.Problems);

        foreach (var root in discovery.InstallRoots)
        {
            token.ThrowIfCancellationRequested();

            // R4/R5: one unreadable library is reported and the others are still scanned.
            var result = _scanner.Scan(root, token);
            results.Add(result);
        }

        // Combine folds the per-library scans together and deduplicates the games. Doing this by
        // hand — concatenating the lists — let the same install found by two different routes appear
        // once per route, which on a default Steam installation meant every game listed three times.
        var combined = ScanResult.Combine(results);

        games.AddRange(combined.Games);
        problems.AddRange(combined.Problems);

        var snapshots = _runtimeScanner.ScanMany(games, token).ToList();

        var rows = new List<GameRow>(games.Count);
        for (var i = 0; i < games.Count; i++)
        {
            rows.Add(new GameRow(games[i], snapshots[i]));
        }

        return new RefreshOutcome(rows, snapshots, problems);
    }

    // ------------------------------------------------------------ Swapping

    private void RefreshRuntimeOptions()
    {
        if (SelectedGame is null)
        {
            RuntimeOptions = [];
            SelectedRuntime = null;
            return;
        }

        var snapshot = SelectedGame.Snapshot;

        // R45: only offer what the game could actually take. A kind the game does not carry has
        // nothing to replace, so offering it would be a swap that writes files it did not previously
        // have and that nothing says it should.
        var presentKinds = snapshot?.Runtimes
            .Where(r => r.Exists)
            .Select(r => r.Kind)
            .ToHashSet() ?? [];

        RuntimeOptions = _catalog.Entries
            .Where(e => presentKinds.Contains(e.Kind))
            .OrderBy(e => RuntimeKindNames.Display(e.Kind))
            .ThenBy(e => e.Version)
            .Select(e => new RuntimeOption(e.Id, e.Kind, e.Version, e.Trust))
            .ToList();

        SelectedRuntime = null;
    }

    /// <summary>
    /// Builds the preview: what would change, for which games, and what would warn.
    /// </summary>
    /// <remarks>
    /// R35/R36: the user can ask what would happen across the selected games. R37: downgrades and
    /// untrusted sources are marked so they are visible before anything is applied. R38: a preview
    /// that finds nothing says so rather than appearing to have succeeded.
    /// </remarks>
    private void PreviewSwap()
    {
        if (SelectedGame is null || SelectedRuntime is null)
        {
            return;
        }

        var targets = SelectedGamesForSwap().ToList();

        _pendingPlans = SwapPlanner.Plan(
            targets, _catalog, SelectedRuntime.Kind, SelectedRuntime.EntryId, _settings);

        _preview.Clear();
        foreach (var plan in _pendingPlans)
        {
            _preview.Add(new PreviewLine(plan.GameName, plan.Summary, plan.HasWarning));
        }

        OnPropertyChanged(nameof(HasPreview));

        StatusText = _pendingPlans.Count == 0
            ? $"Nothing to do: {SelectedGame.GameName} already runs " +
              $"{RuntimeKindNames.Display(SelectedRuntime.Kind)} {SelectedRuntime.Version}."
            : $"{_pendingPlans.Count} game(s) would change. Review the preview, then apply.";
    }

    /// <summary>
    /// Applies the previewed swap.
    /// </summary>
    /// <remarks>
    /// R39: one failure does not stop the rest. R40: cancelling stops work not yet started and the
    /// summary accounts for every game, including those never touched.
    /// </remarks>
    private async System.Threading.Tasks.Task ApplySwapAsync()
    {
        if (_pendingPlans.Count == 0)
        {
            return;
        }

        IsWorking = true;
        var cancellation = BeginOperation();

        try
        {
            var batch = _executor.Execute(_pendingPlans, cancellation.Token);

            StatusText = Describe(batch);

            ClearPreview();
            RefreshRuntimeOptions();

            // Re-read what the games now run, so the list reflects what was just done.
            IsWorking = false;
            await RefreshAsync();
        }
        finally
        {
            IsWorking = false;
        }
    }

    private static string Describe(BatchSwapResult batch)
    {
        var parts = new List<string>();

        if (batch.Succeeded > 0)
        {
            parts.Add($"{batch.Succeeded} updated");
        }

        if (batch.Failed > 0)
        {
            parts.Add($"{batch.Failed} failed");
        }

        if (batch.NotAttempted > 0)
        {
            parts.Add($"{batch.NotAttempted} not attempted");
        }

        if (batch.WasCancelled)
        {
            parts.Add("cancelled");
        }

        var summary = parts.Count > 0 ? string.Join(", ", parts) : "nothing to do";

        // R63: every failure is named, not just the first. A truncated list loses the information
        // the user opened the summary for.
        var failures = batch.Results
            .Where(r => !r.Succeeded)
            .Select(r => $"{r.GameName}: {r.ErrorMessage}")
            .ToList();

        return failures.Count > 0
            ? $"{summary}. {string.Join(" | ", failures)}"
            : summary;
    }

    /// <summary>Undoes the most recent swap on the selected game.</summary>
    private void UndoSelected()
    {
        if (SelectedGame is null)
        {
            return;
        }

        IsWorking = true;

        try
        {
            var history = _persistence.GetHistory(SelectedGame.GameId);

            // Which kinds this game has actually changed, most recent first.
            foreach (var kind in history.Entries.Select(e => e.RuntimeKind).Distinct())
            {
                var result = _undo.Undo(SelectedGame.GameId, kind);
                if (result.Succeeded)
                {
                    StatusText = $"Undid {RuntimeKindNames.Display(kind)} in {SelectedGame.GameName}.";
                    break;
                }

                StatusText = result.ErrorMessage ?? "Undo did not report a reason.";
                break;
            }

            ClearPreview();
            RefreshRuntimeOptions();
        }
        finally
        {
            IsWorking = false;
        }
    }

    private void ClearPreview()
    {
        _pendingPlans = [];
        _preview.Clear();
        OnPropertyChanged(nameof(HasPreview));
        RaiseSwapCommandStates();
    }

    /// <summary>
    /// The games a swap applies to: the ticked ones, or the selected game when nothing is ticked.
    /// </summary>
    /// <remarks>
    /// R35 wants the selection made on the main list rather than in a separate mode, which is what
    /// the checkbox column is. Falling back to the focused game means the common single-game case
    /// needs no tick at all.
    /// </remarks>
    private IEnumerable<SwapGameInfo> SelectedGamesForSwap()
    {
        var ticked = _games.Where(g => g.IsSelected).ToList();

        var targets = new List<GameRow>();
        if (ticked.Count > 0)
        {
            targets.AddRange(ticked);
        }
        else if (SelectedGame is not null)
        {
            targets.Add(SelectedGame);
        }

        return targets
            .Where(g => g.ExistsOnDisk)
            .Select(g => new SwapGameInfo(g.GameId, g.GameName, g.InstallPath, g.ExistsOnDisk, g.Snapshot));
    }

    private void OpenSettings()
    {
        // TODO: Open settings window
        StatusText = "Settings not implemented yet";
    }

    /// <summary>Stops subscribing to events owned elsewhere.</summary>
    public void Dispose()
    {
        _themeManager.ThemeChanged -= OnThemeChanged;
        _games.CollectionChanged -= OnGamesCollectionChanged;
        _operationCancellation.Dispose();
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