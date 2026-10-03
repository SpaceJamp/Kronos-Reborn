namespace KronosReborn.UI;

/// <summary>
/// Application theme mode.
/// </summary>
/// <remarks>
/// Separated from ThemeManager to avoid circular dependency with Settings.
/// </remarks>
public enum ThemeMode
{
    /// <summary>Light theme.</summary>
    Light,

    /// <summary>Dark theme.</summary>
    Dark,

    /// <summary>Follows system setting.</summary>
    System,
}