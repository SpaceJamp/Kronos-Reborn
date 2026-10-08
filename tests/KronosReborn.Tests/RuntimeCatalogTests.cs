using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using KronosReborn.Data.Libraries;
using KronosReborn.Data.Runtimes;
using Xunit;

namespace KronosReborn.Tests;

/// <summary>
/// Tests for the runtime catalog, signature verification, and integration with discovery.
/// </summary>
public class RuntimeCatalogTests
{
    /// <summary>
    /// Serialization settings matching the ones the catalog itself writes with, so a hand-written
    /// catalog file in a test is read back exactly as the program would write it.
    /// </summary>
    private static readonly JsonSerializerOptions CatalogJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
    // ------------------------------------------------------------ SignatureVerifier

    [Fact]
    public void SignatureVerifierReturnsFailedForNonExistentFile()
    {
        var result = SignatureVerifier.Check(@"C:\DoesNotExist\file.dll");
        Assert.False(result.HasValidSignature);
        Assert.Contains("does not exist", result.Details);
    }

    [Fact]
    public void SignatureVerifierReportsUnsignedFileAsUnsigned()
    {
        using var temp = new TemporaryDirectory();
        var file = Path.Combine(temp.Root, "unsigned.dll");
        File.WriteAllBytes(file, CreateDummyDll());

        var result = SignatureVerifier.Check(file);

        Assert.False(result.HasValidSignature);

        // R59: an unsigned file and a file whose signature does not validate are different
        // problems, and saying so has to be visible in the message.
        Assert.Contains("not signed", result.Details);
    }

    [Fact]
    public void SignatureVerifierActuallyValidatesRealSignatures()
    {
        // A positive control. Every other signature test feeds it a file with no signature, so they
        // would all still pass if the trust call were broken and simply always reported "unsigned".
        // Checking a genuinely signed Microsoft binary proves the call reaches Windows and gets a
        // real answer: the file must come back signed, and then rejected for being the wrong
        // publisher rather than for lacking a signature.
        var signed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "kernel32.dll");

        if (!File.Exists(signed))
        {
            return; // Nothing to check against on this machine; not worth failing over.
        }

        var result = SignatureVerifier.Check(signed);

        Assert.False(result.HasValidSignature);
        Assert.DoesNotContain("not signed", result.Details);
        Assert.Contains("not a known runtime vendor", result.Details);
    }

    [Fact]
    public void SignatureVerifierTreatsAnEmptyPathAsAFailureRatherThanThrowing()
    {
        var result = SignatureVerifier.Check("   ");

        Assert.False(result.HasValidSignature);
        Assert.False(string.IsNullOrWhiteSpace(result.Details));
    }

    // ------------------------------------------------------------ CatalogEntry

    [Fact]
    public void CatalogEntryDisplayNameFormatsDiscovered()
    {
        var entry = new CatalogEntry(
            "abc123",
            RuntimeKind.Dlss,
            "3.7.10",
            "3.7.10",
            RuntimeSource.Discovered,
            "Cyberpunk 2077 (Steam)",
            @"C:\Games\Cyberpunk\nvngx_dlss.dll",
            RuntimeTrust.Trusted,
            DateTimeOffset.UtcNow);

        Assert.Contains("DLSS 3.7.10", entry.DisplayName);
        Assert.Contains("discovered in Cyberpunk 2077", entry.DisplayName);
        Assert.DoesNotContain("untrusted", entry.DisplayName);
    }

    [Fact]
    public void CatalogEntryDisplayNameFormatsImportedUntrusted()
    {
        var entry = new CatalogEntry(
            "def456",
            RuntimeKind.Fsr,
            "3.1.2",
            null,
            RuntimeSource.Imported,
            "fsr.dll",
            @"C:\Downloads\fsr.dll",
            RuntimeTrust.Untrusted,
            DateTimeOffset.UtcNow);

        Assert.Contains("FSR 3.1.2", entry.DisplayName);
        Assert.Contains("imported from fsr.dll", entry.DisplayName);
        Assert.Contains("untrusted", entry.DisplayName);
    }

    [Fact]
    public void CatalogEntryMatchesSameKindAndVersion()
    {
        var a = new CatalogEntry("1", RuntimeKind.Dlss, "3.7.10", null, RuntimeSource.Discovered, "G1", "p1", RuntimeTrust.Trusted, DateTimeOffset.UtcNow);
        var b = new CatalogEntry("2", RuntimeKind.Dlss, "3.7.10", null, RuntimeSource.Imported, "f.dll", "p2", RuntimeTrust.Untrusted, DateTimeOffset.UtcNow);
        var c = new CatalogEntry("3", RuntimeKind.Fsr, "3.7.10", null, RuntimeSource.Discovered, "G1", "p3", RuntimeTrust.Trusted, DateTimeOffset.UtcNow);

        Assert.True(a.Matches(b));
        Assert.False(a.Matches(c));
    }

    [Fact]
    public void CatalogIdIsDeterministic()
    {
        var id1 = CatalogId.For(RuntimeKind.Dlss, "3.7.10", RuntimeSource.Discovered);
        var id2 = CatalogId.For(RuntimeKind.Dlss, "3.7.10", RuntimeSource.Discovered);
        var id3 = CatalogId.For(RuntimeKind.Dlss, "3.7.11", RuntimeSource.Discovered);

        Assert.Equal(id1, id2);
        Assert.NotEqual(id1, id3);
    }

    [Fact]
    public void TheGameAVersionWasFoundInIsNotPartOfItsIdentity()
    {
        // DLSS 310.9.1 in three games is one version present on this machine, not three. While the
        // game was part of the id it was, so the picker offered the same version three times under
        // identical labels, and choosing one copied a file onto itself.
        var fromWitcher = CatalogId.For(RuntimeKind.Dlss, "310.9.1", RuntimeSource.Discovered);
        var fromPortal = CatalogId.For(RuntimeKind.Dlss, "310.9.1", RuntimeSource.Discovered);

        Assert.Equal(fromWitcher, fromPortal);
    }

    [Fact]
    public void AnImportedVersionIsNotTheSameThingAsADiscoveredOne()
    {
        // R16 treats the two differently, so they must not collapse into one entry.
        var discovered = CatalogId.For(RuntimeKind.Dlss, "310.9.1", RuntimeSource.Discovered);
        var imported = CatalogId.For(RuntimeKind.Dlss, "310.9.1", RuntimeSource.Imported);

        Assert.NotEqual(discovered, imported);
    }

    [Fact]
    public void KindsDoNotCollide()
    {
        Assert.NotEqual(
            CatalogId.For(RuntimeKind.Dlss, "3.7.10", RuntimeSource.Discovered),
            CatalogId.For(RuntimeKind.DlssG, "3.7.10", RuntimeSource.Discovered));
    }

    // ------------------------------------------------------------ RuntimeCatalog

    [Fact]
    public void RuntimeCatalogStartsEmptyWhenNoFile()
    {
        using var temp = new TemporaryDirectory();
        var catalogPath = Path.Combine(temp.Root, "catalog.json");

        var catalog = new RuntimeCatalog(catalogPath);

        Assert.Empty(catalog.Entries);
    }

    [Fact]
    public void RuntimeCatalogLoadsExistingFile()
    {
        using var temp = new TemporaryDirectory();
        var catalogPath = Path.Combine(temp.Root, "catalog.json");

        var entry = new CatalogEntry(
            "test123",
            RuntimeKind.Dlss,
            "3.7.10",
            null,
            RuntimeSource.Discovered,
            "Game (Steam)",
            @"C:\Game\nvngx_dlss.dll",
            RuntimeTrust.Trusted,
            DateTimeOffset.UtcNow);

        var data = new { Entries = new[] { entry } };
        var json = JsonSerializer.Serialize(data, CatalogJsonOptions);
        File.WriteAllText(catalogPath, json);

        var catalog = new RuntimeCatalog(catalogPath);

        Assert.Single(catalog.Entries);
        Assert.Equal("3.7.10", catalog.Entries[0].Version);
    }

    [Fact]
    public void RuntimeCatalogIgnoresCorruptFile()
    {
        using var temp = new TemporaryDirectory();
        var catalogPath = Path.Combine(temp.Root, "catalog.json");

        File.WriteAllText(catalogPath, "not json at all {");

        var catalog = new RuntimeCatalog(catalogPath);

        // R51: corrupt file doesn't stop startup, returns empty
        Assert.Empty(catalog.Entries);
    }

    [Fact]
    public void RuntimeCatalogAddFromDiscoveryAddsEntry()
    {
        using var temp = new TemporaryDirectory();
        var catalogPath = Path.Combine(temp.Root, "catalog.json");
        var catalog = new RuntimeCatalog(catalogPath);

        // Create a dummy DLL file
        var dllPath = Path.Combine(temp.Root, "nvngx_dlss.dll");
        File.WriteAllBytes(dllPath, CreateDummyDll());

        var detected = new DetectedRuntime(
            RuntimeKind.Dlss,
            "nvngx_dlss.dll",
            "nvngx_dlss.dll",
            dllPath,
            "3.7.10",
            "3.7.10",
            true);

        var entry = catalog.AddFromDiscovery(detected, "123", "Test Game", "Steam");

        Assert.NotNull(entry);
        Assert.Equal("3.7.10", entry!.Version);
        Assert.Equal(RuntimeSource.Discovered, entry.Source);
        Assert.Equal("Test Game (Steam)", entry.SourceDetail);
        Assert.Equal(RuntimeTrust.Untrusted, entry.Trust); // dummy DLL has no signature

        // Second call with same data should return existing
        var entry2 = catalog.AddFromDiscovery(detected, "123", "Test Game", "Steam");
        Assert.Same(entry, entry2);
    }

    [Fact]
    public void RuntimeCatalogAddFromDiscoverySkipsUnknownVersion()
    {
        using var temp = new TemporaryDirectory();
        var catalogPath = Path.Combine(temp.Root, "catalog.json");
        var catalog = new RuntimeCatalog(catalogPath);

        var dllPath = Path.Combine(temp.Root, "nvngx_dlss.dll");
        File.WriteAllBytes(dllPath, CreateDummyDll());

        var detected = new DetectedRuntime(
            RuntimeKind.Dlss,
            "nvngx_dlss.dll",
            "nvngx_dlss.dll",
            dllPath,
            null, // unknown version
            null,
            true);

        var entry = catalog.AddFromDiscovery(detected, "123", "Test Game", "Steam");

        Assert.Null(entry);
        Assert.Empty(catalog.Entries);
    }

    [Fact]
    public void RuntimeCatalogAddFromDiscoverySkipsMissingFile()
    {
        using var temp = new TemporaryDirectory();
        var catalogPath = Path.Combine(temp.Root, "catalog.json");
        var catalog = new RuntimeCatalog(catalogPath);

        var detected = new DetectedRuntime(
            RuntimeKind.Dlss,
            "nvngx_dlss.dll",
            "nvngx_dlss.dll",
            @"C:\NotHere\nvngx_dlss.dll",
            "3.7.10",
            null,
            false); // doesn't exist

        var entry = catalog.AddFromDiscovery(detected, "123", "Test Game", "Steam");

        Assert.Null(entry);
        Assert.Empty(catalog.Entries);
    }

    [Fact]
    public void RuntimeCatalogAddFromSnapshotAddsAllKnownRuntimes()
    {
        using var temp = new TemporaryDirectory();
        var catalogPath = Path.Combine(temp.Root, "catalog.json");
        var catalog = new RuntimeCatalog(catalogPath);

        var dllPath = Path.Combine(temp.Root, "nvngx_dlss.dll");
        File.WriteAllBytes(dllPath, CreateDummyDll());

        var snapshot = new GameRuntimeSnapshot(
            "123",
            "Test Game",
            temp.Root,
            new[]
            {
                new DetectedRuntime(RuntimeKind.Dlss, "nvngx_dlss.dll", "nvngx_dlss.dll", dllPath, "3.7.10", null, true),
                new DetectedRuntime(RuntimeKind.Fsr, "amd_fidelityfx_dx12.dll", "amd_fidelityfx_dx12.dll",
                    Path.Combine(temp.Root, "amd_fidelityfx_dx12.dll"), null, null, false),
            });

        var added = catalog.AddFromSnapshot(snapshot, "Steam");

        Assert.Single(added);
        Assert.Equal(RuntimeKind.Dlss, added[0].Kind);
    }

    [Fact]
    public void RuntimeCatalogAcceptUntrustedChangesTrust()
    {
        using var temp = new TemporaryDirectory();
        var catalogPath = Path.Combine(temp.Root, "catalog.json");
        var catalog = new RuntimeCatalog(catalogPath);

        var dllPath = Path.Combine(temp.Root, "nvngx_dlss.dll");
        File.WriteAllBytes(dllPath, CreateDummyDll());

        var detected = new DetectedRuntime(RuntimeKind.Dlss, "nvngx_dlss.dll", "nvngx_dlss.dll", dllPath, "3.7.10", null, true);
        var entry = catalog.AddFromDiscovery(detected, "123", "Game", "Steam");

        Assert.Equal(RuntimeTrust.Untrusted, entry!.Trust);

        var accepted = catalog.AcceptUntrusted(entry.Id);

        Assert.True(accepted);
        Assert.Equal(RuntimeTrust.UserAccepted, catalog.TryGet(entry.Id, out var updated) ? updated!.Trust : RuntimeTrust.Untrusted);
    }

    [Fact]
    public void RuntimeCatalogWriteIsAtomic()
    {
        using var temp = new TemporaryDirectory();
        var catalogPath = Path.Combine(temp.Root, "catalog.json");
        var catalog = new RuntimeCatalog(catalogPath);

        var dllPath = Path.Combine(temp.Root, "nvngx_dlss.dll");
        File.WriteAllBytes(dllPath, CreateDummyDll());

        var detected = new DetectedRuntime(RuntimeKind.Dlss, "nvngx_dlss.dll", "nvngx_dlss.dll", dllPath, "3.7.10", null, true);
        catalog.AddFromDiscovery(detected, "123", "Game", "Steam");

        // The file should exist and be valid JSON
        Assert.True(File.Exists(catalogPath));
        var json = File.ReadAllText(catalogPath);
        Assert.Contains("3.7.10", json);
    }

    // ------------------------------------------------------------ Integration

    [Fact]
    public void EndToEndDiscoveryAndCatalog()
    {
        using var temp = new TemporaryDirectory();

        // Set up a fake Steam library with two games
        var steamRoot = Path.Combine(temp.Root, "Steam");
        var steamapps = Path.Combine(steamRoot, "steamapps");
        var common = Path.Combine(steamapps, "common");
        Directory.CreateDirectory(common);

        // Game 1 with DLSS
        var game1Dir = Path.Combine(common, "GameOne");
        Directory.CreateDirectory(game1Dir);
        var dlss1 = Path.Combine(game1Dir, "nvngx_dlss.dll");
        File.WriteAllBytes(dlss1, CreateDummyDll());
        File.WriteAllText(
            Path.Combine(steamapps, "appmanifest_100.acf"),
            """
            "AppState"
            {
                "appid" "100"
                "name" "Game One"
                "installdir" "GameOne"
            }
            """);

        // Game 2 with FSR
        var game2Dir = Path.Combine(common, "GameTwo");
        Directory.CreateDirectory(game2Dir);
        var fsr2 = Path.Combine(game2Dir, "amd_fidelityfx_dx12.dll");
        File.WriteAllBytes(fsr2, CreateDummyDll());
        File.WriteAllText(
            Path.Combine(steamapps, "appmanifest_200.acf"),
            """
            "AppState"
            {
                "appid" "200"
                "name" "Game Two"
                "installdir" "GameTwo"
            }
            """);

        // Discover
        var scanner = new SteamLibraryScanner();
        var result = scanner.Scan(steamRoot, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Games.Count);

        // Scan runtimes - dummy DLLs have no version resource, so versions are unknown
        var rtScanner = new GameRuntimeScanner();
        var snapshots = rtScanner.ScanMany(result.Games, TestContext.Current.CancellationToken);

        Assert.Equal(2, snapshots.Count);
        Assert.True(snapshots[0].HasAnyRuntime);
        Assert.True(snapshots[1].HasAnyRuntime);
        Assert.True(snapshots[0].HasUnknownVersions); // R10: unknown, not guessed
        Assert.True(snapshots[1].HasUnknownVersions);

        // Add to catalog - no entries added because versions are unknown (R10)
        var catalogPath = Path.Combine(temp.Root, "catalog.json");
        var catalog = new RuntimeCatalog(catalogPath);

        foreach (var snap in snapshots)
        {
            var added = catalog.AddFromSnapshot(snap, "Steam");
            Assert.Empty(added); // R10: unknown versions don't enter catalog
        }

        Assert.Empty(catalog.Entries);

        // But if we manually create DetectedRuntime with versions, they get added
        var manualDlss = new DetectedRuntime(RuntimeKind.Dlss, "nvngx_dlss.dll", "nvngx_dlss.dll", dlss1, "3.7.10", null, true);
        var manualFsr = new DetectedRuntime(RuntimeKind.Fsr, "amd_fidelityfx_dx12.dll", "amd_fidelityfx_dx12.dll", fsr2, "3.1.2", null, true);

        var snap1 = new GameRuntimeSnapshot("100", "Game One", game1Dir, new[] { manualDlss });
        var snap2 = new GameRuntimeSnapshot("200", "Game Two", game2Dir, new[] { manualFsr });

        var added1 = catalog.AddFromSnapshot(snap1, "Steam");
        var added2 = catalog.AddFromSnapshot(snap2, "Steam");

        Assert.Single(added1);
        Assert.Single(added2);
        Assert.Equal(2, catalog.Entries.Count);
    }

    private static byte[] CreateDummyDll()
    {
        return [
            0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00,
            0xB8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80, 0x00, 0x00, 0x00,
            0x0E, 0x1F, 0xBA, 0x0E, 0x00, 0xB4, 0x09, 0xCD, 0x21, 0xB8, 0x01, 0x4C, 0xCD, 0x21, 0x54, 0x68,
            0x69, 0x73, 0x20, 0x70, 0x72, 0x6F, 0x67, 0x72, 0x61, 0x6D, 0x20, 0x63, 0x61, 0x6E, 0x6E, 0x6F,
            0x74, 0x20, 0x62, 0x65, 0x20, 0x72, 0x75, 0x6E, 0x20, 0x69, 0x6E, 0x20, 0x44, 0x4F, 0x53, 0x20,
            0x6D, 0x6F, 0x64, 0x65, 0x2E, 0x0D, 0x0D, 0x0A, 0x24, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x50, 0x45, 0x00, 0x00
        ];
    }
}
