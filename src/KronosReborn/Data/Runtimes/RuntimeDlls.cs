using System;

namespace KronosReborn.Data.Runtimes;

/// <summary>
/// The kind of runtime a DLL belongs to.
/// </summary>
public enum RuntimeKind
{
    /// <summary>NVIDIA DLSS (Super Resolution).</summary>
    Dlss,

    /// <summary>NVIDIA DLSS Frame Generation.</summary>
    DlssG,

    /// <summary>NVIDIA DLSS Ray Reconstruction.</summary>
    DlssR,

    /// <summary>AMD FidelityFX Super Resolution 2/3.</summary>
    Fsr,

    /// <summary>Intel XeSS.</summary>
    Xess,
}

/// <summary>
/// The file name of a runtime DLL as it appears in a game folder.
/// </summary>
/// <remarks>
/// This is the source of truth for which DLL corresponds to which kind. A game may contain several
/// of these at once, and each is managed independently.
/// </remarks>
public static class RuntimeDlls
{
    /// <summary>All known runtime DLL file names, grouped by kind.</summary>
    public static IReadOnlyDictionary<RuntimeKind, string[]> ByKind { get; } =
        new Dictionary<RuntimeKind, string[]>
        {
            { RuntimeKind.Dlss,  ["nvngx_dlss.dll"] },
            { RuntimeKind.DlssG, ["nvngx_dlssg.dll"] },
            { RuntimeKind.DlssR, ["nvngx_dlssr.dll"] },
            { RuntimeKind.Fsr,   ["amd_fidelityfx_dx12.dll", "ffx_fsr2_api_dx12.dll"] },
            { RuntimeKind.Xess,  ["libxess.dll"] },
        }.AsReadOnly();

    /// <summary>Reverse lookup: file name to kind.</summary>
    public static IReadOnlyDictionary<string, RuntimeKind> ByFileName { get; } =
        ByKind.SelectMany(kvp => kvp.Value.Select(v => (v, kvp.Key)))
              .ToDictionary(t => t.v, t => t.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a file name is a known runtime DLL.</summary>
    public static bool IsKnown(string fileName) => ByFileName.ContainsKey(fileName);

    /// <summary>The kind for a known file name, or null if not a runtime DLL.</summary>
    public static RuntimeKind? KindFor(string fileName) =>
        ByFileName.TryGetValue(fileName, out var kind) ? kind : null;

    /// <summary>All known file names as a flat list.</summary>
    public static IReadOnlyList<string> AllFileNames { get; } =
        ByKind.Values.SelectMany(x => x).ToList().AsReadOnly();
}