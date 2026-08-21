using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Jellyfin.Plugin.TreasureMaps.Api;
using Jellyfin.Plugin.TreasureMaps.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TreasureMaps;

/// <summary>
/// Thin typed client for the Treasure-Maps REST API.
/// </summary>
public class TreasureMapsApiClient
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    // Short-lived in-memory response cache keyed by request URL. Channel navigation re-fetches the
    // same lists constantly (root -> category -> back), and the indexer is slow (~2-5s per search
    // page) and rate-limits rapid calls, so caching makes browsing feel instant instead of static.
    // Entries are kept beyond their freshness window: when the indexer errors (it 503s whole
    // periods when rate-limited), the last known good response is served instead of a blank view.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset FreshUntil, object Value)> _cache = new();
    private static readonly TimeSpan SearchCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CapsCacheTtl = TimeSpan.FromHours(1);

    // Treasure-Maps category ids (movies/TV incl. language variants). Without a category the
    // /movie endpoint returns unrelated results (even books) with no movie metadata.
    // The x100 block is the German ("DE") variant, mirroring the website's Movies-DE / TV-DE rows.
    private const string MovieCategories = "2000,2100,2200,2300";
    private const string TvCategories = "5000,5100,5200,5300";

    /// <summary>The category id of the German movies block (Movies - DE).</summary>
    public const string GermanMovieCategories = "2100";

    /// <summary>The category id of the German TV block (TV - DE).</summary>
    public const string GermanTvCategories = "5100";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TreasureMapsApiClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TreasureMapsApiClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public TreasureMapsApiClient(IHttpClientFactory httpClientFactory, ILogger<TreasureMapsApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Gets a value indicating whether the plugin has enough configuration to talk to the API.
    /// </summary>
    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Config.BaseUrl) && !string.IsNullOrWhiteSpace(Config.ApiKey);

    /// <summary>
    /// Searches for movie releases.
    /// </summary>
    /// <param name="query">Free-text query, may be null.</param>
    /// <param name="genre">Genre filter, may be null.</param>
    /// <param name="limit">Maximum number of results.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The release list response.</returns>
    public Task<ReleaseListResponse?> SearchMoviesAsync(string? query, string? genre, int limit, CancellationToken cancellationToken)
        => SearchMoviesAsync(query, genre, null, limit, 0, cancellationToken);

    /// <summary>
    /// Searches for movie releases within specific categories.
    /// </summary>
    /// <param name="query">Free-text query, may be null.</param>
    /// <param name="genre">Genre filter, may be null.</param>
    /// <param name="categories">Category ids to search (null for all movie categories).</param>
    /// <param name="limit">Maximum number of results (the API times out above ~100).</param>
    /// <param name="offset">Result offset for paging.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The release list response.</returns>
    public Task<ReleaseListResponse?> SearchMoviesAsync(string? query, string? genre, string? categories, int limit, int offset, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["q"] = string.IsNullOrWhiteSpace(query) ? "*" : query,
            ["genre"] = genre,
            ["cat"] = string.IsNullOrWhiteSpace(categories) ? MovieCategories : categories,
            ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
            ["offset"] = offset > 0 ? offset.ToString(CultureInfo.InvariantCulture) : null,
            ["sort"] = "posted_desc",
            ["extended"] = "1"
        };
        return GetJsonAsync<ReleaseListResponse>("movie", parameters, SearchCacheTtl, cancellationToken);
    }

    /// <summary>
    /// Searches for TV releases.
    /// </summary>
    /// <param name="query">Free-text query, may be null.</param>
    /// <param name="limit">Maximum number of results.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The release list response.</returns>
    public Task<ReleaseListResponse?> SearchTvAsync(string? query, int limit, CancellationToken cancellationToken)
        => SearchTvAsync(query, null, limit, 0, cancellationToken);

    /// <summary>
    /// Searches for TV releases within specific categories.
    /// </summary>
    /// <param name="query">Free-text query, may be null.</param>
    /// <param name="categories">Category ids to search (null for all TV categories).</param>
    /// <param name="limit">Maximum number of results (the API times out above ~100).</param>
    /// <param name="offset">Result offset for paging.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The release list response.</returns>
    public Task<ReleaseListResponse?> SearchTvAsync(string? query, string? categories, int limit, int offset, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["q"] = string.IsNullOrWhiteSpace(query) ? null : query,
            ["cat"] = string.IsNullOrWhiteSpace(categories) ? TvCategories : categories,
            ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
            ["offset"] = offset > 0 ? offset.ToString(CultureInfo.InvariantCulture) : null,
            ["sort"] = "posted_desc",
            ["extended"] = "1"
        };
        return GetJsonAsync<ReleaseListResponse>("tv", parameters, SearchCacheTtl, cancellationToken);
    }

    /// <summary>
    /// Gets the trending / spotlight releases.
    /// </summary>
    /// <param name="limit">Maximum number of results.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The release list response.</returns>
    public Task<ReleaseListResponse?> GetTrendingAsync(int limit, CancellationToken cancellationToken)
        => GetTrendingAsync(null, limit, cancellationToken);

    /// <summary>
    /// Gets the trending / spotlight releases for a specific type.
    /// </summary>
    /// <param name="type">The trending type (<c>movie</c> or <c>tv</c>); null for all.</param>
    /// <param name="limit">Maximum number of results.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The release list response.</returns>
    public Task<ReleaseListResponse?> GetTrendingAsync(string? type, int limit, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["type"] = type,
            ["limit"] = limit.ToString(CultureInfo.InvariantCulture)
        };
        return GetJsonAsync<ReleaseListResponse>("trending", parameters, SearchCacheTtl, cancellationToken);
    }

    /// <summary>
    /// Gets the indexer capabilities (categories and genres).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The capabilities response.</returns>
    public Task<CapsResponse?> GetCapsAsync(CancellationToken cancellationToken)
        => GetJsonAsync<CapsResponse>("caps", null, CapsCacheTtl, cancellationToken);

    /// <summary>
    /// Gets information about the current user (used to validate the configuration).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The user info.</returns>
    public async Task<UserInfo?> GetUserAsync(CancellationToken cancellationToken)
    {
        var response = await GetJsonAsync<UserInfoResponse>("user", null, TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
        return response?.User;
    }

    /// <summary>
    /// Downloads the NZB for a release, following the API redirect.
    /// </summary>
    /// <param name="guid">The release GUID.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The NZB payload.</returns>
    public async Task<byte[]> DownloadNzbAsync(string guid, CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        var url = BuildUrl($"releases/{Uri.EscapeDataString(guid)}/download", null);
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<T?> GetJsonAsync<T>(string path, Dictionary<string, string?>? parameters, TimeSpan cacheTtl, CancellationToken cancellationToken)
        where T : class
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Treasure-Maps plugin is not configured (missing Base URL or API key).");
        }

        var url = BuildUrl(path, parameters);
        (DateTimeOffset FreshUntil, object Value) cached = default;
        var hasCached = cacheTtl > TimeSpan.Zero && _cache.TryGetValue(url, out cached);
        if (hasCached && cached.FreshUntil > DateTimeOffset.UtcNow && cached.Value is T hit)
        {
            return hit;
        }

        try
        {
            using var client = CreateClient();
            _logger.LogDebug("Treasure-Maps request: {Url}", url);

            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<T>(_jsonOptions, cancellationToken).ConfigureAwait(false);

            if (cacheTtl > TimeSpan.Zero && result is not null)
            {
                if (_cache.Count > 500)
                {
                    _cache.Clear();
                }

                _cache[url] = (DateTimeOffset.UtcNow.Add(cacheTtl), result);
            }

            return result;
        }
        catch (Exception ex) when (hasCached && cached.Value is T stale)
        {
            _logger.LogWarning(ex, "Treasure-Maps request failed; serving the last known response for {Url}", url);
            return stale;
        }
    }

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", Config.ApiKey);
        client.DefaultRequestHeaders.Add("Accept", "application/json");

        // Cold indexer queries can hang until the gateway 504s (~55s); give up earlier so a slow
        // page is dropped quickly and the rest of the (paged) view still renders.
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }

    private static string BuildUrl(string path, Dictionary<string, string?>? parameters)
    {
        var baseUrl = Config.BaseUrl.TrimEnd('/');
        if (!baseUrl.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl += "/api/v1";
        }

        var url = baseUrl + "/" + path.TrimStart('/');
        if (parameters is null)
        {
            return url;
        }

        var query = HttpUtility.ParseQueryString(string.Empty);
        foreach (var kvp in parameters)
        {
            if (!string.IsNullOrWhiteSpace(kvp.Value))
            {
                query[kvp.Key] = kvp.Value;
            }
        }

        var queryString = query.ToString();
        return string.IsNullOrEmpty(queryString) ? url : url + "?" + queryString;
    }
}
