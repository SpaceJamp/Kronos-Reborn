using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KronosReborn.Data.Logging;
using KronosReborn.Data.Persistence;

namespace KronosReborn.Data.Runtimes;

/// <summary>
/// What an import would do, before it is done.
/// </summary>
/// <remarks>
/// Inspecting separately from importing is what lets the user see the version, the publisher and the
/// signature verdict before anything is accepted. R16 requires an import to be checked before it is
/// taken, and safety principle 5 is warn before rather than after — which is only possible if there
/// is a moment between finding the file and acting on it.
/// </remarks>
/// <param name="CanImport">Whether the import would succeed as things stand.</param>
/// <param name="Kind">Which runtime the file belongs to, when it could be determined.</param>
/// <param name="FileName">The file the user chose.</param>
/// <param name="Version">The version it declares, normalised.</param>
/// <param name="Publisher">The publisher on its certificate, when it has one.</param>
/// <param name="Trust">How the signature was judged.</param>
/// <param name="Files">Every file the import would take.</param>
/// <param name="MissingFiles">Parts of the runtime that were not alongside the chosen file.</param>
/// <param name="NeedsAcceptance">
/// True when the files are readable but not trusted, so the user must agree before they are taken.
/// </param>
/// <param name="Error">Why it cannot be imported at all, in terms the user can act on.</param>
public sealed record RuntimeImportPreview(
    bool CanImport,
    RuntimeKind? Kind,
    string FileName,
    string? Version,
    string? Publisher,
    RuntimeTrust Trust,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> MissingFiles,
    bool NeedsAcceptance,
    string? Error)
{
    /// <summary>A one-line summary for the status bar.</summary>
    public string Summary
    {
        get
        {
            if (Error is not null)
            {
                return $"{FileName}: {Error}";
            }

            var name = Kind is null ? FileName : RuntimeKindNames.Display(Kind.Value);
            var version = Version is null ? "unknown version" : Version;
            var publisher = Publisher is null ? "no publisher" : Publisher;

            return NeedsAcceptance
                ? $"{name} {version} from {FileName} is not trusted ({publisher}). It can be added, " +
                  "but a warning will be shown before it is installed into a game."
                : $"{name} {version} from {FileName}, signed by {publisher}.";
        }
    }
}

/// <summary>
/// The outcome of an import.
/// </summary>
/// <param name="Succeeded">Whether the version entered the collection.</param>
/// <param name="Entry">The catalog entry it produced.</param>
/// <param name="Error">Why it did not, when it did not.</param>
public sealed record RuntimeImportResult(
    bool Succeeded,
    CatalogEntry? Entry,
    string? Error)
{
    public static RuntimeImportResult Failed(string error) => new(false, null, error);
}

/// <summary>
/// Adds runtime versions the user obtained elsewhere into the collection.
/// </summary>
/// <remarks>
/// <para>
/// This is the only sanctioned route for a version this machine does not already have, and it is the
/// one the specification chose. Downloading from a mirror was considered and rejected: it would make
/// the program depend on a service someone else runs, which C3 forbids, and it would put a
/// third-party CDN inside the trust boundary that <see cref="SignatureVerifier"/> exists to keep
/// outside. The user fetches the file; the program checks it before it can reach a game.
/// </para>
/// <para>
/// A runtime kind is a unit, and FSR is two files that only work as a pair. So the chosen file is
/// treated as the way to name a folder rather than as the whole import: every other file of that kind
/// sitting beside it is taken too, and if any are missing the import is refused with their names
/// rather than accepted as a runtime that would not work.
/// </para>
/// <para>
/// What is taken is copied into the collection's own folder under the data directory. The collection
/// then owns its files, so it keeps working when the folder the user downloaded to is moved or
/// deleted — which, for a Downloads folder, is a matter of when rather than if.
/// </para>
/// </remarks>
public sealed class RuntimeImporter
{
    private readonly RuntimeCatalog _catalog;
    private readonly DataPaths _paths;

    public RuntimeImporter(RuntimeCatalog catalog, DataPaths paths)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    /// <summary>
    /// Works out what importing a file would do, without changing anything.
    /// </summary>
    /// <remarks>
    /// Static because it reads only the chosen file: inspecting must be possible before anything
    /// exists to inspect, and before the user has agreed to anything.
    /// </remarks>
    public static RuntimeImportPreview Inspect(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return Refuse("(no file was chosen)");
        }

        if (!File.Exists(filePath))
        {
            return Refuse($"{filePath} could not be found.");
        }

        var fileName = Path.GetFileName(filePath);
        var kind = RuntimeDlls.KindFor(fileName);

        if (kind is null)
        {
            return Refuse(
                $"{fileName} is not a graphics runtime file. Choose one of: " +
                string.Join(", ", RuntimeDlls.AllFileNames) + ".");
        }

        if (!VersionNumber.TryRead(filePath, out var version, out _, out var readError))
        {
            return Refuse(
                $"The version of {fileName} could not be read, so it is not known what this file is " +
                $"({readError}).");
        }

        if (version is null)
        {
            // R10: an unreadable version is unknown and is never guessed at. Without a version there
            // is nothing to install and nothing to compare, so it cannot enter the collection.
            return Refuse(
                $"{fileName} declares no version, so it cannot be identified. A runtime with no " +
                "version cannot be told apart from any other.");
        }

        var folder = Path.GetDirectoryName(filePath) ?? string.Empty;
        var wanted = RuntimeDlls.ByKind[kind.Value];
        var present = wanted.Where(name => File.Exists(Path.Combine(folder, name))).ToArray();
        var missing = wanted.Where(name => !present.Contains(name, StringComparer.OrdinalIgnoreCase)).ToArray();

        var files = present.Select(name => Path.Combine(folder, name)).ToArray();

        // Every file is taken, so every file is judged: a signed file must not vouch for an unsigned
        // one beside it, and a pair whose halves are different versions is not one runtime.
        foreach (var sibling in files)
        {
            if (VersionNumber.TryRead(sibling, out var siblingVersion, out _, out _)
                && !VersionNumber.AreEquivalent(siblingVersion, version))
            {
                return Refuse(
                    $"{Path.GetFileName(sibling)} is version {siblingVersion ?? "unknown"} but {fileName} is " +
                    $"{version}. The files of {RuntimeKindNames.Display(kind.Value)} only work as a matched set.");
            }
        }

        var signature = SignatureVerifier.Check(filePath);
        var trust = files.All(file => SignatureVerifier.Check(file).HasValidSignature)
            ? RuntimeTrust.Trusted
            : RuntimeTrust.Untrusted;

        return new RuntimeImportPreview(
            CanImport: missing.Length == 0,
            Kind: kind,
            FileName: fileName,
            Version: version,
            Publisher: signature.PublisherName,
            Trust: trust,
            Files: files,
            MissingFiles: missing,
            NeedsAcceptance: missing.Length == 0 && trust == RuntimeTrust.Untrusted,
            Error: missing.Length == 0
                ? null
                : $"{RuntimeKindNames.Display(kind.Value)} is made of {wanted.Length} files, and " +
                  $"{string.Join(" and ", missing)} " +
                  (missing.Length == 1 ? "is" : "are") + " not in the same folder. Put " +
                  string.Join(" and ", wanted) + " together and choose any one of them.");
    }

    /// <summary>
    /// Adds a runtime version to the collection, after copying every file it consists of.
    /// </summary>
    /// <param name="filePath">The file the user chose. Its siblings are taken with it.</param>
    /// <param name="acceptUntrusted">
    /// Whether to accept files whose signature could not be attributed to a runtime vendor. R42 keeps
    /// this off by default, and R33 requires a warning at swap time for anything accepted under it.
    /// </param>
    public RuntimeImportResult Import(string filePath, bool acceptUntrusted)
    {
        var preview = Inspect(filePath);

        if (preview.Error is not null)
        {
            return RuntimeImportResult.Failed(preview.Error);
        }

        if (preview.NeedsAcceptance && !acceptUntrusted)
        {
            // R16: not accepted without the user's explicit agreement.
            return RuntimeImportResult.Failed(
                $"{preview.FileName} is not signed by a known runtime vendor" +
                (preview.Publisher is null ? string.Empty : $" (publisher: {preview.Publisher})") +
                $". {signatureReason}");
        }

        var kind = preview.Kind!.Value;
        var version = preview.Version!;
        var trust = acceptUntrusted ? RuntimeTrust.UserAccepted : RuntimeTrust.Trusted;

        // The collection folder is shared by every import of a version, so copying first would put this
        // file under whatever trust the existing entry has. Only a trusted copy may replace another.
        if (_catalog.TryGet(CatalogId.For(kind, version, RuntimeSource.Imported), out var existing)
            && existing is not null
            && (existing.Trust == RuntimeTrust.Trusted || trust != RuntimeTrust.Trusted))
        {
            return existing.Trust == RuntimeTrust.Trusted && trust != RuntimeTrust.Trusted
                ? RuntimeImportResult.Failed(
                    $"{RuntimeKindNames.Display(kind)} {version} is already in the collection, signed by " +
                    "its vendor. This unsigned copy was not added, so it cannot replace it.")
                : new RuntimeImportResult(true, existing, null);
        }

        try
        {
            var destination = _paths.RuntimeFolder(kind, version);
            Directory.CreateDirectory(destination);

            // Copy rather than reference. The collection owns what it offers, so deleting or moving
            // the folder the file was downloaded to does not empty it.
            foreach (var source in preview.Files)
            {
                File.Copy(source, Path.Combine(destination, Path.GetFileName(source)), overwrite: true);
            }

            var entry = _catalog.AddImport(
                kind,
                version,
                destination,
                Path.GetFileName(filePath),
                trust);

            if (entry is null)
            {
                return RuntimeImportResult.Failed(
                    $"{RuntimeKindNames.Display(kind)} {version} could not be added to the collection.");
            }

            Log.Instance.Information(
                $"Imported {RuntimeKindNames.Display(kind)} {version} from {Path.GetFileName(filePath)}",
                category: "Import",
                ("FilePath", filePath),
                ("Kind", kind.ToString()),
                ("Version", version),
                ("Trust", trust.ToString()));

            return new RuntimeImportResult(true, entry, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Instance.Error(
                $"Failed to import {preview.FileName}: {ex.Message}",
                category: "Import",
                ("FilePath", filePath),
                ("Kind", kind.ToString()),
                ("Version", version),
                ("Error", ex.Message));
            return RuntimeImportResult.Failed(
                $"{RuntimeKindNames.Display(kind)} {version} could not be added: " +
                $"{ex.GetType().Name}: {ex.Message}. " +
                "Check that the collection folder is writable.");
        }
    }

    /// <summary>The consequence of accepting an untrusted file, stated where the decision is made.</summary>
    private const string signatureReason =
        "It can be added if you accept that, and a warning will be shown every time it is installed.";

    private static RuntimeImportPreview Refuse(string error) => new(
        CanImport: false,
        Kind: null,
        FileName: string.Empty,
        Version: null,
        Publisher: null,
        Trust: RuntimeTrust.Untrusted,
        Files: [],
        MissingFiles: [],
        NeedsAcceptance: false,
        Error: error);
}
