using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace KronosReborn.Data.Runtimes;

/// <summary>
/// Result of checking a file's digital signature.
/// </summary>
/// <param name="HasValidSignature">Whether the file has a valid signature from a trusted publisher.</param>
/// <param name="PublisherName">The publisher name from the certificate, if any.</param>
/// <param name="Details">Human-readable details for the catalog.</param>
public sealed record SignatureCheckResult(
    bool HasValidSignature,
    string? PublisherName,
    string Details)
{
    public static SignatureCheckResult Failed(string reason) =>
        new(false, null, $"No valid signature: {reason}");

    public static SignatureCheckResult Success(string publisher) =>
        new(true, publisher, $"Signed by {publisher}");
}

/// <summary>
/// Verifies the Authenticode signature of a runtime DLL.
/// </summary>
/// <remarks>
/// <para>
/// R16: imported runtimes are checked for a valid digital signature before being accepted, unless
/// the user has explicitly enabled accepting untrusted ones.
/// </para>
/// <para>
/// <b>This previously did not work.</b> It read the signing certificate out of the file and built an
/// X.509 chain with <c>AllowUnknownCertificateAuthority</c> and no revocation checking, then
/// declared the file trusted if any part of the certificate subject contained a vendor's name.
/// That combination accepts a self-signed certificate, and a self-signed certificate can say
/// anything: a file could carry a certificate reading <c>CN=NVIDIA Corporation</c> and be reported
/// as signed by NVIDIA. Nothing in that path asked whether the signature was valid or whether the
/// publisher was really the vendor.
/// </para>
/// <para>
/// It now uses the Windows trust provider, which is the only thing on this platform that can say
/// whether a signature actually validates and whether it chains to a real root, and it matches the
/// publisher against a configured allow list that is checked on equality rather than by substring.
/// A revoked or untrusted-root signature is reported as such instead of passing.
/// </para>
/// </remarks>
public static class SignatureVerifier
{
    /// <summary>
    /// Publisher names accepted as genuine sources of a graphics runtime.
    /// </summary>
    /// <remarks>
    /// Matched exactly (ignoring case and surrounding whitespace) against the certificate subject's
    /// organisation or common name. Substring matching is what made the old check forgeable: a
    /// certificate claiming to be from "Definitely Not NVIDIA Corporation Ltd" satisfied it.
    /// </remarks>
    private static readonly string[] TrustedPublishers =
    [
        "NVIDIA Corporation",
        "Advanced Micro Devices, Inc.",
        "Advanced Micro Devices Inc",
        "Intel Corporation",
    ];

    /// <summary>
    /// Checks whether a file carries a valid Authenticode signature from a known runtime publisher.
    /// </summary>
    public static SignatureCheckResult Check(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return SignatureCheckResult.Failed("no file was given");
        }

        if (!File.Exists(filePath))
        {
            return SignatureCheckResult.Failed("file does not exist");
        }

        try
        {
#if WINDOWS
            var signature = Authenticode.Verify(filePath);
#else
            var signature = new AuthenticodeResult(false, false, 0, "not supported on this platform");
#endif

            if (!signature.IsSigned)
            {
                return SignatureCheckResult.Failed(signature.Describe());
            }

            if (!signature.IsValid)
            {
                // A signature that exists but does not validate is a different situation from no
                // signature at all, and R59 is explicit that the two must not be reported the same
                // way: one is a broken or forged file, the other is an unsigned one.
                return SignatureCheckResult.Failed(signature.Describe());
            }

            var publisher = ReadPublisher(filePath);

            if (string.IsNullOrWhiteSpace(publisher))
            {
                return SignatureCheckResult.Failed("could not determine the publisher");
            }

            var isTrusted = TrustedPublishers.Any(trusted =>
                string.Equals(publisher.Trim(), trusted, StringComparison.OrdinalIgnoreCase));

            return isTrusted
                ? SignatureCheckResult.Success(publisher.Trim())
                : SignatureCheckResult.Failed($"publisher \"{publisher}\" is not a known runtime vendor");
        }
        catch (FileNotFoundException)
        {
            return SignatureCheckResult.Failed("file does not exist");
        }
        catch (IOException ex)
        {
            return SignatureCheckResult.Failed($"could not read file: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return SignatureCheckResult.Failed($"could not read file: {ex.Message}");
        }
        catch (CryptographicException ex)
        {
            return SignatureCheckResult.Failed($"could not read the signature: {ex.Message}");
        }
    }

    /// <summary>
    /// The publisher named by the file's signing certificate.
    /// </summary>
    /// <remarks>
    /// Read from the file rather than from the trust provider's state, which would mean unpacking
    /// internal structures for one string. The certificate has already been validated by this point,
    /// so reading its subject adds no trust of its own — it only names the publisher to show.
    ///
    /// The certificate inside an Authenticode-signed executable is a PKCS#7 blob in the PE's security
    /// directory, not a bare certificate, so the loader that reads it is
    /// <see cref="X509Certificate.CreateFromSignedFile"/>. That is marked obsolete in favour of
    /// <c>X509CertificateLoader</c>, which can read a DER or PEM file but cannot read the signer out
    /// of a signed executable, so there is no replacement to move to. The warning is disabled for
    /// this one call. A failure here costs the publisher name only; it never changes the verdict,
    /// which comes from <see cref="Authenticode"/>.
    /// </remarks>
    private static string? ReadPublisher(string filePath)
    {
        try
        {
#pragma warning disable SYSLIB0057 // No loader exists for an Authenticode signer in a PE file.
            using var certificate = X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057

            return ExtractPublisherName(certificate.Subject);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Pulls the publisher out of a certificate subject.
    /// </summary>
    /// <remarks>
    /// Prefers the organisation, then the common name. Subject strings are
    /// "CN=..., O=..., L=..., C=..." and the organisation is the field that identifies the publisher;
    /// taking whichever came first meant a certificate could put a misleading value in CN.
    /// </remarks>
    private static string? ExtractPublisherName(string subject)
    {
        string? organisation = null;
        string? commonName = null;

        foreach (var part in subject.Split(
                     ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (organisation is null && part.StartsWith("O=", StringComparison.OrdinalIgnoreCase))
            {
                organisation = part[2..];
            }
            else if (commonName is null && part.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            {
                commonName = part[3..];
            }
        }

        return organisation ?? commonName;
    }
}
