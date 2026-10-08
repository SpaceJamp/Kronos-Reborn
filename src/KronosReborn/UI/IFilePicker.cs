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
    string? AskForRuntimeFile();
}
