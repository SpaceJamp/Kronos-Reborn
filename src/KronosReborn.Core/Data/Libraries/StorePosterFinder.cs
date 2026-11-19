using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using KronosReborn.Data.Logging;

namespace KronosReborn.Data.Libraries;

/// <summary>
/// Finds a cover for a game Steam's own cache has none for, from Steam's public store, then from the
/// Microsoft Store's.
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
/// Games Steam does not sell, such as Xbox games and older console ports, are looked up the same way in
/// the Microsoft Store's catalogue, whose posters are the same 2:3 shape as the cards.
/// </para>
/// <para>
/// Each cover is downloaded once and kept. A game with no match is remembered for a week, so a refresh
/// does not ask again, but a game that reaches a store later still gets its cover. A failure to reach
/// a store is not remembered, so the next refresh retries.
/// </para>
/// </remarks>
public sealed partial class StorePosterFinder(HttpClient http, string cacheFolder)
{
    /// <summary>How long one request may take before the game is left without a cover this time.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>How long a game no store had is left before the stores are asked again.</summary>
    private static readonly TimeSpan MissLasts = TimeSpan.FromDays(7);

    /// <summary>Microsoft Store image kinds that are covers, best first: the 2:3 poster, then square box art.</summary>
    private static readonly string[] PosterPurposes = ["Poster", "BoxArt"];

    /// <summary>The image types a cover can be chosen from.</summary>
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp"];

    /// <summary>The cover for a game, downloading it if needed, or null when there is none.</summary>
    /// <param name="gameId">The game's library id, which names the cached file.</param>
    /// <param name="name">The game's name or folder name, searched for in the stores.</param>
    /// <param name="steamAppId">The Steam app id when it is already known, which skips the search.</param>
    /// <remarks>Never throws for a store or file problem: it runs inside the library scan.</remarks>
    public string? Find(string gameId, string name, string? steamAppId, CancellationToken cancellation)
    {
        var file = Path.Combine(cacheFolder, KronosReborn.Data.Persistence.DataPaths.SafeName(gameId));
        var cover = file + ".jpg";
        var miss = file + ".miss";

        if (File.Exists(cover))
        {
            return cover;
        }

        try
        {
            // Older versions remembered a miss forever, before the Microsoft Store was searched; those
            // are dropped so each such game is asked about once more.
            if (File.Exists(file + ".none"))
            {
                File.Delete(file + ".none");
            }

            if (File.Exists(miss) && DateTime.UtcNow - File.GetLastWriteTimeUtc(miss) < MissLasts)
            {
                return null;
            }

            // Each store is skipped only once it has failed this refresh, so one being blocked never
            // stops the other: where a store is blocked rather than offline, each attempt would sit out
            // its whole timeout.
            var unanswered = false;
            string? appId = null;
            var image = Ask(ref _steamDown, ref unanswered, name, () =>
                (appId = steamAppId ?? Search(name, cancellation)) is { } id ? Download(id, cancellation) : null, cancellation);
            image ??= Ask(ref _microsoftDown, ref unanswered, name, () => MicrosoftStorePoster(name, cancellation), cancellation);

            // Last, Steam's landscape header at the address its store names: newer games' images are
            // not at the fixed address, and a portrait poster from Microsoft is the better fit first.
            if (image is null && appId is not null)
            {
                image = Ask(ref _steamDetailsDown, ref unanswered, name, () => NamedHeader(appId, cancellation), cancellation);
            }

            if (image is null)
            {
                // Remembered only when every store really answered; a store that could not be asked
                // says nothing about whether it has the game.
                if (!unanswered)
                {
                    Directory.CreateDirectory(cacheFolder);
                    File.WriteAllText(miss, name);
                }

                return null;
            }

            Directory.CreateDirectory(cacheFolder);
            File.WriteAllBytes(cover, image);
            return cover;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Instance.Warning($"No cover for {name} this time: {ex.Message}", category: "Posters");
            return null;
        }
    }

    /// <summary>Asks one store, unless it has already failed this refresh, and notes when it could not answer.</summary>
    private static byte[]? Ask(ref bool down, ref bool unanswered, string name, Func<byte[]?> ask, CancellationToken cancellation)
    {
        if (down)
        {
            unanswered = true;
            return null;
        }

        try
        {
            return ask();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            cancellation.ThrowIfCancellationRequested();
            down |= ex is HttpRequestException or TaskCanceledException;
            unanswered = true;
            Log.Instance.Warning($"No cover for {name} from one store this time: {ex.Message}", category: "Posters");
            return null;
        }
    }

    /// <summary>The cover the user chose for a game, which wins over any found one, or null.</summary>
    /// <remarks>Never throws: it runs for every game in the library scan.</remarks>
    public static string? Chosen(string cacheFolder, string gameId)
    {
        try
        {
            return Directory.Exists(cacheFolder)
                ? Directory.GetFiles(cacheFolder, ChosenPrefix(gameId) + "*").OrderDescending(StringComparer.Ordinal).FirstOrDefault()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Keeps a copy of an image the user chose as a game's cover, replacing any earlier choice.</summary>
    /// <returns>The kept copy, under a new name each time so no cached image of the old one is shown.</returns>
    /// <exception cref="ArgumentException">The file is not an image.</exception>
    public static string Choose(string cacheFolder, string gameId, string imagePath)
    {
        var extension = Path.GetExtension(imagePath).ToLowerInvariant();
        if (!ImageExtensions.Contains(extension))
        {
            throw new ArgumentException($"{Path.GetFileName(imagePath)} is not an image.", nameof(imagePath));
        }

        Directory.CreateDirectory(cacheFolder);
        var earlier = Directory.GetFiles(cacheFolder, ChosenPrefix(gameId) + "*");
        var kept = Path.Combine(cacheFolder, $"{ChosenPrefix(gameId)}{DateTime.UtcNow.Ticks:D19}{extension}");
        File.Copy(imagePath, kept);

        // The new cover is already kept and wins by name, so an old one that cannot go yet is harmless.
        foreach (var old in earlier)
        {
            try
            {
                File.Delete(old);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Instance.Warning($"An earlier cover could not be removed: {ex.Message}", category: "Posters");
            }
        }

        return kept;
    }

    /// <summary>Forgets the cover the user chose for a game, so the automatic one is used again.</summary>
    public static void Unchoose(string cacheFolder, string gameId)
    {
        if (!Directory.Exists(cacheFolder))
        {
            return;
        }

        foreach (var chosen in Directory.GetFiles(cacheFolder, ChosenPrefix(gameId) + "*"))
        {
            File.Delete(chosen);
        }
    }

    private static string ChosenPrefix(string gameId) =>
        KronosReborn.Data.Persistence.DataPaths.SafeName(gameId) + ".chosen-";

    /// <summary>Set once that store could not be reached; a finder lives for one refresh.</summary>
    private bool _steamDown;

    private bool _microsoftDown;

    /// <summary>Steam's details call on its own: it is rate limited more tightly than search and images.</summary>
    private bool _steamDetailsDown;

    /// <summary>The largest cover kept; a store's posters are a few megabytes at most.</summary>
    private const long MaxImageBytes = 20 * 1024 * 1024;

    /// <summary>The words of a name worth searching for.</summary>
    /// <remarks>
    /// Bracketed notes ("[FitGirl Repack]"), version numbers ("v1.2.3") and an initialism's
    /// possessive ("CB's") are dropped, and punctuation becomes spaces. Other possessives are part of
    /// the name ("Assassin's Creed"), and both apostrophes count as the same one.
    /// </remarks>
    internal static string[] SearchWords(string name) =>
    [
        .. Tokens(Version().Replace(Brackets().Replace(name, " "), " "))
            .Where(w => !IsInitialismPossessive(w)),
    ];

    /// <summary>The words of a name, with the curly apostrophe written as the straight one.</summary>
    private static IEnumerable<string> Tokens(string text) =>
        Words().Matches(text).Select(m => m.Value.Replace('’', '\''));

    /// <summary>"CB's": a short all-capitals word with a possessive, a repacker's or author's initials.</summary>
    /// <remarks>Only a possessive: "DON'T" is a word of the name.</remarks>
    private static bool IsInitialismPossessive(string word) =>
        word.EndsWith("'s", StringComparison.OrdinalIgnoreCase)
        && word.Length - 2 is > 0 and <= 4
        && word[..^2].All(char.IsUpper);

    /// <summary>A word as compared: without apostrophes, so a "Baldurs Gate" folder matches "Baldur's Gate".</summary>
    private static string Key(string word) => word.Replace("'", "", StringComparison.Ordinal);

    /// <summary>A number or Roman numeral, as a sequel adds to a name.</summary>
    private static bool IsSequelNumber(string word) =>
        word.All(char.IsAsciiDigit) || RomanNumeral().IsMatch(word);

    /// <summary>Whether a store result can be the game: its name is the searched words, or holds them all.</summary>
    /// <remarks>
    /// A single word must be the whole name. Containing it is far too loose: "Control" matched
    /// "CONTROL Resonant" and "Tools" matched "Tools Up!", which is worse than no cover.
    /// </remarks>
    /// <remarks>
    /// A title holding all the words plus a number the name lacks is a sequel, not the game: "Baldur's
    /// Gate" must not take "Baldur's Gate 3"'s cover.
    /// </remarks>
    internal static bool Matches(IReadOnlyCollection<string> words, string candidate)
    {
        if (words.Count == 0)
        {
            return false;
        }

        if (IsExactly(words, candidate))
        {
            return true;
        }

        var wanted = words.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var title = Tokens(candidate).Select(Key).ToList();
        return words.Count > 1
               && wanted.All(w => title.Contains(w, StringComparer.OrdinalIgnoreCase))
               && !title.Any(t => !wanted.Contains(t) && IsSequelNumber(t));
    }

    /// <summary>Whether a name is exactly the searched words, ignoring case and punctuation.</summary>
    private static bool IsExactly(IReadOnlyCollection<string> words, string candidate) =>
        Tokens(candidate).Select(Key).SequenceEqual(words.Select(Key), StringComparer.OrdinalIgnoreCase);

    private string? Search(string name, CancellationToken cancellation)
    {
        var words = SearchWords(name);
        if (words.Length == 0)
        {
            return null;
        }

        var url = $"https://store.steampowered.com/api/storesearch/?l=english&cc=US&term={Uri.EscapeDataString(string.Join(' ', words))}";
        using var json = JsonDocument.Parse(Get(url, cancellation) ?? "{}"u8.ToArray());

        // The shortest matching name is the game itself rather than its demo, soundtrack or DLC; an
        // exact name, when there is one, is always the shortest.
        return Array(json.RootElement, "items")
            .Select(i => (Id: i.ValueKind == JsonValueKind.Object && i.TryGetProperty("id", out var id)
                                  && id.ValueKind is JsonValueKind.Number or JsonValueKind.String ? id.ToString() : null,
                          Name: Text(i, "name") ?? ""))
            .Where(i => i.Id is not null && Matches(words, i.Name))
            .OrderBy(i => i.Name.Length)
            .Select(i => i.Id)
            .FirstOrDefault();
    }

    /// <summary>The game's poster from the Microsoft Store's public catalogue, or null.</summary>
    private byte[]? MicrosoftStorePoster(string name, CancellationToken cancellation)
    {
        var words = SearchWords(name);
        if (words.Length == 0)
        {
            return null;
        }

        var search = $"https://displaycatalog.mp.microsoft.com/v7.0/productFamilies/autosuggest?market=US&languages=en-US&query={Uri.EscapeDataString(string.Join(' ', words))}&productFamilyNames=Games,Apps";
        using var found = JsonDocument.Parse(Get(search, cancellation) ?? "{}"u8.ToArray());

        // The same strict match as Steam's, and the shortest name, which is the game rather than its add-ons.
        var productId = Array(found.RootElement, "Results")
            .SelectMany(r => Array(r, "Products"))
            .Select(p => (Id: Text(p, "ProductId"), Title: Text(p, "Title") ?? ""))
            .Where(p => p.Id is not null && Matches(words, p.Title))
            .OrderBy(p => p.Title.Length)
            .Select(p => p.Id)
            .FirstOrDefault();

        if (productId is null)
        {
            return null;
        }

        var details = $"https://displaycatalog.mp.microsoft.com/v7.0/products?bigIds={Uri.EscapeDataString(productId)}&market=US&languages=en-US";
        using var product = JsonDocument.Parse(Get(details, cancellation) ?? "{}"u8.ToArray());

        // The 2:3 poster first, then the square box art; never screenshots or banners.
        var images = Array(product.RootElement, "Products")
            .SelectMany(p => Array(p, "LocalizedProperties"))
            .SelectMany(l => Array(l, "Images"))
            .Select(i => (Purpose: Text(i, "ImagePurpose"), Uri: Text(i, "Uri")))
            .ToArray();

        var image = PosterPurposes
            .Select(purpose => images.FirstOrDefault(i => i.Purpose == purpose).Uri)
            .FirstOrDefault(uri => uri is not null);

        // Downloaded only from Microsoft's own image server, over https.
        return image is not null
               && Uri.TryCreate(image.StartsWith("//", StringComparison.Ordinal) ? "https:" + image : image, UriKind.Absolute, out var uri)
               && uri.Scheme == Uri.UriSchemeHttps
               && (uri.Host == "store-images.s-microsoft.com" || uri.Host == "store-images.microsoft.com")
            ? Get(uri.AbsoluteUri, cancellation, image: true)
            : null;
    }

    private static IEnumerable<JsonElement> Array(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>The header image Steam's store names for a game, from Steam's own image servers only.</summary>
    private byte[]? NamedHeader(string appId, CancellationToken cancellation)
    {
        var details = $"https://store.steampowered.com/api/appdetails?appids={Uri.EscapeDataString(appId)}&filters=basic";
        using var json = JsonDocument.Parse(Get(details, cancellation) ?? "{}"u8.ToArray());

        var header = json.RootElement.ValueKind == JsonValueKind.Object
                     && json.RootElement.TryGetProperty(appId, out var app)
                     && app.ValueKind == JsonValueKind.Object
                     && app.TryGetProperty("data", out var data)
            ? Text(data, "header_image")
            : null;

        // No details (rate limiting, or a game not sold in the region) says nothing about whether
        // the game has art, so it is not remembered as a miss.
        if (header is null)
        {
            throw new InvalidOperationException($"Steam gave no details for app {appId}.");
        }

        return header is not null
               && Uri.TryCreate(header, UriKind.Absolute, out var uri)
               && uri.Scheme == Uri.UriSchemeHttps
               && (uri.Host == "steamstatic.com" || uri.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase))
            ? Get(uri.AbsoluteUri, cancellation, image: true)
            : null;
    }

    private byte[]? Download(string appId, CancellationToken cancellation) =>
        Get($"https://shared.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900.jpg", cancellation, image: true)
        ?? Get($"https://shared.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg", cancellation, image: true);

    /// <summary>The body of a successful response, or null for a missing one.</summary>
    /// <param name="image">
    /// Whether it must be an image: a web page in its place, such as a hotel or airport sign-in page,
    /// or anything too large, is treated as no image rather than kept as a cover.
    /// </param>
    private byte[]? Get(string url, CancellationToken cancellation, bool image = false)
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
        if (image && (response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true
                      || response.Content.Headers.ContentLength > MaxImageBytes))
        {
            return null;
        }

        var body = response.Content.ReadAsByteArrayAsync(timeout.Token).GetAwaiter().GetResult();
        return image && body.Length > MaxImageBytes ? null : body;
    }

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"[\p{L}\p{N}'’]+")]
    private static partial Regex Words();

    [GeneratedRegex("^(II|III|IV|V|VI|VII|VIII|IX|X|XI|XII)$", RegexOptions.IgnoreCase)]
    private static partial Regex RomanNumeral();

    [GeneratedRegex(@"\bv?\d+(\.\d+)+\b", RegexOptions.IgnoreCase)]
    private static partial Regex Version();
}
