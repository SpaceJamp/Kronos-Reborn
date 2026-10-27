namespace KronosReborn.UI;

/// <summary>
/// Asks the user to choose a file.
/// </summary>
/// <remarks>
/// An interface rather than a call to the dialog directly, for two reasons. The dialog is the one
/// part of the interface that cannot be reached without a window, so keeping it behind a seam is what
/// lets the import flow be tested at all. And it keeps the rule the specification states in one
/// place: a file dialog is a request for information from the system and is legitimately modal,
/// while every question the program asks about that file is shown inline.
/// </remarks>
public interface IFilePicker
{
    /// <summary>
    /// Shows a chooser for a graphics runtime file.
    /// </summary>
    /// <returns>The chosen path, or null if the user cancelled.</returns>
    Task<string?> AskForRuntimeFileAsync();

    /// <summary>
    /// Shows a save dialog for a diagnostics bundle.
    /// </summary>
    /// <param name="suggestedName">The file name offered by default.</param>
    /// <returns>The chosen path, or null if the user cancelled.</returns>
    Task<string?> AskWhereToSaveDiagnosticsAsync(string suggestedName);

    /// <summary>
    /// Shows a folder chooser, for a game no library reports or a folder of such games.
    /// </summary>
    /// <param name="title">What the chooser asks for.</param>
    /// <returns>The chosen folder, or null if the user cancelled.</returns>
    Task<string?> AskForGameFolderAsync(string title);

    /// <summary>Puts text on the clipboard, which like a dialog belongs to the window.</summary>
    Task SetClipboardTextAsync(string text);
}
