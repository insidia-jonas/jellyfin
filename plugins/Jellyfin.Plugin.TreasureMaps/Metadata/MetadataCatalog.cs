using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TreasureMaps.Channels;
using Jellyfin.Plugin.TreasureMaps.Search;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TreasureMaps.Metadata;

/// <summary>
/// Poster, plot, year, rating and cast for a title — used to bring channel BoxSets up to
/// IMDb-style detail pages when the indexer only has a scene name.
/// </summary>
public sealed class CatalogHit
{
    /// <summary>Gets or sets the display title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the plot.</summary>
    public string? Plot { get; set; }

    /// <summary>Gets or sets the poster URL.</summary>
    public string? Cover { get; set; }

    /// <summary>Gets or sets the year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the community rating (0–10).</summary>
    public double? Rating { get; set; }

    /// <summary>Gets or sets the director.</summary>
    public string? Director { get; set; }

    /// <summary>Gets the genres.</summary>
    public List<string> Genres { get; } = new();

    /// <summary>Gets the actors.</summary>
    public List<string> Actors { get; } = new();
}

/// <summary>
/// Fills missing title-card metadata from OMDb (optional key) and the public iTunes Search API
/// (no key). Results are cached in memory so browsing a folder does not hammer the providers.
/// </summary>
public sealed class MetadataCatalog
{
    private static readonly ConcurrentDictionary<string, CatalogHit?> Cache = new(StringComparer.OrdinalIgnoreCase);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MetadataCatalog> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataCatalog"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public MetadataCatalog(IHttpClientFactory httpClientFactory, ILogger<MetadataCatalog> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Enriches groups that are missing a cover, plot, rating or cast.
    /// </summary>
    /// <param name="groups">The grouped titles.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the lookups finish.</returns>
    public async Task FillAsync(IReadOnlyList<ReleaseGroup> groups, CancellationToken cancellationToken)
    {
        var missing = groups.Where(NeedsFill).Take(28).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        await Task.WhenAll(missing.Select(g => FillOneAsync(g, cancellationToken))).ConfigureAwait(false);
    }

    /// <summary>
    /// Picks the iTunes/OMDb candidate that best matches the indexer title and year.
    /// </summary>
    /// <param name="title">The indexer title.</param>
    /// <param name="year">The indexer year, may be null.</param>
    /// <param name="candidates">The catalog hits.</param>
    /// <returns>The best hit, or null.</returns>
    public static CatalogHit? PickBest(string title, int? year, IEnumerable<CatalogHit> candidates)
    {
        CatalogHit? best = null;
        var bestScore = 0f;
        foreach (var hit in candidates)
        {
            var name = hit.Title ?? string.Empty;
            var score = TreasureMapsSearch.ScoreTitle(name, title, hit.Year);
            if (year is int y && hit.Year == y)
            {
                score += 10f;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = hit;
            }
        }

        return bestScore >= TreasureMapsSearch.ContainsMatchScore ? best : null;
    }

    /// <summary>
    /// Turns an iTunes <c>artworkUrl100</c> into a 600px poster.
    /// </summary>
    /// <param name="artworkUrl">The artwork URL.</param>
    /// <returns>The larger URL, or the original.</returns>
    public static string? UpgradeArtwork(string? artworkUrl)
    {
        if (string.IsNullOrWhiteSpace(artworkUrl))
        {
            return artworkUrl;
        }

        return artworkUrl
            .Replace("100x100", "600x600", StringComparison.Ordinal)
            .Replace("200x200", "600x600", StringComparison.Ordinal);
    }

    private static bool NeedsFill(ReleaseGroup group)
        => string.IsNullOrWhiteSpace(group.Cover)
           || string.IsNullOrWhiteSpace(group.Plot)
           || group.Actors.Count == 0
           || !group.Rating.HasValue;

    private async Task FillOneAsync(ReleaseGroup group, CancellationToken cancellationToken)
    {
        try
        {
            ApplyPicbit(group);
            if (!NeedsFill(group))
            {
                return;
            }

            var key = group.Kind + "|" + (group.Imdb ?? string.Empty) + "|" + group.Title + "|" + (group.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            if (!Cache.TryGetValue(key, out var hit))
            {
                hit = await LookupAsync(group, cancellationToken).ConfigureAwait(false);
                Cache[key] = hit;
            }

            if (hit is null)
            {
                return;
            }

            group.Cover ??= hit.Cover;
            group.Plot ??= hit.Plot;
            group.Year ??= hit.Year;
            group.Rating ??= hit.Rating;
            group.Director ??= hit.Director;
            if (group.Genres.Count == 0)
            {
                group.Genres.AddRange(hit.Genres);
            }

            if (group.Actors.Count == 0)
            {
                group.Actors.AddRange(hit.Actors);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Metadata catalog fill failed for {Title}", group.Title);
        }
    }

    private static void ApplyPicbit(ReleaseGroup group)
    {
        if (!string.IsNullOrWhiteSpace(group.Cover) || string.IsNullOrWhiteSpace(group.Imdb))
        {
            return;
        }

        var raw = group.Imdb;
        var numeric = raw.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? raw[2..] : raw.Trim();
        if (numeric.Length == 0 || !numeric.All(char.IsDigit))
        {
            return;
        }

        group.Cover = "https://picbit.io/movies_" + numeric + "-cover.webp";
    }

    private async Task<CatalogHit?> LookupAsync(ReleaseGroup group, CancellationToken cancellationToken)
    {
        var omdb = await LookupOmdbAsync(group, cancellationToken).ConfigureAwait(false);
        if (omdb is not null && !string.IsNullOrWhiteSpace(omdb.Cover) && !string.IsNullOrWhiteSpace(omdb.Plot))
        {
            return omdb;
        }

        var itunes = await LookupItunesAsync(group, cancellationToken).ConfigureAwait(false);
        return Merge(omdb, itunes);
    }

    private static CatalogHit? Merge(CatalogHit? first, CatalogHit? second)
    {
        if (first is null)
        {
            return second;
        }

        if (second is null)
        {
            return first;
        }

        first.Cover ??= second.Cover;
        first.Plot ??= second.Plot;
        first.Year ??= second.Year;
        first.Rating ??= second.Rating;
        first.Director ??= second.Director;
        first.Title ??= second.Title;
        if (first.Genres.Count == 0)
        {
            first.Genres.AddRange(second.Genres);
        }

        if (first.Actors.Count == 0)
        {
            first.Actors.AddRange(second.Actors);
        }

        return first;
    }

    private async Task<CatalogHit?> LookupOmdbAsync(ReleaseGroup group, CancellationToken cancellationToken)
    {
        var key = Plugin.Instance?.Configuration.OmdbApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var query = !string.IsNullOrWhiteSpace(group.Imdb)
            ? "i=" + Uri.EscapeDataString(ReleaseMapper.NormalizeImdbId(group.Imdb))
            : "t=" + Uri.EscapeDataString(group.Title) + (group.Year is int y ? "&y=" + y.ToString(CultureInfo.InvariantCulture) : string.Empty);
        var url = "https://www.omdbapi.com/?apikey=" + Uri.EscapeDataString(key) + "&plot=full&" + query;
        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;
        if (!root.TryGetProperty("Response", out var ok) || !string.Equals(ok.GetString(), "True", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hit = new CatalogHit
        {
            Title = Get(root, "Title"),
            Plot = Get(root, "Plot"),
            Cover = N(Get(root, "Poster")),
            Director = N(Get(root, "Director")),
            Year = ParseYear(Get(root, "Year")),
            Rating = ParseRating(Get(root, "imdbRating"))
        };
        SplitInto(hit.Genres, Get(root, "Genre"));
        SplitInto(hit.Actors, Get(root, "Actors"));
        return hit;
    }

    private async Task<CatalogHit?> LookupItunesAsync(ReleaseGroup group, CancellationToken cancellationToken)
    {
        var entity = string.Equals(group.Kind, "tv", StringComparison.OrdinalIgnoreCase) ? "tvSeason" : "movie";
        var url = "https://itunes.apple.com/search?entity=" + entity
                  + "&limit=8&term=" + Uri.EscapeDataString(group.Title);
        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var hits = new List<CatalogHit>();
        foreach (var row in results.EnumerateArray())
        {
            var name = Get(row, "trackName") ?? Get(row, "collectionName");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var hit = new CatalogHit
            {
                Title = name,
                Plot = Get(row, "longDescription") ?? Get(row, "shortDescription"),
                Cover = UpgradeArtwork(Get(row, "artworkUrl100")),
                Year = ParseYear(Get(row, "releaseDate"))
            };
            SplitInto(hit.Genres, Get(row, "primaryGenreName"));
            hits.Add(hit);
        }

        return PickBest(group.Title, group.Year, hits);
    }

    private static string? Get(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? N(string? value)
        => string.IsNullOrWhiteSpace(value) || string.Equals(value, "N/A", StringComparison.OrdinalIgnoreCase)
            ? null
            : value;

    private static void SplitInto(List<string> target, string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return;
        }

        foreach (var part in csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!string.Equals(part, "N/A", StringComparison.OrdinalIgnoreCase))
            {
                target.Add(part);
            }
        }
    }

    private static int? ParseYear(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        for (var i = 0; i <= value.Length - 4; i++)
        {
            if (char.IsDigit(value[i]) && int.TryParse(value.AsSpan(i, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
                && year is >= 1900 and <= 2100)
            {
                return year;
            }
        }

        return null;
    }

    private static double? ParseRating(string? value)
        => double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
}
