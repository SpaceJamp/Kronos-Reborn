using System;
using System.IO;
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
/// Verifies the digital signature of a runtime DLL.
/// </summary/// <remarks>
/// R16: imported runtimes are checked for a valid digital signature before being accepted.
/// The known publishers are NVIDIA Corporation, Advanced Micro Devices Inc, and Intel Corporation.
/// A file without a valid signature from one of these is Untrusted.
/// </remarks>
public static class SignatureVerifier
{
    private static readonly string[] TrustedPublishers =
    {
        "NVIDIA Corporation",
        "Advanced Micro Devices Inc",
        "Intel Corporation",
    };

    /// <summary>
    /// Checks whether a file has a valid signature from a known runtime publisher.
    /// </summary>
    public static SignatureCheckResult Check(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return SignatureCheckResult.Failed("file does not exist");
        }

        try
        {
            var signature = X509Certificate.CreateFromSignedFile(filePath);

            if (signature is not X509Certificate2 cert)
            {
                return SignatureCheckResult.Failed("not a valid signed file");
            }

            // Check the certificate chain
            var chain = new X509Chain
            {
                ChainPolicy =
                {
                    RevocationMode = X509RevocationMode.NoCheck, // Offline-friendly
                    VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority
                }
            };

            var isValid = chain.Build(cert);

            if (!isValid)
            {
                var errors = string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation));
                return SignatureCheckResult.Failed($"chain validation failed: {errors}");
            }

            var subject = cert.Subject;
            var publisher = ExtractPublisherName(subject);

            if (string.IsNullOrWhiteSpace(publisher))
            {
                return SignatureCheckResult.Failed("could not determine publisher");
            }

            var isTrusted = TrustedPublishers.Any(tp =>
                publisher.Contains(tp, StringComparison.OrdinalIgnoreCase));

            return isTrusted
                ? SignatureCheckResult.Success(publisher)
                : SignatureCheckResult.Failed($"publisher \"{publisher}\" is not a known runtime vendor");
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            return SignatureCheckResult.Failed($"not signed or invalid signature: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SignatureCheckResult.Failed($"could not read file: {ex.Message}");
        }
    }

    private static string? ExtractPublisherName(string subject)
    {
        // Subject format: "CN=NVIDIA Corporation, O=NVIDIA Corporation, L=Santa Clara, S=California, C=US"
        // or similar. We want the CN or O value.
        foreach (var part in subject.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            {
                return part[3..];
            }
            if (part.StartsWith("O=", StringComparison.OrdinalIgnoreCase))
            {
                return part[2..];
            }
        }
        return null;
    }
}