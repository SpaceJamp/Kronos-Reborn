namespace KronosReborn.Data.Persistence;

/// <summary>
/// User settings that persist between sessions.
/// </summary>
/// <remarks>
/// R42: accepting untrusted runtime versions is off by default.
/// R44: changing settings takes effect without restarting the program.
/// R50: stored as plain JSON, readable and correctable by hand.
/// <para>
/// Every value has a default here, so a file missing one, or written by an older version with
/// settings that no longer exist, still loads: missing values take the default and unknown ones are
/// ignored.
/// </para>
/// </remarks>
/// <param name="AllowUntrustedRuntimes">Whether to accept runtime files with no valid vendor signature.</param>
/// <param name="MaxHistoryPerGame">How many undo steps to keep per game.</param>
/// <param name="WindowWidth">Window width, restored on startup.</param>
/// <param name="WindowHeight">Window height, restored on startup.</param>
/// <param name="WindowMaximized">Whether the window was maximised.</param>
public sealed record Settings(
    bool AllowUntrustedRuntimes = false,
    int MaxHistoryPerGame = 50,
    double? WindowWidth = 1600,
    double? WindowHeight = 960,
    bool WindowMaximized = false)
{
    /// <summary>Default settings for a fresh install.</summary>
    public static Settings Default { get; } = new();

    /// <summary>Creates a copy with the given values changed.</summary>
    public Settings With(
        bool? allowUntrustedRuntimes = null,
        int? maxHistoryPerGame = null,
        double? windowWidth = null,
        double? windowHeight = null,
        bool? windowMaximized = null) =>
        new(
            allowUntrustedRuntimes ?? AllowUntrustedRuntimes,
            maxHistoryPerGame ?? MaxHistoryPerGame,
            windowWidth ?? WindowWidth,
            windowHeight ?? WindowHeight,
            windowMaximized ?? WindowMaximized);
}
