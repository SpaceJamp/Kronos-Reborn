using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Input;
using KronosReborn;
using KronosReborn.Data;
using KronosReborn.Data.Actions;
using KronosReborn.Data.Downloads;
using KronosReborn.Data.Libraries;
using KronosReborn.Data.Logging;
using KronosReborn.Data.Persistence;
using KronosReborn.Data.Runtimes;
using KronosReborn.UI;
using Microsoft.Win32;

namespace KronosReborn.UI;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly SteamLibraryScanner _scanner = new();
    private readonly GameRuntimeScanner _runtimeScanner = new();
    private readonly RuntimeCatalog _catalog;
    private readonly PersistenceService _persistence;
    private Settings _settings;
    private readonly ThemeManager _themeManager;
    private readonly DataPaths _paths;
    private readonly IFilePicker? _filePicker;
    private readonly SwapExecutor _executor;
    private readonly UndoEngine _undo;
    private CancellationTokenSource _operationCancellation = new();

    private ObservableCollection<GameRow> _games = new();
    private ObservableCollection<PreviewLine> _preview = new();
    private IReadOnlyList<SwapPlan> _pendingPlans = [];
    private IReadOnlyList<ScanProblem> _problems = [];
    private RuntimeImportPreview? _importPreview;

    private GroupMode _groupMode = GroupMode.ByLibrary;
    private GameRow? _selectedGame;
    private bool _isScanning;
    private bool _isWorking;
    private bool _isDownloadOpen;
    private string _statusText = "Ready";
    private int _selectedCount;

    private readonly DownloadsViewModel _downloads;
    private readonly DiagnosticsViewModel _diagnostics;
    private bool _isDiagnosticsOpen;

    public MainViewModel(
        RuntimeCatalog catalog,
        PersistenceService persistence,
        Settings settings,
        ThemeManager themeManager,
        DataPaths paths,
        IFilePicker? filePicker = null,
        MemoryLogSink? recentLog = null,
        IReadOnlyList<ScanProblem>? startupProblems = null)
    {
        _catalog = catalog;
        _persistence = persistence;
        _settings = settings;
        _themeManager = themeManager;
        _paths = paths;
        _filePicker = filePicker;

        _executor = new SwapExecutor(persistence, catalog, paths);
        _undo = new UndoEngine(persistence, paths);
        _downloads = new DownloadsViewModel(
            new VendorDownloader(VendorDownloader.CreateClient(), catalog, paths),
            () => _games.SelectMany(g => g.Snapshot?.Runtimes ?? []).Where(r => r.Exists).Select(r => r.Kind));

        // A downloaded version is offered in every game's picker straight away.
        _downloads.CollectionChanged += (_, _) => RebuildAllChoices();

        // Problems from loading settings and history were reported by Load() and then dropped; the
        // diagnostics panel is where they are shown, alongside the latest scan's.
        var loadProblems = startupProblems ?? [];
        _diagnostics = new DiagnosticsViewModel(
            paths, catalog, persistence, () => [.. loadProblems, .. Problems], recentLog, filePicker);

        // Initialize group mode from settings
        _groupMode = settings.GroupByLibrary ? GroupMode.ByLibrary : GroupMode.Flat;

        RefreshCommand = new RelayCommand(async _ => await RefreshAsync(), _ => !IsBusy);
        ToggleGroupCommand = new RelayCommand(_ => GroupMode = GroupMode == GroupMode.Flat ? GroupMode.ByLibrary : GroupMode.Flat);
        ToggleDownloadCommand = new RelayCommand(_ => IsDownloadOpen = !IsDownloadOpen);
        ToggleDiagnosticsCommand = new RelayCommand(_ => IsDiagnosticsOpen = !IsDiagnosticsOpen);

        ToggleAreaCommand = new RelayCommand(_ => Area = Area == MainArea.Games ? MainArea.Library : MainArea.Games);
        ToggleThemeCommand = new RelayCommand(_ => _themeManager.ToggleTheme());
        OpenSettingsCommand = new RelayCommand(_ => IsSettingsOpen = !IsSettingsOpen);
        ToggleSettingsCommand = new RelayCommand(_ => IsSettingsOpen = !IsSettingsOpen);
        ChooseNewestCommand = new RelayCommand(_ => ChooseNewest(), _ => !IsBusy && (SelectedGame is not null || _games.Any(g => g.IsSelected)));
        ApplySwapCommand = new RelayCommand(_ => _ = ApplySwapAsync(), _ => _pendingPlans.Count > 0 && !IsWorking);
        UndoCommand = new RelayCommand(async _ => await RevertSelectedAsync(redo: false), _ => SelectedGame is not null && !IsWorking);
        AddGameCommand = new RelayCommand(async _ => await AddGameAsync(), _ => !IsBusy && _filePicker is not null);
        RemoveGameCommand = new RelayCommand(
            async _ => await RemoveGameAsync(), _ => !IsBusy && SelectedGame?.IsAddedByHand == true);
        RedoCommand = new RelayCommand(async _ => await RevertSelectedAsync(redo: true), _ => SelectedGame is not null && !IsWorking);
        CancelCommand = new RelayCommand(
            _ => _operationCancellation.Cancel(), _ => IsBusy);

        // Choosing a file needs the window, which the view model does not have. The picker is supplied to
        // it, so the command can stay a command and every command the model exposes is one the window
        // binds — a command that exists but is not bound is a feature nobody can reach.
        BeginImportCommand = new RelayCommand(
            _ => BeginImport(_filePicker?.AskForRuntimeFile()), _ => !IsBusy && _filePicker is not null);
        ConfirmImportCommand = new RelayCommand(_ => ConfirmImport(), _ => CanConfirmImport);
        DiscardImportCommand = new RelayCommand(_ => DiscardImport(), _ => HasImportPreview);

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
            OnPropertyChanged(nameof(HasGames));
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
            ((RelayCommand)ChooseNewestCommand).RaiseCanExecuteChanged();
        }
    }

    private void UpdateSelectedCount()
    {
        var previous = _selectedCount;
        SelectedCount = _games?.Count(g => g.IsSelected) ?? 0;

        if (previous != _selectedCount)
        {
            OnPropertyChanged(nameof(HasSelection));
        }

        RaiseSwapCommandStates();
    }

    public IEnumerable<GameRow> VisibleGames => _games;

    /// <summary>Whether there is at least one game to show.</summary>
    /// <remarks>
    /// The empty-state panel used to bind to <c>Games.Count</c> through a converter. A count is not a
    /// boolean, so the binding could not produce a Visibility and the panel stayed on screen over the
    /// whole list — the list was found and populated, and then hidden by the message saying there was
    /// nothing there. Visibility is asked for here instead.
    /// </remarks>
    public bool HasGames => _games.Count > 0;

    /// <summary>Whether any game is ticked.</summary>
    public bool HasSelection => SelectedCount > 0;

    /// <summary>
    /// Whether the detail panel has a game to show.
    /// </summary>
    /// <remarks>
    /// On the selected game rather than on any ticked row: the panel is about one game, and showing it
    /// for a multi-row selection would imply the pickers applied to all of them at once, which they do
    /// not.
    /// </remarks>
    public bool HasDetailPanel => SelectedGame is not null;

    /// <summary>Whether the download panel is open.</summary>
    public bool IsDownloadOpen
    {
        get => _isDownloadOpen;
        set
        {
            if (_isDownloadOpen == value) return;
            _isDownloadOpen = value;
            OnPropertyChanged();
        }
    }

    /// <summary>The view model for the download panel.</summary>
    public DownloadsViewModel Downloads => _downloads;

    /// <summary>The view model for the diagnostics panel.</summary>
    public DiagnosticsViewModel Diagnostics => _diagnostics;

    /// <summary>Whether the diagnostics panel is open. Opening it runs the checks.</summary>
    public bool IsDiagnosticsOpen
    {
        get => _isDiagnosticsOpen;
        set
        {
            if (_isDiagnosticsOpen == value) return;
            _isDiagnosticsOpen = value;
            OnPropertyChanged();

            if (value)
            {
                _ = _diagnostics.RunAsync();
            }
        }
    }

    /// <summary>
    /// Whether the detail panel is actually on screen.
    /// </summary>
    /// <remarks>
    /// Not the same as <see cref="HasDetailPanel"/>. The panel is drawn over the main area, and the
    /// version library is drawn over the main area too and would cover it — so a panel that stayed
    /// "shown" while the library was up would be a control that could not be seen or reached.
    /// </remarks>
    public bool ShowDetailPanel => HasDetailPanel && ShowingGames;

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

    /// <summary>
    /// Text typed into the search box, filtering the games as it changes.
    /// </summary>
    /// <remarks>
    /// Applied by the collection view rather than by rebuilding the collection, so filtering does not
    /// discard the rows' chosen versions. Rebuilding would silently throw away a change the user had
    /// made and was about to apply.
    /// </remarks>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (string.Equals(_searchText, value, StringComparison.Ordinal))
            {
                return;
            }

            _searchText = value ?? string.Empty;
            OnPropertyChanged();

            ApplyFilter();
        }
    }

    /// <summary>Whether the search found nothing, so an empty library reads as a bad search.</summary>
    public bool IsSearchMissingEverything => _searchText.Length > 0 && Games.Count == 0;

    private string _searchText = string.Empty;

    /// <summary>Applies the search text to whichever view of the games is showing.</summary>
    // ------------------------------------------------------------ The version library

    /// <summary>Which pane of the main area is showing.</summary>
    public MainArea Area
    {
        get => _area;
        set
        {
            if (_area == value)
            {
                return;
            }

            _area = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowingGames));
            OnPropertyChanged(nameof(ShowingLibrary));
            OnPropertyChanged(nameof(ShowDetailPanel));
        }
    }

    /// <summary>Whether the games pane is the one showing.</summary>
    public bool ShowingGames => Area == MainArea.Games;

    /// <summary>Whether the library pane is the one showing.</summary>
    public bool ShowingLibrary => Area == MainArea.Library;

    /// <summary>Switches between the games and the version library.</summary>
    public ICommand ToggleAreaCommand { get; }

    /// <summary>What the area toggle says it will switch to.</summary>
    public string AreaText => Area == MainArea.Games ? "Versions ▤" : "◀ Games";

    /// <summary>Which runtime the library is filtered to.</summary>
    public LibraryKindOption LibraryKind
    {
        get
        {
            var kinds = LibraryKindOptions;

            return kinds.FirstOrDefault(o => o.Kind == _libraryKind) is { } found
                ? found
                : kinds[0];
        }
        set
        {
            if (value is not null && _libraryKind == value.Kind)
            {
                return;
            }

            _libraryKind = value?.Kind;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LibraryEntries));
            OnPropertyChanged(nameof(LibrarySummary));
        }
    }

    /// <summary>The runtimes the library can be filtered to, "all" first.</summary>
    public IReadOnlyList<LibraryKindOption> LibraryKindOptions =>
        [LibraryKindOption.All, .. LibraryKinds.Select(k => new LibraryKindOption(k, RuntimeKindNames.Display(k)))];

    /// <summary>The runtime types the library has something for.</summary>
    public IReadOnlyList<RuntimeKind> LibraryKinds =>
        [.. Enum.GetValues<RuntimeKind>()
            .Where(kind => _catalog.Entries.Any(e => e.Kind == kind))];

    /// <summary>The versions of the selected runtime the collection holds.</summary>
    public IReadOnlyList<LibraryEntry> LibraryEntries
    {
        get
        {
            // No selection means everything, which is what a first visit should show rather than an
            // empty library because nothing had been chosen yet.
            var chosen = _libraryKind;

            return
            [
                .. _catalog.Entries
                    .Where(e => chosen is null || e.Kind == chosen)
                    .OrderBy(e => RuntimeKindNames.Display(e.Kind))
                    .ThenByDescending(e => e.Version, NewestFirst)
                    .Select(e => new LibraryEntry(e, GamesUsing(e)))
            ];
        }
    }

    /// <summary>How many versions are held, and for which runtimes.</summary>
    public string LibrarySummary =>
        _catalog.Entries.Count == 0
            ? "No versions held yet. Import one, or find one in a game."
            : $"{_catalog.Entries.Count} version(s) across {LibraryKinds.Count} runtime(s).";

    /// <summary>
    /// Newest first, comparing by number rather than by text.
    /// </summary>
    /// <remarks>
    /// Text ordering puts "310.9.1" before "310.2.1", which is the wrong way round and makes the
    /// library read as though the older build were the newer one.
    /// </remarks>
    private static IComparer<string> NewestFirst =>
        Comparer<string>.Create((a, b) =>
            VersionNumber.IsOlder(a, b) ? -1
            : VersionNumber.IsOlder(b, a) ? 1
            : 0);

    /// <summary>Names of the games that already run a given catalog entry.</summary>
    private IReadOnlyList<string> GamesUsing(CatalogEntry entry) =>
        [.. _games
            .Where(g => g.Snapshot?.Runtimes.Any(r =>
                r.Exists &&
                r.Kind == entry.Kind &&
                VersionNumber.AreEquivalent(r.Version, entry.Version)) == true)
            .Select(g => g.GameName)];

    private MainArea _area = MainArea.Games;
    private RuntimeKind? _libraryKind;

    private void ApplyFilter()
    {
        var text = _searchText.Trim();

        // Both views are filtered, not just the one on screen, so switching view does not reveal games
        // the search was meant to hide.
        var filtered = text.Length == 0
            ? null
            : _games.Where(g => g.GameName.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();

        if (filtered is null)
        {
            if (ListView is not null)
            {
                ListView.Filter = null;
            }
        }
        else
        {
            var keep = filtered.ToHashSet();

            if (ListView is not null)
            {
                ListView.Filter = o => o is GameRow row && keep.Contains(row);
            }
        }

        if (ListView is not null)
        {
            ListView.Refresh();
        }

        OnPropertyChanged(nameof(IsSearchMissingEverything));
    }

    private System.ComponentModel.ICollectionView? ListView { get; set; }

    /// <summary>
    /// Hands the view model the collection view it filters.
    /// </summary>
    /// <remarks>
    /// Supplied by the window because a collection view belongs to a view. Filtering is done on the
    /// view rather than by rebuilding <see cref="Games"/>, so that searching does not discard the
    /// versions the user has chosen but not yet applied.
    /// </remarks>
    public void AttachViews(System.ComponentModel.ICollectionView list)
    {
        ListView = list;
        ApplyFilter();
    }

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
        ((RelayCommand)ChooseNewestCommand).RaiseCanExecuteChanged();
        ((RelayCommand)AddGameCommand).RaiseCanExecuteChanged();
        ((RelayCommand)RemoveGameCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ApplySwapCommand).RaiseCanExecuteChanged();
        ((RelayCommand)UndoCommand).RaiseCanExecuteChanged();
        ((RelayCommand)RedoCommand).RaiseCanExecuteChanged();
        ((RelayCommand)BeginImportCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ConfirmImportCommand).RaiseCanExecuteChanged();
        ((RelayCommand)DiscardImportCommand).RaiseCanExecuteChanged();
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
    public ICommand ToggleDownloadCommand { get; }

    public ICommand ToggleThemeCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand ToggleSettingsCommand { get; }
    public ICommand ChooseNewestCommand { get; }
    public ICommand AddGameCommand { get; }
    public ICommand RemoveGameCommand { get; }
    public ICommand ApplySwapCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ToggleDiagnosticsCommand { get; }
    public ICommand BeginImportCommand { get; }
    public ICommand ConfirmImportCommand { get; }
    public ICommand DiscardImportCommand { get; }

    /// <summary>What importing the chosen file would do, once a file has been chosen.</summary>
    public RuntimeImportPreview? ImportPreview
    {
        get => _importPreview;
        private set
        {
            _importPreview = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasImportPreview));
            OnPropertyChanged(nameof(ImportPreviewText));
            OnPropertyChanged(nameof(ImportConfirmLabel));
            RaiseSwapCommandStates();
        }
    }

    public bool HasImportPreview => ImportPreview is not null;

    /// <summary>The import preview, as a sentence for the interface to show.</summary>
    public string ImportPreviewText => ImportPreview?.Summary ?? string.Empty;

    /// <summary>
    /// What the confirming button should say.
    /// </summary>
    /// <remarks>
    /// R42 says accepting untrusted runtime versions is off by default, which implies a switch that
    /// turns it on. That setting now has exactly one job: when it is on, an untrusted file needs no
    /// separate acknowledgement, because the user has already said in advance that they accept them.
    /// With it off — the default — the button says plainly that it is accepting an untrusted file.
    ///
    /// Either way the runtime still warns before being installed into a game. R33 is not affected by
    /// this setting, and that separation is the point of having both requirements.
    /// </remarks>
    public string ImportConfirmLabel =>
        ImportPreview is { NeedsAcceptance: true } && !_settings.AllowUntrustedRuntimes
            ? "Add anyway (untrusted)"
            : "Add to collection";

    /// <summary>Whether the previewed import can be carried out right now.</summary>
    private bool CanConfirmImport =>
        !IsBusy
        && ImportPreview is { Error: null, CanImport: true }
        && (ImportPreview.NeedsAcceptance || ImportPreview.Trust == RuntimeTrust.Trusted);

    /// <summary>
    /// Inspects a file the user chose and shows what importing it would do.
    /// </summary>
    /// <remarks>
    /// Nothing is copied and nothing enters the collection until the user confirms. R16 requires the
    /// signature to be checked before an import is accepted, and safety principle 5 is warn before
    /// rather than after — which needs a moment between choosing and acting.
    /// </remarks>
    public void BeginImport(string? filePath)
    {
        ImportPreview = RuntimeImporter.Inspect(filePath);
    }

    /// <summary>Carries out the previewed import, accepting an untrusted file if the user said to.</summary>
    public void ConfirmImport()
    {
        if (ImportPreview is not { Error: null } preview || !CanConfirmImport)
        {
            return;
        }

        var acceptUntrusted = preview.NeedsAcceptance;

        IsWorking = true;

        try
        {
            // The preview no longer names the chosen file, so it is taken from the files it reported.
            // Inspecting again would re-read them, which is also a check that nothing has changed
            // underneath between the warning and the action.
            var chosen = preview.Files.Count > 0 ? preview.Files[0] : null;

            if (chosen is null)
            {
                StatusText = "The file to import could not be identified.";
                return;
            }

            var result = new RuntimeImporter(_catalog, _paths).Import(chosen, acceptUntrusted);

            if (result.Succeeded && result.Entry is not null)
            {
                StatusText =
                    $"Added {RuntimeKindNames.Display(result.Entry.Kind)} {result.Entry.Version} " +
                    $"to the collection, from {preview.FileName}.";
                ImportPreview = null;
                RebuildAllChoices();
            }
            else
            {
                // R56/R57: say what failed and why, and keep running.
                StatusText = result.Error ?? "The import did not succeed.";
            }
        }
        finally
        {
            IsWorking = false;
        }
    }

    /// <summary>Abandons the previewed import without doing anything.</summary>
    public void DiscardImport() => ImportPreview = null;

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
            OnPropertyChanged(nameof(HasDetailPanel));
            RebuildAllChoices();
            ClearPreview();
            RaiseSwapCommandStates();
        }
    }

    /// <summary>What would change, shown before anything is touched.</summary>
    public ObservableCollection<PreviewLine> Preview => _preview;

    public bool HasPreview => _preview.Count > 0;

    /// <summary>Whether anything has been chosen that could be previewed.</summary>
    /// <remarks>
    /// Based on the choices made in the rows rather than on a selection, because the rows are where
    /// the choices are now made. A game being selected in the list no longer has any bearing on what
    /// would be swapped.
    /// </remarks>
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

            RebuildAllChoices();

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

#if WINDOWS
        // --- Steam -------------------------------------------------------
        var steamLocator = new SteamInstallLocator(new WindowsSteamRegistry());
        var steamDiscovery = steamLocator.Discover();

        var results = new List<ScanResult>();
        var problems = new List<ScanProblem>(steamDiscovery.Problems);

        foreach (var root in steamDiscovery.InstallRoots)
        {
            token.ThrowIfCancellationRequested();

            // R4/R5: one unreadable library is reported and the others are still scanned.
            var result = _scanner.Scan(root, token);
            results.Add(result);
        }

        var combinedSteam = ScanResult.Combine(results);
        games.AddRange(combinedSteam.Games);
        problems.AddRange(combinedSteam.Problems);

        // --- GOG -------------------------------------------------------------
        token.ThrowIfCancellationRequested();
        var gogResult = GOGLibraryScanner.Scan(new WindowsGOGRegistry());
        games.AddRange(gogResult.Games);
        problems.AddRange(gogResult.Problems);

        // --- Epic, Ubisoft, Battle.net, EA, Xbox, and games added by hand -------
        var stores = new WindowsStoreRegistry();
        var drives = System.IO.DriveInfo.GetDrives()
            .Where(d => d.DriveType == System.IO.DriveType.Fixed && d.IsReady)
            .Select(d => d.RootDirectory.FullName);

        foreach (var read in new Func<ScanResult>[]
                 {
                     () => EpicLibrary.Scan(EpicLibrary.DefaultManifestsFolder),
                     () => UbisoftLibrary.Scan(stores),
                     () => PublisherLibrary.BattleNet(stores),
                     () => PublisherLibrary.Ea(stores),
                     () => XboxLibrary.Scan(drives),
                     () => ManualLibrary.Scan(_persistence.ManualGames),
                 })
        {
            token.ThrowIfCancellationRequested();
            var result = read();
            games.AddRange(result.Games);
            problems.AddRange(result.Problems);
        }

        // A game both stores point at is one game folder, so it is listed once.
        games = [.. GameRecord.Deduplicate(games)];
#else
        // On non-Windows platforms, library discovery is not available.
        // Registry access and Windows-specific paths don't exist.
        var problems = new List<ScanProblem>
        {
            new ScanProblem(
                "Libraries",
                null,
                "Library discovery is not available on this platform.",
                "Steam and GOG Galaxy discovery require Windows registry access.",
                "Run on Windows to discover installed games, or add games manually.")
        };
#endif

        var snapshots = _runtimeScanner.ScanMany(games, token).ToList();

        // R48: versions found inside games are added to the collection without being asked for. Done
        // here rather than on the UI thread, since each new version has its signature checked.
        for (var i = 0; i < games.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            _catalog.AddFromSnapshot(snapshots[i], games[i].LibraryName);
        }

        // Only after a scan that ran to the end: what it found decides what is stale.
        _catalog.PruneDiscovered();

        var rows = new List<GameRow>(games.Count);
        for (var i = 0; i < games.Count; i++)
        {
            rows.Add(new GameRow(games[i], snapshots[i]));
        }

        return new RefreshOutcome(rows, snapshots, problems);
    }

    // ------------------------------------------------------------ Swapping

    /// <summary>
    /// Every game with a version chosen that is not the one it runs.
    /// </summary>
    /// <remarks>
    /// This is what Preview and Apply act on. The choice is made in the game's own row rather than in
    /// a panel for a selected game, so a change can be made to any number of games and then looked at
    /// as a whole before anything is written. That is the whole reason the choices live where they do:
    /// seeing several games side by side is the only way to tell that four of them are being moved to
    /// the same version.
    /// </remarks>
    private IReadOnlyList<PendingChange> PendingChanges() =>
    [
        .. _games
            .Where(g => g.ExistsOnDisk)
            .SelectMany(g => g.Choices.Where(c => c.HasChange).Select(c => new PendingChange(g, c)))
            .ToArray(),
    ];

    /// <summary>How many version choices are waiting to be applied.</summary>
    public int PendingChangeCount => PendingChanges().Count;

    /// <summary>Whether any version choice is waiting to be applied.</summary>
    public bool HasPendingChanges => PendingChangeCount > 0;

    /// <summary>Rebuilds every row's choices from the collection and tells the interface.</summary>
    private void RebuildAllChoices()
    {
        foreach (var game in _games)
        {
            game.WatchChoices(OnGameChoiceChanged, subscribe: false);
            game.RebuildChoices(_catalog.Entries);
            game.WatchChoices(OnGameChoiceChanged, subscribe: true);
        }

        ClearPreview();
        OnPropertyChanged(nameof(PendingChangeCount));
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(LibraryEntries));
        OnPropertyChanged(nameof(LibrarySummary));
        OnPropertyChanged(nameof(LibraryKinds));
        RaiseSwapCommandStates();
    }

    /// <summary>
    /// Reacts to a version being chosen in any row.
    /// </summary>
    /// <remarks>
    /// The preview is rebuilt straight away, so what Apply will do is always on screen and a swap is
    /// two steps, choose then apply, instead of three. It used to be dropped here, which made every
    /// change of mind need another press of Preview.
    /// </remarks>
    private void OnGameChoiceChanged(object? sender, EventArgs e)
    {
        PreviewSwap();
        OnPropertyChanged(nameof(PendingChangeCount));
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(LibraryEntries));
        OnPropertyChanged(nameof(LibrarySummary));
        OnPropertyChanged(nameof(LibraryKinds));
        RaiseSwapCommandStates();
    }

    /// <summary>Adds a game from a folder the user chooses, for games no library reports.</summary>
    private async System.Threading.Tasks.Task AddGameAsync()
    {
        if (_filePicker?.AskForGameFolder() is not { } folder)
        {
            return;
        }

        if (!_persistence.SetManualGame(folder, add: true))
        {
            StatusText = $"{folder} is already in the list.";
            return;
        }

        await RefreshAsync();
        SelectedGame = _games.FirstOrDefault(g => g.GameId == ManualLibrary.IdFor(folder)) ?? SelectedGame;
    }

    /// <summary>Takes a hand-added game off the list. The game's files are not touched.</summary>
    private async System.Threading.Tasks.Task RemoveGameAsync()
    {
        if (SelectedGame is not { IsAddedByHand: true } game)
        {
            return;
        }

        _persistence.SetManualGame(game.InstallPath, add: false);
        await RefreshAsync();
    }

    /// <summary>
    /// Chooses the newest version held for every runtime in the ticked games, or the selected game.
    /// </summary>
    /// <remarks>
    /// The common case, "bring these up to date", in one click. Only upgrades are chosen, and nothing
    /// is written: the preview shows what Apply would do.
    /// </remarks>
    private void ChooseNewest()
    {
        var rows = _games.Where(g => g.IsSelected).ToList();
        if (rows.Count == 0 && SelectedGame is not null)
        {
            rows.Add(SelectedGame);
        }

        var chosen = rows
            .Where(r => r.ExistsOnDisk)
            .SelectMany(r => r.Choices)
            .Count(c => c.ChooseNewest());

        PreviewSwap();
        OnPropertyChanged(nameof(PendingChangeCount));
        OnPropertyChanged(nameof(HasPendingChanges));
        RaiseSwapCommandStates();

        if (chosen == 0)
        {
            StatusText = rows.Count == 1
                ? $"{rows[0].GameName} already runs the newest version you hold of everything it has."
                : "Every game chosen already runs the newest versions you hold.";
        }
    }

    /// <summary>
    /// Builds the preview: what would change, for which games, and what would warn.
    /// </summary>
    /// <remarks>
    /// R35/R36: the user can ask what would happen across every game with a choice made. R37:
    /// downgrades and untrusted sources are marked so they are visible before anything is applied.
    /// R38: a preview that finds nothing says so rather than appearing to have succeeded.
    /// </remarks>
    private void PreviewSwap()
    {
        var pending = PendingChanges();

        if (pending.Count == 0)
        {
            ClearPreview();
            return;
        }

        // Exactly the runtime that was chosen in that row, for that game. DLSS, DLSS Frame
        // Generation and DLSS Ray Reconstruction are three separate choices and the user makes them
        // separately; installing one never installs another. That NVIDIA supports them only at
        // matching versions is then said in the preview rather than acted on, because it is the
        // user's library and there are reasons to pair them differently that this program cannot know.
        _pendingPlans = pending
            .SelectMany(p => SwapPlanner.Plan(
                [new SwapGameInfo(
                    p.Game.GameId, p.Game.GameName, p.Game.InstallPath, p.Game.ExistsOnDisk, p.Game.Snapshot)],
                _catalog,
                p.Choice.Kind,
                p.Choice.Selected!.EntryId,
                _settings))
            .ToArray();

        _pendingPlans = SwapPlanner.NotePartsLeftBehind(_pendingPlans, pending.Select(p => new SwapGameInfo(
            p.Game.GameId, p.Game.GameName, p.Game.InstallPath, p.Game.ExistsOnDisk, p.Game.Snapshot)));

        _preview.Clear();
        foreach (var plan in _pendingPlans)
        {
            _preview.Add(new PreviewLine(plan.GameName, plan.Summary, plan.HasWarning));
        }

        OnPropertyChanged(nameof(HasPreview));

        var games = _pendingPlans.Select(p => p.GameId).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        StatusText = _pendingPlans.Count == 0
            ? "Nothing to do: every version chosen is the one already installed."
            : $"{_pendingPlans.Count} change(s) across {games} game(s). Review, then apply.";
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
            // Off the UI thread: copying and hashing every DLL froze the window, and Cancel could not
            // be clicked while it ran.
            var plans = _pendingPlans;
            var batch = await System.Threading.Tasks.Task.Run(
                () => _executor.Execute(plans, cancellation.Token));

            StatusText = Describe(batch);

            ClearPreview();
            RebuildAllChoices();

            // Re-read what the games now run, so the list reflects what was just done.
            IsWorking = false;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            // Called fire-and-forget from the command, so anything not caught here would vanish.
            StatusText = $"Apply failed: {ex.Message}";
            Log.Instance.Error(ex, "Apply failed", category: "Swap");
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

    /// <summary>Undoes the most recent swap on the selected game, or redoes the most recent undo.</summary>
    private async System.Threading.Tasks.Task RevertSelectedAsync(bool redo)
    {
        if (SelectedGame is not { } game)
        {
            return;
        }

        IsWorking = true;
        var succeeded = false;

        try
        {
            var history = _persistence.GetHistory(game.GameId);

            // The kind whose next step is the most recent one, across every kind this game changed.
            var next = history.Entries
                .Select(e => e.RuntimeKind)
                .Distinct()
                .Select(kind => (Kind: kind, Entry: redo ? history.NextToRedo(kind) : history.NextToUndo(kind)))
                .Where(step => step.Entry is not null)
                .OrderBy(step => history.Entries.IndexOf(step.Entry!))
                .FirstOrDefault();

            if (next.Entry is null)
            {
                StatusText = redo ? "Nothing to redo." : "Nothing to undo.";
                return;
            }

            var result = await System.Threading.Tasks.Task.Run(() => redo
                ? _undo.Redo(game.GameId, next.Kind)
                : _undo.Undo(game.GameId, next.Kind));

            succeeded = result.Succeeded;
            StatusText = result.Succeeded
                ? $"{(redo ? "Redid" : "Undid")} {RuntimeKindNames.Display(next.Kind)} in {game.GameName}."
                : result.ErrorMessage ?? $"{(redo ? "Redo" : "Undo")} did not report a reason.";

            ClearPreview();
            RebuildAllChoices();
        }
        catch (Exception ex)
        {
            StatusText = $"{(redo ? "Redo" : "Undo")} failed: {ex.Message}";
            Log.Instance.Error(ex, "Undo or redo failed", category: "Undo");
        }
        finally
        {
            IsWorking = false;
        }

        if (succeeded)
        {
            // The rows still show what the game ran before; plans made from them would be wrong.
            var status = StatusText;
            await RefreshAsync();
            StatusText = status;
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

    private bool _isSettingsOpen;
    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set
        {
            _isSettingsOpen = value;
            OnPropertyChanged(nameof(IsSettingsOpen));
        }
    }

    public bool AllowUntrustedRuntimes
    {
        get => _settings.AllowUntrustedRuntimes;
        set
        {
            if (_settings.AllowUntrustedRuntimes == value) return;
            _settings = _settings.With(allowUntrustedRuntimes: value);
            _persistence.SaveSettings(_settings);
            OnPropertyChanged(nameof(AllowUntrustedRuntimes));
        }
    }

    public int MaxHistoryPerGame
    {
        get => _settings.MaxHistoryPerGame;
        set
        {
            // Below one, nothing could be recorded and no swap could be undone.
            value = Math.Max(1, value);
            if (_settings.MaxHistoryPerGame == value)
            {
                OnPropertyChanged(nameof(MaxHistoryPerGame));
                return;
            }
            _settings = _settings.With(maxHistoryPerGame: value);
            _persistence.SaveSettings(_settings);
            OnPropertyChanged(nameof(MaxHistoryPerGame));
        }
    }

    public bool GroupByLibrary
    {
        get => _settings.GroupByLibrary;
        set
        {
            if (_settings.GroupByLibrary == value) return;
            _settings = _settings.With(groupByLibrary: value);
            _persistence.SaveSettings(_settings);
            GroupMode = value ? GroupMode.ByLibrary : GroupMode.Flat;
            OnPropertyChanged(nameof(GroupByLibrary));
        }
    }

    /// <summary>Stops subscribing to events owned elsewhere.</summary>
    public void Dispose()
    {
        _themeManager.ThemeChanged -= OnThemeChanged;
        _games.CollectionChanged -= OnGamesCollectionChanged;
        _operationCancellation.Dispose();
        _downloads.Dispose();
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
