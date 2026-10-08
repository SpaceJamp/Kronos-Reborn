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
        PosterPath = record.PosterPath;
        Snapshot = snapshot;
    }

    /// <summary>
    /// The runtime choices for this game, one per runtime kind.
    /// </summary>
    /// <remarks>
    /// Each choice is independent: choosing a version for DLSS Frame Generation changes nothing
    /// about the DLSS choice beside it, and the two are applied separately. That matters because
    /// DLSS, Frame Generation and Ray Reconstruction are three separate things that routinely hold
    /// the same version number, so a single picker per game could not say which one it was for.
    /// </remarks>
    public IReadOnlyList<RuntimeChoice> Choices { get; private set; } = [];

    /// <summary>The choice shown in this game's DLSS column.</summary>
    public RuntimeChoice DlssChoice => ChoiceFor(RuntimeKind.Dlss);

    /// <summary>The choice shown in this game's DLSS Frame Generation column.</summary>
    public RuntimeChoice DlssGChoice => ChoiceFor(RuntimeKind.DlssG);

    /// <summary>The choice shown in this game's DLSS Ray Reconstruction column.</summary>
    public RuntimeChoice DlssRChoice => ChoiceFor(RuntimeKind.DlssR);

    /// <summary>The choice shown in this game's FSR column.</summary>
    public RuntimeChoice FsrChoice => ChoiceFor(RuntimeKind.Fsr);

    /// <summary>The choice shown in this game's XeSS column.</summary>
    public RuntimeChoice XessChoice => ChoiceFor(RuntimeKind.Xess);

    /// <summary>Whether any runtime in this row has a version chosen that is not the one installed.</summary>
    public bool HasAnyChange => Choices.Any(c => c.HasChange);

    private RuntimeChoice ChoiceFor(RuntimeKind kind) =>
        Choices.FirstOrDefault(c => c.Kind == kind) ?? Unavailable(kind);

    /// <summary>
    /// Rebuilds this row's runtime choices from the collection.
    /// </summary>
    /// <remarks>
    /// Called after every scan and after an import, so a version added to the collection appears in
    /// every game that could use it without the user having to reselect anything.
    /// </remarks>
    public void RebuildChoices(IReadOnlyList<CatalogEntry> catalogEntries)
    {
        foreach (var existing in Choices)
        {
            existing.Changed -= OnChoiceChanged;
        }

        Choices = RuntimeChoice.Build(Snapshot, catalogEntries);

        foreach (var choice in Choices)
        {
            choice.Changed += OnChoiceChanged;
        }

        OnPropertyChanged(nameof(DlssChoice));
        OnPropertyChanged(nameof(DlssGChoice));
        OnPropertyChanged(nameof(DlssRChoice));
        OnPropertyChanged(nameof(FsrChoice));
        OnPropertyChanged(nameof(XessChoice));
        OnPropertyChanged(nameof(HasAnyChange));
    }

    /// <summary>Raised when any version in this row is chosen or cleared.</summary>
    public event EventHandler? ChoiceChanged;

    /// <summary>Starts or stops listening to this row's choices.</summary>
    public void WatchChoices(EventHandler handler, bool subscribe)
    {
        if (subscribe)
        {
            ChoiceChanged += handler;
        }
        else
        {
            ChoiceChanged -= handler;
        }
    }

    private void OnChoiceChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(HasAnyChange));
        ChoiceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// A stand-in for a runtime this game cannot be offered.
    /// </summary>
    /// <remarks>
    /// Returned so a column always binds to a real object. A binding to null would give the interface
    /// nothing to bind and would have to special-case absence at every use.
    /// </remarks>
    private static RuntimeChoice Unavailable(RuntimeKind kind) =>
        new(kind, RuntimeKindNames.Display(kind), null, [], isAvailable: false);

    /// <summary>Cover art the library has already downloaded, or null.</summary>
    public string? PosterPath { get; }

    /// <summary>Whether there is a poster to show.</summary>
    public bool HasPoster => !string.IsNullOrWhiteSpace(PosterPath);

    /// <summary>The game's initial, shown in place of a poster when there is none.</summary>
    public string Initial =>
        GameName.Length > 0 ? GameName[..1].ToUpperInvariant() : "?";

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
            OnPropertyChanged(nameof(HasDlssG));
            OnPropertyChanged(nameof(HasDlssR));
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

    /// <summary>Whether this game has DLSS Frame Generation, so the row can label it.</summary>
    /// <remarks>
    /// The markup used to bind the label to the version string itself. A string is not a boolean, so
    /// the binding could not produce a Visibility and the label was always on screen. Visibility is
    /// asked for here instead, where the answer is unambiguous.
    /// </remarks>
    public bool HasDlssG => !string.IsNullOrWhiteSpace(DlssGVersion);

    /// <summary>Whether this game has DLSS Ray Reconstruction.</summary>
    public bool HasDlssR => !string.IsNullOrWhiteSpace(DlssRVersion);

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
    /// <remarks>
    /// The kind's name is included deliberately. The picker lists every kind the game carries in one
    /// flat list, and DLSS, DLSS Frame Generation and DLSS Ray Reconstruction are three choices the
    /// user makes separately — a line reading only "310.2.1" would not say which of the three it is,
    /// and picking the wrong one would install the wrong DLL.
    /// </remarks>
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
/// One runtime the selected game carries, and the versions that could be installed for it.
/// </summary>

/// <remarks>
/// <para>
/// The picker is arranged by runtime rather than as one flat list of every version of every runtime,
/// because the three DLSS parts are separate decisions. Under a flat list, choosing "310.2.1" is
/// ambiguous — it could mean Super Resolution, Frame Generation or Ray Reconstruction — and the
/// surrounding lines of the list repeat those names in an order that depends on which versions
/// happen to be installed elsewhere.
/// </para>
/// <para>
/// Grouped this way, each runtime gets its own heading and its own versions beneath it, so the
/// choice for one part is visibly a choice about that part. Nothing is implied to travel with
/// anything else; picking a version for Frame Generation installs Frame Generation.
/// </para>
/// </remarks>
/// <param name="Kind">The runtime this group is about.</param>
/// <param name="Label">Its name, such as "DLSS Frame Gen".</param>
/// <param name="CurrentVersion">What the game runs now, or null when it runs nothing.</param>
/// <param name="IsSameVersionAsTarget">Whether the game already runs the selected version.</param>
/// <param name="Options">Every version the collection holds for this runtime.</param>
public sealed class RuntimeChoice : INotifyPropertyChanged
{
    public RuntimeChoice(
        RuntimeKind kind,
        string label,
        string? currentVersion,
        IReadOnlyList<RuntimeOption> options,
        bool isAvailable = true)
    {
        Kind = kind;
        Label = label;
        CurrentVersion = currentVersion;
        Options = options;
        IsAvailable = isAvailable;
    }

    /// <summary>Which runtime this group is about.</summary>
    public RuntimeKind Kind { get; }

    /// <summary>Its name, such as "DLSS Frame Gen".</summary>
    public string Label { get; }

    /// <summary>What the game runs now, or null when it runs nothing.</summary>
    public string? CurrentVersion { get; }

    /// <summary>Every version the collection holds for this runtime.</summary>
    public IReadOnlyList<RuntimeOption> Options { get; }

    /// <summary>
    /// What the group heading shows about the version the game runs.
    /// </summary>
    public string CurrentText => CurrentVersion is null ? "not present" : CurrentVersion;

    /// <summary>
    /// Whether a version chosen for this group would be the one already installed.
    /// </summary>
    /// <remarks>
    /// Shown as a note rather than left to the preview, because selecting it does nothing at all:
    /// the planner skips a swap to the version already there. The user is told before they click
    /// Preview rather than being told nothing happened afterwards.
    /// </remarks>
    public string StatusText
    {
        get
        {
            if (Selected is null)
            {
                return string.Empty;
            }

            return CurrentVersion is not null &&
                   VersionNumber.AreEquivalent(CurrentVersion, Selected.Version)
                ? "already installed"
                : $"{CurrentText} → {Selected.Version}";
        }
    }

    /// <summary>The version chosen for this runtime.</summary>
    public RuntimeOption? Selected
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value))
            {
                return;
            }

            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasChange)));
            RaiseChanged();
        }
    }

    private RuntimeOption? _selected;

    /// <summary>Raised when the chosen version for this runtime changes.</summary>
    public event EventHandler? Changed;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Tells the view model a version was chosen for this runtime.</summary>
    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    internal void SetSelectedWithoutNotification(RuntimeOption? option)
    {
        _selected = option;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
    }

    /// <summary>Whether this runtime can be chosen for at all.</summary>
    /// <remarks>
    /// False when the game does not carry the runtime, or when the collection holds no version of it.
    /// In both cases there is nothing a picker here could offer: R45 says a runtime the game does not
    /// have is not offered, because installing one is adding runtime support to a game rather than
    /// upgrading what it has. An empty picker would be worse than none, since it looks usable.
    /// </remarks>
    public bool IsAvailable { get; }

    /// <summary>Whether a version has been chosen that differs from the one installed.</summary>
    /// <remarks>
    /// This is what makes a row a pending change. It is false when nothing is chosen and false when
    /// what is chosen is already installed, so a dropdown left on its current version never puts a
    /// game in the list of things to apply.
    /// </remarks>
    public bool HasChange =>
        Selected is not null
        && !VersionNumber.AreEquivalent(CurrentVersion, Selected.Version);

    /// <summary>The runtime choices for one game: one per kind, whether or not it is available.</summary>
    /// <remarks>
    /// Every kind is returned, including unavailable ones, so that a row's column always binds to the
    /// same object and the interface does not have to cope with a binding that sometimes resolves and
    /// sometimes does not.
    /// </remarks>
    public static IReadOnlyList<RuntimeChoice> Build(
        GameRuntimeSnapshot? snapshot,
        IReadOnlyList<CatalogEntry> catalogEntries)
    {
        return
        [
            .. Enum.GetValues<RuntimeKind>()
                .Select(kind =>
                {
                    var detected = snapshot?.Runtimes.FirstOrDefault(r => r.Kind == kind);

                    var options = catalogEntries
                        .Where(e => e.Kind == kind)
                        .OrderBy(e => e.Version)
                        .Select(e => new RuntimeOption(e.Id, e.Kind, e.Version, e.Trust))
                        .ToArray();

                    // R45: not offered unless the game carries it and the collection has something.
                    var available = detected is { Exists: true } && options.Length > 0;

                    return new RuntimeChoice(
                        kind,
                        RuntimeKindNames.Display(kind),
                        detected?.Version,
                        options,
                        available);
                }),
        ];
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
/// <summary>
/// One version choice waiting to be applied: a game, and the runtime within it.
/// </summary>
/// <param name="Game">The game whose file would be written.</param>
/// <param name="Choice">The chosen version, and which runtime it is for.</param>
public sealed record PendingChange(GameRow Game, RuntimeChoice Choice);

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
    private string _statusText = "Ready";
    private int _selectedCount;

    public MainViewModel(
        RuntimeCatalog catalog,
        PersistenceService persistence,
        Settings settings,
        ThemeManager themeManager,
        DataPaths paths,
        IFilePicker? filePicker = null)
    {
        _catalog = catalog;
        _persistence = persistence;
        _settings = settings;
        _themeManager = themeManager;
        _paths = paths;
        _filePicker = filePicker;

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
    public ICommand ToggleThemeCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand PreviewSwapCommand { get; }
    public ICommand ApplySwapCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand CancelCommand { get; }
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
    private bool CanSwap => !IsBusy && PendingChangeCount > 0;

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
        RaiseSwapCommandStates();
    }

    /// <summary>
    /// Reacts to a version being chosen in any row.
    /// </summary>
    /// <remarks>
    /// The preview is dropped, because it was built from a set of choices that no longer holds. Leaving
    /// it on screen would mean Apply acting on something other than what the user is looking at.
    /// </remarks>
    private void OnGameChoiceChanged(object? sender, EventArgs e)
    {
        ClearPreview();
        OnPropertyChanged(nameof(PendingChangeCount));
        OnPropertyChanged(nameof(HasPendingChanges));
        RaiseSwapCommandStates();
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
            return;
        }

        // Exactly the runtime that was chosen in that row, for that game. DLSS, DLSS Frame
        // Generation and DLSS Ray Reconstruction are three separate choices and the user makes them
        // separately; installing one never installs another. That NVIDIA supports them only at
        // matching versions is then said in the preview rather than acted on, because it is the
        // user's library and there are reasons to pair them differently that this program cannot know.
        var plans = pending
            .Select(p => new SwapGameInfo(
                p.Game.GameId, p.Game.GameName, p.Game.InstallPath, p.Game.ExistsOnDisk, p.Game.Snapshot))
            .ToArray();

        _pendingPlans = pending
            .SelectMany(p => SwapPlanner.Plan(
                plans,
                _catalog,
                p.Choice.Kind,
                p.Choice.Selected!.EntryId,
                _settings))
            .Distinct()
            .ToArray();

        _pendingPlans = SwapPlanner.NotePartsLeftBehind(_pendingPlans, plans);

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
            var batch = _executor.Execute(_pendingPlans, cancellation.Token);

            StatusText = Describe(batch);

            ClearPreview();
            RebuildAllChoices();

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
            RebuildAllChoices();
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
