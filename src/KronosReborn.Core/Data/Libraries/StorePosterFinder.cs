using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using KronosReborn.Data.Logging;

namespace KronosReborn.Data.Libraries;

/// <summary>
/// Finds a cover for a game Steam's own cache has none for, from Steam's public store.
/// </summary>
/// <remarks>
/// <para>
/// Repacks, hand-added games and games from other stores carry no artwork. Steam's store search finds
/// the game by name, and its cover is a public image. The search is picky — "CB's Hellraiser -
/// Revival" finds nothing while "Hellraiser Revival" finds it — so the name is cleaned first, and a
/// result is only taken when every searched word is in its name, so a vague folder name gets no
/// cover rather than someone else's.
/// </para>
/// <para>
/// Each cover is downloaded once and kept. A game with no match is remembered too, so a refresh does
/// not ask Steam again. A failure to reach Steam is not remembered, so the next refresh retries.
/// </para>
/// </remarks>
public sealed partial class StorePosterFinder(HttpClient http, string cacheFolder)
{
    /// <summary>How long one request may take before the game is left without a cover this time.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>The cover for a game, downloading it if needed, or null when there is none.</summary>
    /// <param name="gameId">The game's library id, which names the cached file.</param>
    /// <param name="name">The game's name or folder name, searched for on Steam.</param>
    /// <param name="steamAppId">The Steam app id when it is already known, which skips the search.</param>
    public string? Find(string gameId, string name, string? steamAppId, CancellationToken cancellation)
    {
        var file = Path.Combine(cacheFolder, KronosReborn.Data.Persistence.DataPaths.SafeName(gameId));
        var cover = file + ".jpg";
        var none = file + ".none";

        if (File.Exists(cover))
        {
            return cover;
        }

        if (File.Exists(none))
        {
            return null;
        }

        try
        {
            var appId = steamAppId ?? Search(name, cancellation);
            Directory.CreateDirectory(cacheFolder);

            if (appId is null || Download(appId, cancellation) is not { } image)
            {
                File.WriteAllText(none, name);
                return null;
            }

            File.WriteAllBytes(cover, image);
            return cover;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                      or IOException or UnauthorizedAccessException)
        {
            cancellation.ThrowIfCancellationRequested();
            Log.Instance.Warning($"No cover for {name} this time: {ex.Message}", category: "Posters");
            return null;
        }
    }

    /// <summary>The words of a name worth searching for.</summary>
    /// <remarks>
    /// Bracketed notes ("[FitGirl Repack]"), version numbers ("v1.2.3") and possessive abbreviations
    /// ("CB's") are dropped, and punctuation becomes spaces.
    /// </remarks>
    internal static string[] SearchWords(string name) =>
    [
        .. Words().Matches(Version().Replace(Brackets().Replace(name, " "), " "))
            .Select(m => m.Value)
            .Where(w => !w.Contains('\'') && !w.Contains('’')),
    ];

    /// <summary>Whether a store result's name contains every searched word.</summary>
    internal static bool Matches(IReadOnlyCollection<string> words, string candidate)
    {
        var have = Words().Matches(candidate).Select(m => m.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return words.Count > 0 && words.All(have.Contains);
    }

    private string? Search(string name, CancellationToken cancellation)
    {
        var words = SearchWords(name);
        if (words.Length == 0)
        {
            return null;
        }

        var url = $"https://store.steampowered.com/api/storesearch/?l=english&cc=US&term={Uri.EscapeDataString(string.Join(' ', words))}";
        using var json = JsonDocument.Parse(Get(url, cancellation) ?? "{}"u8.ToArray());

        if (!json.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        // The shortest matching name is the game itself rather than its demo, soundtrack or DLC.
        return items.EnumerateArray()
            .Select(i => (Id: i.TryGetProperty("id", out var id) ? id.ToString() : null,
                          Name: i.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""))
            .Where(i => i.Id is not null && Matches(words, i.Name))
            .OrderBy(i => i.Name.Length)
            .Select(i => i.Id)
            .FirstOrDefault();
    }

    private byte[]? Download(string appId, CancellationToken cancellation) =>
        Get($"https://shared.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900.jpg", cancellation)
        ?? Get($"https://shared.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg", cancellation);

    /// <summary>The body of a successful response, or null for a missing one.</summary>
    private byte[]? Get(string url, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(Timeout);

        // Called from the scan, which already runs off the UI thread, so blocking here is fine.
        using var response = http.GetAsync(new Uri(url), timeout.Token).GetAwaiter().GetResult();
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return response.Content.ReadAsByteArrayAsync(timeout.Token).GetAwaiter().GetResult();
    }

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"[\p{L}\p{N}'’]+")]
    private static partial Regex Words();

    [GeneratedRegex(@"\bv?\d+(\.\d+)+\b", RegexOptions.IgnoreCase)]
    private static partial Regex Version();
}
