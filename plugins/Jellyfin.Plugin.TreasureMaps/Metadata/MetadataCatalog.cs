using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TreasureMaps.Channels;
using Jellyfin.Plugin.TreasureMaps.Listing;
using Jellyfin.Plugin.TreasureMaps.Search;
using Microsoft.Extensions.Logging;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Providers;

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

    public string? RatingSource { get; set; }

    public string? Language { get; set; }

    /// <summary>Gets or sets the director.</summary>
    public string? Director { get; set; }

    /// <summary>Gets the genres.</summary>
    public List<string> Genres { get; set; } = new();

    /// <summary>Gets the actors.</summary>
    public List<string> Actors { get; set; } = new();
}

/// <summary>
/// Fills missing title-card metadata from OMDb (optional key) and the public iTunes Search API
/// (no key). Results are cached in memory so browsing a folder does not hammer the providers.
/// </summary>
public sealed class MetadataCatalog
{
    private static readonly TreasureMapsListingCache Cache = new(identity: MetadataSettingsKey);
    private static readonly SemaphoreSlim Slots = new(3, 3);
    private readonly ConcurrentDictionary<string, byte> _warming = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _missing = new();
    private static readonly SemaphoreSlim WarmSlots = new(2, 2);
    private readonly IProviderManager? _providers;
    private readonly string? _cacheDirectory;

    /// <summary>
    /// Drops cached IMDb/iTunes hits (language / image settings changed).
    /// </summary>
    public static void Clear() => Cache.InvalidateAll();

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MetadataCatalog> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataCatalog"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public MetadataCatalog(IHttpClientFactory httpClientFactory, ILogger<MetadataCatalog> logger,
        IProviderManager? providers = null, IApplicationPaths? paths = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _providers = providers;
        _cacheDirectory = paths is null ? null : Path.Combine(paths.CachePath, "evolution-metadata-v1");
    }

    /// <summary>
    /// Enriches groups that are missing a cover, plot, rating or cast.
    /// </summary>
    /// <param name="groups">The grouped titles.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="allowNetwork">Whether optional remote metadata may delay this listing.</param>
    /// <returns>A task that completes when the lookups finish.</returns>
    public Task FillAsync(IReadOnlyList<ReleaseGroup> groups, CancellationToken cancellationToken, bool allowNetwork = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var group in groups)
        {
            ApplyPicbit(group);
            if (Cache.TryGetFresh<CatalogEntry>(CatalogKey(group), out var entry, out _))
            {
                ApplyHit(group, entry?.Hit);
            }
        }

        if (allowNetwork) Warm(groups);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <summary>Prepare a bounded visible page without holding up indexer listings.</summary>
    public void Warm(IEnumerable<ReleaseGroup> groups)
    {
        foreach (var group in groups.Take(24))
        {
            var key = CatalogKey(group);
            if (_warming.Count >= 48 || Cache.TryGetFresh<CatalogEntry>(key, out _, out _) || !_warming.TryAdd(key, 0)) continue;
            // Never mutate a listing after returning it to ChannelManager.
            var identity = new ReleaseGroup { Title = group.Title, Kind = group.Kind, Imdb = group.Imdb, Tmdb = group.Tmdb, Year = group.Year };
            _ = Task.Run(async () =>
            {
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                    // Leave one network slot for a title opened by the viewer.
                    await WarmSlots.WaitAsync(deadline.Token).ConfigureAwait(false);
                    try { await GetAsync(identity, deadline.Token).ConfigureAwait(false); }
                    finally { WarmSlots.Release(); }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { _logger.LogDebug("Optional metadata warmup unavailable ({Type})", ex.GetType().Name); }
                finally { _warming.TryRemove(key, out _); }
            });
        }
    }

    /// <summary>Coalesced detail lookup; a cancelled client does not cancel another viewer's lookup.</summary>
    public async Task<CatalogHit?> GetAsync(ReleaseGroup group, CancellationToken ct)
    {
        var key = CatalogKey(group);
        if (_missing.TryGetValue(key, out var until) && until > DateTimeOffset.UtcNow) return null;
        var entry = await Cache.GetOrFetchAsync<CatalogEntry>(key, TimeSpan.FromHours(24), async (_, token) =>
        {
            await Slots.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var disk = ReadDisk(key);
                if (disk is not null) return ListingFetch<CatalogEntry>.Store(new CatalogEntry(disk));
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(12));
                var hit = await LookupAsync(group, deadline.Token).ConfigureAwait(false);
                if (hit is null)
                {
                    _missing[key] = DateTimeOffset.UtcNow.AddMinutes(2);
                    // Snapshot under ConcurrentDictionary's locks before LINQ reads Count/CopyTo.
                    foreach (var old in _missing.ToArray().OrderByDescending(x => x.Value).Skip(256)) _missing.TryRemove(old.Key, out var ignored);
                    return ListingFetch<CatalogEntry>.DoNotStore(null);
                }
                WriteDisk(key, hit);
                return ListingFetch<CatalogEntry>.Store(new CatalogEntry(hit));
            }
            finally { Slots.Release(); }
        }, ct).ConfigureAwait(false);
        return entry?.Hit;
    }

    private string? DiskPath(string key) => _cacheDirectory is null ? null : Path.Combine(_cacheDirectory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json");

    private CatalogHit? ReadDisk(string key)
    {
        var path = DiskPath(key);
        try { return path is not null && File.Exists(path) && File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddDays(-7)
            ? JsonSerializer.Deserialize<CatalogHit>(File.ReadAllText(path)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private void WriteDisk(string key, CatalogHit hit)
    {
        var path = DiskPath(key);
        if (path is null) return;
        try
        {
            Directory.CreateDirectory(_cacheDirectory!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(hit));
            File.Move(path + ".tmp", path, true);
            foreach (var old in new DirectoryInfo(_cacheDirectory!).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Skip(256)) old.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Optional persistent cache. */ }
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

    private sealed record CatalogEntry(CatalogHit? Hit);

    private static void ApplyHit(ReleaseGroup group, CatalogHit? hit)
    {
        if (hit is not null)
        {
            group.Cover ??= hit.Cover;
            if (!string.IsNullOrWhiteSpace(hit.Plot)) group.Plot = hit.Plot;
            group.Year ??= hit.Year;
            if (hit.Rating.HasValue) group.Rating = hit.Rating;
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

    /// <summary>
    /// Cache key for immutable card identity: IMDb id when present, otherwise title/year.
    /// Language / OMDb settings are part of the key so a config change revalidates artwork.
    /// </summary>
    /// <param name="group">The title group.</param>
    /// <returns>The catalog cache key.</returns>
    public static string CatalogKey(ReleaseGroup group)
    {
        var settings = MetadataSettingsKey();
        var imdb = ReleaseMapper.NormalizeImdbId(group.Imdb ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(imdb) && !string.Equals(imdb, "tt", StringComparison.Ordinal))
        {
            return settings + "|imdb:" + imdb;
        }

        return settings + "|title:" + group.Kind + "|" + group.Title + "|"
               + (group.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
    }

    private static string MetadataSettingsKey()
    {
        var c = Plugin.Instance?.Configuration;
        var lang = "de-DE-v1";
        var omdb = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(c?.OmdbApiKey ?? string.Empty)));
        return lang + "|" + omdb;
    }

    private async Task<CatalogHit?> LookupAsync(ReleaseGroup group, CancellationToken cancellationToken)
    {
        CatalogHit? localized = null, omdb = null;
        try { localized = await LocalizedCatalog.LookupAsync(_providers, group, cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException) { /* Another provider may still have data. */ }
        try
        {
            omdb = !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.OmdbApiKey)
                ? await LookupOmdbAsync(group, cancellationToken).ConfigureAwait(false)
                : await LocalizedCatalog.LookupAsync(_providers, group, cancellationToken, "The Open Movie Database").ConfigureAwait(false);
        }
        catch (HttpRequestException) { /* A missing IMDb rating must not hide the synopsis. */ }
        if (localized is not null && !string.IsNullOrWhiteSpace(localized.Plot))
        {
            if (omdb?.Rating is not null) { localized.Rating = omdb.Rating; localized.RatingSource = "IMDb"; }
            return Merge(localized, omdb);
        }

        var itunes = await LookupItunesAsync(group, cancellationToken).ConfigureAwait(false);
        // German storefront supplies the description; OMDb supplies IMDb ratings only.
        return Merge(Merge(itunes, localized), omdb is null ? null : new CatalogHit
        { Title = omdb.Title, Cover = omdb.Cover, Year = omdb.Year, Rating = omdb.Rating, RatingSource = omdb.RatingSource, Actors = omdb.Actors, Genres = omdb.Genres });
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
        if (!first.Rating.HasValue) { first.Rating = second.Rating; first.RatingSource = second.RatingSource; }
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
            Rating = ParseRating(Get(root, "imdbRating")),
            RatingSource = "IMDb"
        };
        SplitInto(hit.Genres, Get(root, "Genre"));
        SplitInto(hit.Actors, Get(root, "Actors"));
        return hit;
    }

    private async Task<CatalogHit?> LookupItunesAsync(ReleaseGroup group, CancellationToken cancellationToken)
    {
        var entity = string.Equals(group.Kind, "tv", StringComparison.OrdinalIgnoreCase) ? "tvSeason" : "movie";
        var url = "https://itunes.apple.com/search?entity=" + entity
                  + "&country=DE&lang=de_de&limit=8&term=" + Uri.EscapeDataString(group.Title);
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
                Year = ParseYear(Get(row, "releaseDate")),
                Language = "de"
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
