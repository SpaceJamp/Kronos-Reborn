using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KronosReborn.Data.Runtimes;

/// <summary>
/// The result of asking Windows to validate a file's Authenticode signature.
/// </summary>
/// <param name="IsSigned">Whether the file carries a signature at all.</param>
/// <param name="IsValid">Whether that signature is trusted and unexpired.</param>
/// <param name="StatusCode">The raw status Windows returned, for diagnostics.</param>
/// <param name="Detail">A short description of the outcome.</param>
internal sealed record AuthenticodeResult(
    bool IsSigned,
    bool IsValid,
    int StatusCode,
    string Detail)
{
    /// <summary>A one-line summary suitable for showing the user.</summary>
    public string Describe() => IsValid
        ? "signed and trusted"
        : $"{Detail} (0x{StatusCode:X8})";
}

/// <summary>
/// Asks the Windows trust provider whether a signature on a file is valid.
/// </summary>
/// <remarks>
/// <para>
/// A thin wrapper over <c>WinVerifyTrust</c>, which is the supported way to answer this question on
/// Windows. It exists because the X.509 classes cannot: pulling a certificate out of a file says
/// nothing about whether the signature over that file verifies, or whether the certificate chains to
/// a root Windows trusts. An earlier version of this code inferred trust from the subject text of a
/// certificate, which any self-signed certificate could satisfy.
/// </para>
/// <para>
/// The data structure is marshalled by hand because <c>WINTRUST_FILE_INFO</c> holds a raw pointer
/// to the path rather than a string field. The layout below is the 64-bit one, correct for x64 and
/// for ARM64 and therefore for every architecture this project builds; a 32-bit target would need a
/// second declaration.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class Authenticode
{
    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0x00000010;
    private const uint WTD_SAFER_FLAG = 0x00000100;
    private const uint WTD_STATEACTION_VERIFY = 0x00000001;
    private const uint WTD_STATEACTION_CLOSE = 0x00000002;

    /// <summary>TRUST_E_NOSIGNATURE: the file carries no signature at all.</summary>
    private const uint TrustENoSignature = 0x800B0100;

    /// <summary>Validates the signature on a file.</summary>
    public static AuthenticodeResult Verify(string filePath)
    {
        var dataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
        var pathPointer = IntPtr.Zero;
        var fileInfoPointer = IntPtr.Zero;

        try
        {
            pathPointer = Marshal.StringToHGlobalUni(filePath);

            fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(
                new WinTrustFileInfo
                {
                    cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                    pcwszFilePath = pathPointer,
                },
                fileInfoPointer,
                fDeleteOld: false);

            Marshal.StructureToPtr(
                new WinTrustData
                {
                    cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                    dwUIChoice = WTD_UI_NONE,
                    fdwRevocationChecks = WTD_REVOKE_NONE,
                    dwUnionChoice = 1, // WTD_CHOICE_FILE
                    pFile = fileInfoPointer,
                    dwStateAction = WTD_STATEACTION_VERIFY,
                    dwProvFlags = WTD_SAFER_FLAG,
                },
                dataPointer,
                fDeleteOld: false);

            var action = WinTrustActionGenericVerifyV2;
            var status = WinVerifyTrust(IntPtr.Zero, ref action, dataPointer);

            // The provider allocates and the caller must release, whatever the outcome. Skipping
            // this leaks on every call.
            var closing = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = 1,
                pFile = fileInfoPointer,
                dwStateAction = WTD_STATEACTION_CLOSE,
                dwProvFlags = WTD_SAFER_FLAG,
            };

            Marshal.StructureToPtr(closing, dataPointer, fDeleteOld: false);
            _ = WinVerifyTrust(IntPtr.Zero, ref action, dataPointer);

            return Interpret(status);
        }
        finally
        {
            if (fileInfoPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(fileInfoPointer);
            }

            if (pathPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(pathPointer);
            }

            Marshal.FreeHGlobal(dataPointer);
        }
    }

    private static AuthenticodeResult Interpret(int status)
    {
        if (status == 0)
        {
            return new AuthenticodeResult(IsSigned: true, IsValid: true, status, "signed and trusted");
        }

        if (unchecked((uint)status) == TrustENoSignature)
        {
            // "Not signed" and "signed but not trusted" are different problems. Reporting them the
            // same way tells the user something false about the file they have.
            return new AuthenticodeResult(
                IsSigned: false, IsValid: false, status, "the file is not signed");
        }

        // Everything else, including TRUST_E_SUBJECT_NOT_TRUSTED (0x800B0004), which is what a
        // self-signed certificate produces.
        return new AuthenticodeResult(
            IsSigned: true, IsValid: false, status, "the signature does not validate");
    }

    /// <summary>WINTRUST_ACTION_GENERIC_VERIFY_V2.</summary>
    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(
        IntPtr window,
        ref Guid actionId,
        IntPtr trustData);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        internal uint cbStruct;
        internal IntPtr pcwszFilePath;
        internal IntPtr hFile;
        internal IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        internal uint cbStruct;
        internal IntPtr pPolicyCallbackData;
        internal IntPtr pSIPClientData;
        internal uint dwUIChoice;
        internal uint fdwRevocationChecks;
        internal uint dwUnionChoice;
        internal IntPtr pFile;
        internal uint dwStateAction;
        internal IntPtr hWVTStateData;
        internal IntPtr pwszURLReference;
        internal uint dwProvFlags;
        internal uint dwUIContext;
        internal IntPtr pSignatureSettings;
    }
}