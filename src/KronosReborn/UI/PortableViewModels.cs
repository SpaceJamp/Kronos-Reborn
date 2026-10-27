using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
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

    /// <summary>The choice shown in this game's FSR (Vulkan) column.</summary>
    public RuntimeChoice FsrVkChoice => ChoiceFor(RuntimeKind.FsrVk);

    /// <summary>The choice shown in this game's XeSS column.</summary>
    public RuntimeChoice XessChoice => ChoiceFor(RuntimeKind.Xess);

    /// <summary>The choice shown in this game's XeSS Frame Generation column.</summary>
    public RuntimeChoice XessFgChoice => ChoiceFor(RuntimeKind.XessFg);

    /// <summary>The choice shown in this game's XeSS (DX11) column.</summary>
    public RuntimeChoice XessDx11Choice => ChoiceFor(RuntimeKind.XessDx11);

    /// <summary>The choice shown in this game's XeLL column.</summary>
    public RuntimeChoice XellChoice => ChoiceFor(RuntimeKind.Xell);

    /// <summary>The choice shown in this game's FSR Upscaler (SDK 2) column.</summary>
    public RuntimeChoice FsrUpscalerChoice => ChoiceFor(RuntimeKind.FsrUpscaler);

    /// <summary>The choice shown in this game's FSR Frame Generation (SDK 2) column.</summary>
    public RuntimeChoice FsrFgChoice => ChoiceFor(RuntimeKind.FsrFg);

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
        OnPropertyChanged(nameof(FsrVkChoice));
        OnPropertyChanged(nameof(XessChoice));
        OnPropertyChanged(nameof(XessFgChoice));
        OnPropertyChanged(nameof(XessDx11Choice));
        OnPropertyChanged(nameof(XellChoice));
        OnPropertyChanged(nameof(FsrUpscalerChoice));
        OnPropertyChanged(nameof(FsrFgChoice));
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
    public string? FsrVkVersion => Snapshot?.Runtimes.FirstOrDefault(r => r.Kind == RuntimeKind.FsrVk)?.Version;
    public string? XessFgVersion => Snapshot?.Runtimes.FirstOrDefault(r => r.Kind == RuntimeKind.XessFg)?.Version;
    public string? XessDx11Version => Snapshot?.Runtimes.FirstOrDefault(r => r.Kind == RuntimeKind.XessDx11)?.Version;
    public string? XellVersion => Snapshot?.Runtimes.FirstOrDefault(r => r.Kind == RuntimeKind.Xell)?.Version;

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
                .OrderBy(RuntimeKindNames.Display)
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

/// <summary>
/// One version in the collection, as the library shows it.
/// </summary>
/// <remarks>
/// <para>
/// This is the answer to a question the rest of the interface cannot answer: what is actually
/// available to install. The game list shows what each game runs; the pickers show what could be put
/// in a particular game. Neither says what the collection holds in total, where a version came from,
/// or whether it can be trusted.
/// </para>
/// <para>
/// Games currently running it is included because it is the practical question: a version that three
/// games already run is one that is known to work on this machine.
/// </para>
/// </remarks>
/// <param name="Entry">The catalog entry being shown.</param>
/// <param name="UsedBy">Names of the games that already run this version.</param>
public sealed record LibraryEntry(CatalogEntry Entry, IReadOnlyList<string> UsedBy)
{
    /// <summary>Which runtime this version is for.</summary>
    public RuntimeKind Kind => Entry.Kind;

    /// <summary>Its name, such as "DLSS Frame Gen".</summary>
    public string KindLabel => RuntimeKindNames.Display(Entry.Kind);

    /// <summary>The version number.</summary>
    public string Version => Entry.Version;

    /// <summary>Whether the file was brought in by the user rather than found in a game.</summary>
    public bool IsImported => Entry.Source == RuntimeSource.Imported;

    /// <summary>Where it came from, in the user's terms.</summary>
    public string SourceText => Entry.SourceDetail;

    /// <summary>Whether the signature was judged, and what that means for installing it.</summary>
    public bool IsUntrusted => !Entry.IsInstallableWithoutWarning;

    /// <summary>A short mark for the trust state, since a colour alone says nothing to everyone.</summary>
    public string TrustText => IsUntrusted ? "⚠ untrusted" : "signed";

    /// <summary>How many games already run it, and which, or that none do.</summary>
    public string UsageText =>
        UsedBy.Count == 0
            ? "Not installed in any game"
            : UsedBy.Count == 1
                ? $"Used by {UsedBy[0]}"
                : $"Used by {UsedBy.Count} games";
}

/// <summary>
/// A runtime the library can be filtered to.
/// </summary>
/// <param name="Kind">The runtime, or null for "every runtime".</param>
/// <param name="Label">Its name in the picker.</param>
public sealed record LibraryKindOption(RuntimeKind? Kind, string Label)
{
    public static LibraryKindOption All { get; } = new(null, "All runtimes");
}

/// <summary>Which pane of the main area is showing.</summary>
public enum MainArea
{
    /// <summary>The games, as a list or as poster cards.</summary>
    Games,

    /// <summary>The collection's versions, as cards.</summary>
    Library,
}

public enum GroupMode
{
    /// <summary>Flat list, no grouping.</summary>
    Flat,

    /// <summary>Group by library name.</summary>
    ByLibrary,
}

/// <summary>
