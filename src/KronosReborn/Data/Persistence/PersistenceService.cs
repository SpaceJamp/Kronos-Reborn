using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using KronosReborn.Data.Libraries;
using KronosReborn.Data.Runtimes;

namespace KronosReborn.Data.Persistence;

/// <summary>
/// Result of loading persistent data, including any problems encountered.
/// </summary>
/// <remarks>
/// R51: a corrupt or unreadable data file does not stop the program starting. It is reported,
/// and the rest of the data is used.
/// </remarks>
public sealed record PersistenceLoadResult(
    Settings Settings,
    IReadOnlyList<GameHistory> Histories,
    IReadOnlyList<ScanProblem> Problems)
{
    public static PersistenceLoadResult Empty { get; } = new(Settings.Default, [], []);
}

/// <summary>
/// Coordinates all persistent data: settings, history, catalog.
/// </summary>
/// <remarks>
/// R50: data stored as plain files (JSON).
/// R51: corrupt/unreadable files are reported, not fatal.
/// R52: writes are atomic (temp file + move).
/// R53/R54: history per game with configurable limit, oldest dropped first.
/// R55: no migration from previous versions.
/// </remarks>
public sealed class PersistenceService : IDisposable
{
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly DataPaths _paths;
    private Settings _settings = Settings.Default;
    private ImmutableDictionary<string, GameHistory> _histories = ImmutableDictionary<string, GameHistory>.Empty;

    /// <summary>Creates a persistence service over the given data paths.</summary>
    /// <param name="paths">
    /// Where the data lives. Injected rather than resolved statically so a test can point it at a
    /// temporary directory; see <see cref="DataPaths"/>.
    /// </param>
    public PersistenceService(DataPaths? paths = null)
    {
        _paths = paths ?? DataPaths.ForCurrentUser();
        _paths.EnsureCreated();
    }

    /// <summary>Where this service reads and writes.</summary>
    public DataPaths Paths => _paths;

    /// <summary>
    /// Releases the reader/writer lock.
    /// </summary>
    /// <remarks>
    /// The lock is held only for the duration of each property read or write, never across an
    /// await, so a service that is simply left alone is also fine and this exists for the sake of
    /// being a good citizen with the underlying handle.
    /// </remarks>
    public void Dispose() => _lock.Dispose();

    /// <summary>Current settings.</summary>
    public Settings Settings
    {
        get
        {
            _lock.EnterReadLock();
            try { return _settings; }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>All game histories.</summary>
    public IReadOnlyDictionary<string, GameHistory> Histories
    {
        get
        {
            _lock.EnterReadLock();
            try { return _histories; }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// Loads all persistent data from disk.
    /// </summary>
    public PersistenceLoadResult Load()
    {
        var problems = new List<ScanProblem>();

        // Load settings
        var settings = LoadSettings(out var settingsProblems);
        problems.AddRange(settingsProblems);

        // Load histories
        var histories = LoadHistories(out var historyProblems);
        problems.AddRange(historyProblems);

        _lock.EnterWriteLock();
        try
        {
            _settings = settings;
            _histories = histories.ToImmutableDictionary(h => h.Key, h => h.Value);
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        return new PersistenceLoadResult(settings, histories.Values.ToList(), problems);
    }

    /// <summary>Updates settings and saves atomically.</summary>
    public void SaveSettings(Settings settings)
    {
        _lock.EnterWriteLock();
        try
        {
            _settings = settings;
            AtomicWrite(_paths.SettingsPath, JsonSerializer.Serialize(SettingsData.FromSettings(settings), JsonOptions.Default));
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>Updates a single history and saves atomically.</summary>
    public void SaveHistory(GameHistory history)
    {
        _lock.EnterWriteLock();
        try
        {
            _histories = _histories.SetItem(history.GameId, history);
            var path = HistoryPath(history.GameId);
            AtomicWrite(path, JsonSerializer.Serialize(GameHistoryData.FromHistory(history), JsonOptions.Default));
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>Adds a history entry for a game, enforcing the max limit.</summary>
    public void AddHistoryEntry(string gameId, HistoryEntry entry)
    {
        _lock.EnterWriteLock();
        try
        {
            var maxEntries = _settings.MaxHistoryPerGame;
            var history = _histories.TryGetValue(gameId, out var existing)
                ? existing.Add(entry, maxEntries)
                : new GameHistory(gameId, ImmutableList.Create(entry));

            _histories = _histories.SetItem(gameId, history);
            var path = HistoryPath(gameId);
            AtomicWrite(path, JsonSerializer.Serialize(GameHistoryData.FromHistory(history), JsonOptions.Default));
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>Gets history for a game, or empty if none.</summary>
    public GameHistory GetHistory(string gameId)
    {
        _lock.EnterReadLock();
        try
        {
            return _histories.TryGetValue(gameId, out var h) ? h : new GameHistory(gameId, ImmutableList<HistoryEntry>.Empty);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    private Settings LoadSettings(out List<ScanProblem> problems)
    {
        problems = [];
        var path = _paths.SettingsPath;

        if (!File.Exists(path))
        {
            return Settings.Default;
        }

        try
        {
            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<SettingsData>(json, JsonOptions.Default);
            return data?.ToSettings() ?? Settings.Default;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            problems.Add(new ScanProblem(
                "Settings",
                path,
                "Settings file could not be read, using defaults.",
                ex.Message,
                "Delete or fix the file at " + path));
            return Settings.Default;
        }
    }

    private Dictionary<string, GameHistory> LoadHistories(out List<ScanProblem> problems)
    {
        problems = [];
        var histories = new Dictionary<string, GameHistory>();
        var dir = _paths.HistoryDirectory;

        if (!Directory.Exists(dir))
        {
            return histories;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var data = JsonSerializer.Deserialize<GameHistoryData>(json, JsonOptions.Default);
                if (data is not null)
                {
                    histories[data.GameId] = data.ToHistory();
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                var gameId = Path.GetFileNameWithoutExtension(file);
                problems.Add(new ScanProblem(
                    "History",
                    file,
                    $"History for game {gameId} could not be read.",
                    ex.Message,
                    "The file will be ignored. Delete it to clear the error."));
            }
        }

        return histories;
    }

    private string HistoryPath(string gameId)
    {
        return Path.Combine(_paths.HistoryDirectory, $"{DataPaths.SafeName(gameId)}.json");
    }

    /// <summary>
    /// Writes a file atomically: write to temp, then move over original.
    /// </summary>
    /// <remarks>
    /// R52: an interrupted write leaves the previous version intact rather than a truncated one.
    /// </remarks>
    private static void AtomicWrite(string path, string content)
    {
        var dir = Path.GetDirectoryName(path)!;

        // The data paths are pure values and do not create anything as a side effect of being read,
        // so the directory is made here rather than assumed. A first write would otherwise fail with
        // DirectoryNotFoundException.
        Directory.CreateDirectory(dir);

        var tempPath = Path.Combine(dir, $".{Path.GetFileName(path)}.tmp.{Guid.NewGuid():N}");

        File.WriteAllText(tempPath, content);
        File.Move(tempPath, path, overwrite: true);
    }

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
