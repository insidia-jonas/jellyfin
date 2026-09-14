using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Jellyfin.Plugin.TreasureMaps.Api;
using Jellyfin.Plugin.TreasureMaps.Configuration;
using Jellyfin.Plugin.TreasureMaps.Listing;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TreasureMaps;

/// <summary>
/// Thin typed client for the Treasure-Maps REST API.
/// </summary>
public class TreasureMapsApiClient
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Browse lists (<c>q=*</c> / empty / one letter) stay cached for minutes. Typed queries
    /// (2+ characters) use a short TTL so Search/Hints typeahead stays live.
    /// </summary>
    /// <param name="query">The free-text query sent as <c>q=</c>.</param>
    /// <returns>The cache duration for that query.</returns>
    public static TimeSpan CacheTtlForQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)
            || string.Equals(query, "*", StringComparison.Ordinal)
            || query.Trim().Length < 2)
        {
            return TreasureMapsListingCache.BrowseFreshTtl;
        }

        return TreasureMapsListingCache.LiveSearchFreshTtl;
    }

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
    private readonly TreasureMapsListingCache _listingCache;
    private readonly ILogger<TreasureMapsApiClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TreasureMapsApiClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="listingCache">The freshness-gated snapshot cache.</param>
    /// <param name="logger">The logger.</param>
    public TreasureMapsApiClient(
        IHttpClientFactory httpClientFactory,
        TreasureMapsListingCache listingCache,
        ILogger<TreasureMapsApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _listingCache = listingCache;
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
        return GetJsonAsync<ReleaseListResponse>("movie", parameters, CacheTtlForQuery(query), cancellationToken);
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
        return GetJsonAsync<ReleaseListResponse>("tv", parameters, CacheTtlForQuery(query), cancellationToken);
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
        return GetJsonAsync<ReleaseListResponse>("trending", parameters, TreasureMapsListingCache.BrowseFreshTtl, cancellationToken);
    }

    /// <summary>
    /// Gets one of the website's TMDB spotlight feeds (popular / trending day / trending week /
    /// top rated / now playing-on air), containing only titles that have releases on the indexer.
    /// </summary>
    /// <param name="type">The media type (<c>movie</c> or <c>tv</c>).</param>
    /// <param name="feed">The feed number (1=popular, 2=trending today, 3=trending week, 4=top rated, 5=now playing/on air).</param>
    /// <param name="limit">Maximum number of results.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The release list response.</returns>
    public Task<ReleaseListResponse?> GetSpotlightAsync(string type, int feed, int limit, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["type"] = type,
            ["feed"] = feed.ToString(CultureInfo.InvariantCulture),
            ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
            ["extended"] = "1"
        };
        return GetJsonAsync<ReleaseListResponse>("spotlight", parameters, TreasureMapsListingCache.SpotlightFreshTtl, cancellationToken);
    }

    /// <summary>
    /// Gets the indexer capabilities (categories and genres).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The capabilities response.</returns>
    public Task<CapsResponse?> GetCapsAsync(CancellationToken cancellationToken)
        => GetJsonAsync<CapsResponse>("caps", null, TreasureMapsListingCache.CapsFreshTtl, cancellationToken);

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
        if (cacheTtl <= TimeSpan.Zero)
        {
            var (value, _, notModified) = await SendJsonAsync<T>(url, null, cancellationToken).ConfigureAwait(false);
            return notModified ? null : value;
        }

        return await _listingCache.GetOrFetchAsync<T>(
            url,
            cacheTtl,
            async (validator, token) =>
            {
                var (value, etag, notModified) = await SendJsonAsync<T>(url, validator, token).ConfigureAwait(false);
                if (notModified)
                {
                    return ListingFetch<T>.Validated(etag);
                }

                if (value is null || TreasureMapsListingCache.IsEmptyListing(value))
                {
                    return ListingFetch<T>.DoNotStore(value);
                }

                return ListingFetch<T>.Store(value, etag, TreasureMapsListingCache.HashPayload(value));
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(T? Value, string? ETag, bool NotModified)> SendJsonAsync<T>(
        string url,
        string? validator,
        CancellationToken cancellationToken)
        where T : class
    {
        using var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(validator))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", validator);
        }

        _logger.LogDebug("Treasure-Maps request: {Url}", url);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return (null, validator, true);
        }

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<T>(_jsonOptions, cancellationToken).ConfigureAwait(false);
        var etag = response.Headers.ETag?.Tag;
        return (result, etag, false);
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
