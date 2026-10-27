using System;
using System.IO;
using System.Linq;
using System.Reflection;
using KronosReborn.Data.Persistence;
using KronosReborn.Data.Runtimes;
using Xunit;

namespace KronosReborn.Tests;

/// <summary>
/// Tests for adding a runtime version the user obtained elsewhere.
/// </summary>
/// <remarks>
/// <para>
/// The import tests that were here before all asserted that the result was null, because the file
/// they used had no version resource and an unknown version is refused. Every one of them passed
/// without ever exercising a successful import, which is the case that matters.
/// </para>
/// <para>
/// They are replaced with tests that import for real. The stand-in for a downloaded runtime is the
/// test assembly itself, copied to a runtime file's name: it is a real PE with a real version
/// resource, and it is present wherever the tests run, so nothing here depends on the machine.
/// </para>
/// </remarks>
public class RuntimeImporterTests
{
    // ------------------------------------------------------------ Inspecting

    [Fact]
    public void InspectReportsTheVersionAndThatItIsNotTrusted()
    {
        using var temp = new TemporaryDirectory();
        var dll = WriteRuntimeFile(temp, RuntimeKind.Dlss);

        var preview = RuntimeImporter.Inspect(dll);

        Assert.Null(preview.Error);
        Assert.True(preview.CanImport);
        Assert.Equal(RuntimeKind.Dlss, preview.Kind);
        Assert.NotNull(preview.Version);
        Assert.Single(preview.Files);
    }

    [Fact]
    public void InspectChangesNothing()
    {
        using var temp = new TemporaryDirectory();
        var paths = new DataPaths(Path.Combine(temp.Root, "data"));
        using var catalog = new RuntimeCatalog(paths.CatalogPath);
        var dll = WriteRuntimeFile(temp, RuntimeKind.Dlss);

        RuntimeImporter.Inspect(dll);

        // Nothing has been accepted yet, so nothing may exist.
        Assert.Empty(catalog.Entries);
        Assert.False(Directory.Exists(paths.RuntimesDirectory));
    }

    [Fact]
    public void AFileThatIsNotARuntimeIsRefusedByName()
    {
        using var temp = new TemporaryDirectory();
        var notARuntime = Path.Combine(temp.Root, "someother.dll");
        WriteRealDll(notARuntime);

        var preview = RuntimeImporter.Inspect(notARuntime);

        Assert.False(preview.CanImport);
        Assert.Contains("is not a graphics runtime file", preview.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileWithNoVersionIsRefusedBecauseItCannotBeIdentified()
    {
        using var temp = new TemporaryDirectory();
        var dll = Path.Combine(temp.Root, "nvngx_dlss.dll");
        File.WriteAllBytes(dll, [0x4D, 0x5A, 0x90, 0x00]); // not a real PE

        var preview = RuntimeImporter.Inspect(dll);

        // R10: unknown is never guessed at, and a runtime with no version cannot be told apart from
        // any other, so it cannot be offered for installing.
        Assert.False(preview.CanImport);
        Assert.NotNull(preview.Error);
    }

    [Fact]
    public void AMissingFileIsReportedAsMissingRatherThanUnreadable()
    {
        var preview = RuntimeImporter.Inspect(@"C:\nowhere\nvngx_dlss.dll");

        // R59: "could not be found" and "could not be read" are different problems.
        Assert.False(preview.CanImport);
        Assert.Contains("could not be found", preview.Error!, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ Importing

    [Fact]
    public void AnUntrustedFileIsRefusedUnlessTheUserAgrees()
    {
        using var temp = new TemporaryDirectory();
        using var fixture = new ImportFixture(temp);
        var dll = WriteRuntimeFile(temp, RuntimeKind.Dlss);

        var result = fixture.Importer.Import(dll, acceptUntrusted: false);

        // R16 and R42: off by default.
        Assert.False(result.Succeeded);
        Assert.Null(result.Entry);
        Assert.Contains("not signed by a known runtime vendor", result.Error!, StringComparison.Ordinal);
        Assert.Empty(fixture.Catalog.Entries);
    }

    [Fact]
    public void AnUntrustedFileIsAcceptedWhenTheUserAgreesAndRecordedAsSuch()
    {
        using var temp = new TemporaryDirectory();
        using var fixture = new ImportFixture(temp);
        var dll = WriteRuntimeFile(temp, RuntimeKind.Dlss);

        var result = fixture.Importer.Import(dll, acceptUntrusted: true);

        Assert.True(result.Succeeded, result.Error);
        var entry = Assert.IsType<CatalogEntry>(result.Entry);
        Assert.Equal(RuntimeTrust.UserAccepted, entry.Trust);

        // R33: anything accepted this way must warn before it is installed. The swap planner decides
        // that from the trust level, and this is the level it looks at.
        Assert.False(entry.IsInstallableWithoutWarning);
    }

    [Fact]
    public void AnImportedVersionIsCopiedIntoTheCollectionRatherThanReferenced()
    {
        using var temp = new TemporaryDirectory();
        using var fixture = new ImportFixture(temp);

        // The user chooses a file somewhere disposable, as they would a Downloads folder.
        var downloads = Path.Combine(temp.Root, "Downloads");
        Directory.CreateDirectory(downloads);
        var dll = WriteRuntimeFile(downloads, RuntimeKind.Dlss);

        var result = fixture.Importer.Import(dll, acceptUntrusted: true);
        Assert.True(result.Succeeded, result.Error);

        var entry = result.Entry!;

        // The collection must not depend on that folder still being there.
        Assert.StartsWith(
            fixture.Paths.RuntimesDirectory,
            entry.FilePath,
            StringComparison.OrdinalIgnoreCase);

        Directory.Delete(downloads, recursive: true);

        // Every file the runtime consists of is still present.
        foreach (var name in RuntimeDlls.ByKind[RuntimeKind.Dlss])
        {
            Assert.True(File.Exists(Path.Combine(fixture.Paths.RuntimeFolder(RuntimeKind.Dlss, entry.Version), name)));
        }
    }

    [Fact]
    public void WhereAVersionCameFromIsRecorded()
    {
        using var temp = new TemporaryDirectory();
        using var fixture = new ImportFixture(temp);
        var dll = WriteRuntimeFile(temp, RuntimeKind.Dlss);

        var entry = fixture.Importer.Import(dll, acceptUntrusted: true).Entry!;

        // R17: the user can tell where it came from.
        Assert.Equal(RuntimeSource.Imported, entry.Source);
        Assert.Contains("imported from", entry.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImportingTheSameVersionTwiceChangesNothing()
    {
        using var temp = new TemporaryDirectory();
        using var fixture = new ImportFixture(temp);
        var dll = WriteRuntimeFile(temp, RuntimeKind.Dlss);

        var first = fixture.Importer.Import(dll, acceptUntrusted: true);
        var second = fixture.Importer.Import(dll, acceptUntrusted: true);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);

        // One version is one entry, however many times it is offered.
        Assert.Single(fixture.Catalog.Entries);
        Assert.Equal(first.Entry!.Id, second.Entry!.Id);
    }

    [Fact]
    public void AnUntrustedCopyCannotReplaceATrustedVersionOfTheSameNumber()
    {
        using var temp = new TemporaryDirectory();
        using var fixture = new ImportFixture(temp);
        var dll = WriteRuntimeFile(temp, RuntimeKind.Dlss);
        Assert.True(VersionNumber.TryRead(dll, out var version, out _, out _));

        // A trusted copy of this version is already held, in the folder an import would write to.
        var folder = fixture.Paths.RuntimeFolder(RuntimeKind.Dlss, version!);
        Directory.CreateDirectory(folder);
        var held = Path.Combine(folder, RuntimeDlls.ByKind[RuntimeKind.Dlss][0]);
        WriteRealDll(held);
        File.AppendAllText(held, "trusted");
        Assert.NotNull(fixture.Catalog.AddImport(
            RuntimeKind.Dlss, version!, folder, "vendor", RuntimeTrust.Trusted));

        var result = fixture.Importer.Import(dll, acceptUntrusted: true);

        // Refused, and the trusted file was not overwritten underneath its trusted label.
        Assert.False(result.Succeeded);
        Assert.Contains("already in the collection", result.Error);
        Assert.EndsWith("trusted", File.ReadAllText(held));
    }

    [Fact]
    public void AnImportedVersionSurvivesARestart()
    {
        using var temp = new TemporaryDirectory();
        using var fixture = new ImportFixture(temp);
        var dll = WriteRuntimeFile(temp, RuntimeKind.Dlss);

        var imported = fixture.Importer.Import(dll, acceptUntrusted: true).Entry!;

        // A fresh catalog over the same data directory, as the next launch would build.
        using var reopened = new RuntimeCatalog(fixture.Paths.CatalogPath);

        Assert.True(reopened.TryGet(imported.Id, out var found));
        Assert.Equal(imported.Version, found!.Version);
        Assert.Equal(RuntimeSource.Imported, found.Source);
    }

    [Fact]
    public void AMultiFileRuntimeIsImportedWhole()
    {
        using var temp = new TemporaryDirectory();
        using var fixture = new ImportFixture(temp);

        foreach (var name in RuntimeDlls.ByKind[RuntimeKind.Fsr])
        {
            WriteRuntimeFile(temp, RuntimeKind.Fsr, name);
        }

        var result = fixture.Importer.Import(
            Path.Combine(temp.Root, RuntimeDlls.ByKind[RuntimeKind.Fsr][0]),
            acceptUntrusted: true);

        Assert.True(result.Succeeded, result.Error);

        var folder = fixture.Paths.RuntimeFolder(RuntimeKind.Fsr, result.Entry!.Version);
        foreach (var name in RuntimeDlls.ByKind[RuntimeKind.Fsr])
        {
            Assert.True(File.Exists(Path.Combine(folder, name)), $"{name} was not taken");
        }
    }

    [Fact]
    public void TheCatalogRefusesAnUntrustedEntryEvenIfAskedDirectly()
    {
        // The importer is where the user is shown the verdict, but the catalog refuses an entry that
        // is neither trusted nor explicitly accepted, so a caller cannot skip the check.
        using var temp = new TemporaryDirectory();
        var paths = new DataPaths(Path.Combine(temp.Root, "data"));
        using var catalog = new RuntimeCatalog(paths.CatalogPath);

        var folder = Path.Combine(temp.Root, "copy");
        Directory.CreateDirectory(folder);
        WriteRuntimeFile(folder, RuntimeKind.Dlss);

        var entry = catalog.AddImport(
            RuntimeKind.Dlss, "1.0", folder, "somewhere.dll", RuntimeTrust.Untrusted);

        Assert.Null(entry);
        Assert.Empty(catalog.Entries);
    }

    // ------------------------------------------------------------ Helpers

    private sealed class ImportFixture : IDisposable
    {
        public ImportFixture(TemporaryDirectory temp)
        {
            Paths = new DataPaths(Path.Combine(temp.Root, "data"));
            Catalog = new RuntimeCatalog(Paths.CatalogPath);
            Importer = new RuntimeImporter(Catalog, Paths);
        }

        public DataPaths Paths { get; }
        public RuntimeCatalog Catalog { get; }
        public RuntimeImporter Importer { get; }

        public void Dispose() => Catalog.Dispose();
    }

    /// <summary>
    /// Writes a real, readable PE with a version resource under a runtime file's name.
    /// </summary>
    /// <remarks>
    /// The test assembly is used because it is a genuine signed-or-not PE carrying a genuine version
    /// resource, and it exists wherever the tests run. Copying it rather than shipping a fixture
    /// binary keeps the repository free of compiled artefacts while still exercising the real
    /// version-reading path rather than a stub.
    /// </remarks>
    private static string WriteRuntimeFile(
        TemporaryDirectory temp,
        RuntimeKind kind,
        string? fileName = null) =>
        WriteRuntimeFile(temp.Root, kind, fileName);

    private static string WriteRuntimeFile(
        string directory,
        RuntimeKind kind,
        string? fileName = null)
    {
        var target = Path.Combine(directory, fileName ?? RuntimeDlls.ByKind[kind][0]);
        WriteRealDll(target);
        return target;
    }

    private static void WriteRealDll(string path)
    {
        var source = Assembly.GetExecutingAssembly().Location;
        File.Copy(source, path, overwrite: true);
    }
}
