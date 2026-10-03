using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using KronosReborn.Data.Libraries;
using KronosReborn.UI;

namespace KronosReborn.Data.Persistence;

/// <summary>
/// User settings that persist between sessions.
/// </summary>
/// <remarks>
/// R41: which libraries are enabled is configurable and persists.
/// R42: accepting untrusted runtime versions is off by default.
/// R44: changing settings takes effect without restarting the program.
/// R50: stored as plain JSON, readable and correctable by hand.
/// </remarks>
public sealed record Settings(
    /// <summary>Enabled library roots. Disabled libraries are skipped during discovery.</summary>
    IReadOnlyList<string> EnabledLibraryRoots,

    /// <summary>Whether to allow installing untrusted runtime versions (no valid signature).</summary>
    bool AllowUntrustedRuntimes,

    /// <summary>Maximum history entries to keep per game.</summary>
    int MaxHistoryPerGame,

    /// <summary>Whether to group the game list by library or show flat.</summary>
    bool GroupByLibrary,

    /// <summary>Window width (restored on startup).</summary>
    double? WindowWidth,

    /// <summary>Window height (restored on startup).</summary>
    double? WindowHeight,

    /// <summary>Window maximized state.</summary>
    bool WindowMaximized,

    /// <summary>Column widths for the game list (JSON object).</summary>
    Dictionary<string, double>? ColumnWidths,

    /// <summary>UI theme mode.</summary>
    ThemeMode ThemeMode)
{
    /// <summary>Default settings for a fresh install.</summary>
    public static Settings Default { get; } = new(
        EnabledLibraryRoots: [],
        AllowUntrustedRuntimes: false,
        MaxHistoryPerGame: 50,
        GroupByLibrary: true,
        WindowWidth: null,
        WindowHeight: null,
        WindowMaximized: false,
        ColumnWidths: null,
        ThemeMode: ThemeMode.Light);

    /// <summary>Creates a copy with one value changed.</summary>
    public Settings With(
        IReadOnlyList<string>? enabledLibraryRoots = null,
        bool? allowUntrustedRuntimes = null,
        int? maxHistoryPerGame = null,
        bool? groupByLibrary = null,
        double? windowWidth = null,
        double? windowHeight = null,
        bool? windowMaximized = null,
        Dictionary<string, double>? columnWidths = null,
        ThemeMode? themeMode = null)
    {
        return new Settings(
            enabledLibraryRoots ?? EnabledLibraryRoots,
            allowUntrustedRuntimes ?? AllowUntrustedRuntimes,
            maxHistoryPerGame ?? MaxHistoryPerGame,
            groupByLibrary ?? GroupByLibrary,
            windowWidth ?? WindowWidth,
            windowHeight ?? WindowHeight,
            windowMaximized ?? WindowMaximized,
            columnWidths ?? ColumnWidths,
            themeMode ?? ThemeMode);
    }
}

/// <summary>
/// JSON-serializable wrapper for settings (handles the dictionary).
/// </summary>
internal sealed record SettingsData(
    List<string> EnabledLibraryRoots,
    bool AllowUntrustedRuntimes,
    int MaxHistoryPerGame,
    bool GroupByLibrary,
    double? WindowWidth,
    double? WindowHeight,
    bool WindowMaximized,
    Dictionary<string, double>? ColumnWidths,
    string ThemeMode)
{
    public Settings ToSettings() => new(
        EnabledLibraryRoots,
        AllowUntrustedRuntimes,
        MaxHistoryPerGame,
        GroupByLibrary,
        WindowWidth,
        WindowHeight,
        WindowMaximized,
        ColumnWidths,
        Enum.TryParse<ThemeMode>(ThemeMode, out var tm) ? tm : KronosReborn.UI.ThemeMode.Light);

    public static SettingsData FromSettings(Settings s) => new(
        [.. s.EnabledLibraryRoots],
        s.AllowUntrustedRuntimes,
        s.MaxHistoryPerGame,
        s.GroupByLibrary,
        s.WindowWidth,
        s.WindowHeight,
        s.WindowMaximized,
        s.ColumnWidths,
        s.ThemeMode.ToString());
}