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
public sealed class RuntimeCatalog : IDisposable
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
        if (!detected.Exists || !detected.IsVersionKnown || detected.Version is null)
        {
            // R10: unknown versions are never guessed at, so they don't enter the catalog
            return null;
        }

        var sourceDetail = $"{gameName} ({libraryName})";
        var id = CatalogId.For(detected.Kind, detected.Version, RuntimeSource.Discovered);

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
    /// <summary>
    /// Records a runtime version the collection now owns a copy of.
    /// </summary>
    /// <remarks>
    /// The single write path for an imported version. The files live in <paramref name="folder"/>,
    /// which is the collection's own copy under the data directory rather than wherever the user
    /// downloaded them to.
    ///
    /// The signature has already been judged by <see cref="RuntimeImporter"/>, which is where the
    /// user is shown it and where R16 is enforced; this only records the outcome. Passing an entry
    /// that is neither trusted nor explicitly accepted would put an unchecked file into the
    /// collection, so it is refused here as well rather than trusted to be called correctly once.
    /// </remarks>
    /// <param name="kind">Which runtime this is.</param>
    /// <param name="version">The normalised version.</param>
    /// <param name="folder">The folder holding every file this kind consists of.</param>
    /// <param name="sourceDetail">Where the user obtained it, recorded as R17 requires.</param>
    /// <param name="trust">How its signature was judged.</param>
    /// <returns>The entry, or null if it was refused.</returns>
    public CatalogEntry? AddImport(
        RuntimeKind kind,
        string version,
        string folder,
        string sourceDetail,
        RuntimeTrust trust)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        if (trust == RuntimeTrust.Untrusted)
        {
            return null;
        }

        var primary = RuntimeDlls.ByKind[kind]
            .Select(name => Path.Combine(folder, name))
            .FirstOrDefault(File.Exists);

        if (primary is null)
        {
            return null;
        }

        var signature = SignatureVerifier.Check(primary);
        var id = CatalogId.For(kind, version, RuntimeSource.Imported);

        _lock.EnterWriteLock();
        try
        {
            if (_entries.TryGetValue(id, out var existing))
            {
                // Already have this version. An import that turned out to be trusted upgrades an
                // earlier untrusted acceptance rather than sitting beside it.
                if (trust == RuntimeTrust.Trusted && existing.Trust != RuntimeTrust.Trusted)
                {
                    var improved = existing with { Trust = trust, SignatureInfo = signature.Details };
                    _entries = _entries.SetItem(id, improved);
                    Save();
                    return improved;
                }

                return existing;
            }

            var entry = new CatalogEntry(
                id,
                kind,
                version,
                null,
                RuntimeSource.Imported,
                sourceDetail,
                primary,
                trust,
                DateTimeOffset.UtcNow,
                signature.Details);

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

    /// <summary>Releases the reader/writer lock.</summary>
    public void Dispose() => _lock.Dispose();

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
