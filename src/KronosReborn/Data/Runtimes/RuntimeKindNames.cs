namespace KronosReborn.Data.Runtimes;

/// <summary>
/// Human-readable names for runtime kinds.
/// </summary>
/// <remarks>
/// These strings were repeated in three places — the catalog entry display name, the swap plan
/// summary and the history description — and had already begun to drift. A single definition means a
/// rename cannot leave one surface calling DLSS Frame Generation "DLSS FG".
/// </remarks>
public static class RuntimeKindNames
{
    /// <summary>The name shown to the user for a runtime kind.</summary>
    public static string Display(RuntimeKind kind) => kind switch
    {
        RuntimeKind.Dlss => "DLSS",
        RuntimeKind.DlssG => "DLSS Frame Gen",
        RuntimeKind.DlssR => "DLSS Ray Recon",
        RuntimeKind.Fsr => "FSR",
        RuntimeKind.Xess => "XeSS",
        _ => kind.ToString(),
    };
}