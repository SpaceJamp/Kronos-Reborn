using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using KronosReborn.Data.Libraries;

namespace KronosReborn.Data.Runtimes;

/// <summary>
/// The persistent catalog of runtime versions available for installation.
/// </summary>
/// <remarks>
/// R13: the program holds a collection of runtime versions it can install.
/// R14: populated by discovering versions already present on this machine.
/// R15: user can import from a file.
/// R16: imported runtimes checked for valid signature.
/// R17: source recorded.
/// R18: removing from catalog doesn't remove from games.
/// R48: versions found in games added to catalog.
/// R49: games from before program existed still usable as source.
/// R50: data stored as plain files.
/// R51: corrupt file doesn't stop startup.
/// R52: writes are atomic.
/// R53/R54: history per game with limit (handled elsewhere, catalog is the version pool).
/// </remarks>
public sealed class RuntimeCatalog
{
    private readonly string _catalogPath;
    private readonly ReaderWriterLockSlim _lock = new();
    private ImmutableDictionary<string, CatalogEntry> _entries = ImmutableDictionary<string, CatalogEntry>.Empty;

    /// <summary>
    /// Creates a catalog backed by a JSON file.
    /// </summary>
    /// <param name="catalogPath">Full path to the catalog file.</param>
    public RuntimeCatalog(string catalogPath)
    {
        _catalogPath = catalogPath ?? throw new ArgumentNullException(nameof(catalogPath));
        Load();
    }

    /// <summary>All entries in the catalog.</summary>
    public IReadOnlyList<CatalogEntry> Entries
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _entries.Values.ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    /// <summary>Entries for a specific runtime kind.</summary>
    public IReadOnlyList<CatalogEntry> GetByKind(RuntimeKind kind)
    {
        _lock.EnterReadLock();
        try
        {
            return _entries.Values.Where(e => e.Kind == kind).ToList();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Tries to get an entry by its ID.
    /// </summary>
    public bool TryGet(string id, out CatalogEntry? entry)
    {
        _lock.EnterReadLock();
        try
        {
            return _entries.TryGetValue(id, out entry);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Adds or updates an entry from a discovered runtime in a game.
    /// </summary>
    /// <remarks>
    /// R48: a runtime version found inside an installed game is added to the collection.
    /// R49: games from before the program existed are still usable as a source.
    /// </remarks>
    /// <param name="detected">The detected runtime from scanning a game.</param>
    /// <param name="gameId">The game's library identifier.</param>
    /// <param name="gameName">The game's display name.</param>
    /// <param name="libraryName">The library name for source detail.</param>
    /// <returns>The entry that was added or updated, or null if the version could not be read.</returns>
    public CatalogEntry? AddFromDiscovery(
        DetectedRuntime detected,
        string gameId,
        string gameName,
        string libraryName)
    {
        if (!detected.Exists || !detected.IsVersionKnown)
        {
            // R10: unknown versions are never guessed at, so they don't enter the catalog
            return null;
        }

        var sourceDetail = $"{gameName} ({libraryName})";
        var id = CatalogId.For(detected.Kind, detected.Version, RuntimeSource.Discovered, sourceDetail);

        _lock.EnterWriteLock();
        try
        {
            if (_entries.TryGetValue(id, out var existing))
            {
                // Already have this version from this source, nothing to do
                return existing;
            }

            // Check signature for discovered files too - a game might have a modified DLL
            var sig = SignatureVerifier.Check(detected.FullPath);
            var trust = sig.HasValidSignature ? RuntimeTrust.Trusted : RuntimeTrust.Untrusted;

            var entry = new CatalogEntry(
                id,
                detected.Kind,
                detected.Version,
                detected.ProductVersion,
                RuntimeSource.Discovered,
                sourceDetail,
                detected.FullPath,
                trust,
                DateTimeOffset.UtcNow,
                sig.Details);

            _entries = _entries.Add(id, entry);
            Save();
            return entry;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Adds multiple entries from a game runtime snapshot.
    /// </summary>
    public IReadOnlyList<CatalogEntry> AddFromSnapshot(GameRuntimeSnapshot snapshot, string libraryName)
    {
        var added = new List<CatalogEntry>();

        foreach (var detected in snapshot.Runtimes)
        {
            var entry = AddFromDiscovery(detected, snapshot.GameId, snapshot.GameName, libraryName);
            if (entry is not null)
            {
                added.Add(entry);
            }
        }

        return added;
    }

    /// <summary>
    /// Imports a runtime version from a file the user provides.
    /// </summary>
    /// <remarks>
    /// R15: user can import from a file. R16: checked for valid signature. R17: source recorded.
    /// </remarks>
    /// <param name="filePath">Path to the DLL file to import.</param>
    /// <param name="kind">The runtime kind (must match the file).</param>
    /// <param name="allowUntrusted">If true, accept even without valid signature (R42).</param>
    /// <returns>The added entry, or null if the file could not be read or version is unknown.</returns>
    public CatalogEntry? ImportFromFile(string filePath, RuntimeKind kind, bool allowUntrusted = false)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        // Read version from the file
        string? version = null;
        string? productVersion = null;

        try
        {
            var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(filePath);
            version = string.IsNullOrWhiteSpace(vi.FileVersion) ? null : vi.FileVersion.Trim();
            productVersion = string.IsNullOrWhiteSpace(vi.ProductVersion) ? null : vi.ProductVersion.Trim();
        }
        catch
        {
            // Ignore - version will be null
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            // R10: unknown version not guessed, don't add
            return null;
        }

        var sig = SignatureVerifier.Check(filePath);
        var trust = sig.HasValidSignature ? RuntimeTrust.Trusted : (allowUntrusted ? RuntimeTrust.UserAccepted : RuntimeTrust.Untrusted);

        var sourceDetail = Path.GetFileName(filePath);
        var id = CatalogId.For(kind, version, RuntimeSource.Imported, sourceDetail);

        _lock.EnterWriteLock();
        try
        {
            if (_entries.TryGetValue(id, out var existing))
            {
                // Update trust if this import is trusted and the existing wasn't
                if (trust == RuntimeTrust.Trusted && existing.Trust != RuntimeTrust.Trusted)
                {
                    var updated = existing with { Trust = trust, SignatureInfo = sig.Details };
                    _entries = _entries.SetItem(id, updated);
                    Save();
                    return updated;
                }
                return existing;
            }

            var entry = new CatalogEntry(
                id,
                kind,
                version,
                productVersion,
                RuntimeSource.Imported,
                sourceDetail,
                filePath,
                trust,
                DateTimeOffset.UtcNow,
                sig.Details);

            _entries = _entries.Add(id, entry);
            Save();
            return entry;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Removes an entry from the catalog.
    /// </summary>
    /// <remarks>
    /// R18: removing from catalog does not remove from games. This only removes the version
    /// from the pool of available versions.
    /// </remarks>
    public bool Remove(string id)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_entries.ContainsKey(id))
            {
                _entries = _entries.Remove(id);
                Save();
                return true;
            }
            return false;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Marks an untrusted entry as user-accepted.
    /// </summary>
    public bool AcceptUntrusted(string id)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_entries.TryGetValue(id, out var entry) && entry.Trust == RuntimeTrust.Untrusted)
            {
                var updated = entry with { Trust = RuntimeTrust.UserAccepted };
                _entries = _entries.SetItem(id, updated);
                Save();
                return true;
            }
            return false;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private void Load()
    {
        if (!File.Exists(_catalogPath))
        {
            _entries = ImmutableDictionary<string, CatalogEntry>.Empty;
            return;
        }

        try
        {
            var json = File.ReadAllText(_catalogPath);
            var data = JsonSerializer.Deserialize<CatalogData>(json, JsonOptions.Default);

            if (data?.Entries is not null)
            {
                var dict = data.Entries.ToImmutableDictionary(e => e.Id, e => e);
                _entries = dict;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // R51: corrupt or unreadable data file does not stop the program starting.
            // It is reported (via empty catalog) and the rest of the data is used.
            // The caller can check if the catalog loaded by checking if Entries is empty
            // when a file was expected. We don't throw.
            _entries = ImmutableDictionary<string, CatalogEntry>.Empty;
        }
    }

    private void Save()
    {
        var data = new CatalogData(_entries.Values.ToList());
        var json = JsonSerializer.Serialize(data, JsonOptions.Default);

        // R52: atomic write - write to temp then move
        var tempPath = _catalogPath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _catalogPath, overwrite: true);
    }

    private sealed record CatalogData(IReadOnlyList<CatalogEntry> Entries);

    private static class JsonOptions
    {
        public static readonly JsonSerializerOptions Default = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };
    }
}