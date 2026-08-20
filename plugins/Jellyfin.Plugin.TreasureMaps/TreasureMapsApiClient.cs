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
    {
        var parameters = new Dictionary<string, string?>
        {
            ["q"] = string.IsNullOrWhiteSpace(query) ? "*" : query,
            ["genre"] = genre,
            ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
            ["extended"] = "1"
        };
        return GetJsonAsync<ReleaseListResponse>("movie", parameters, cancellationToken);
    }

    /// <summary>
    /// Searches for TV releases.
    /// </summary>
    /// <param name="query">Free-text query, may be null.</param>
    /// <param name="limit">Maximum number of results.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The release list response.</returns>
    public Task<ReleaseListResponse?> SearchTvAsync(string? query, int limit, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["q"] = string.IsNullOrWhiteSpace(query) ? "*" : query,
            ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
            ["extended"] = "1"
        };
        return GetJsonAsync<ReleaseListResponse>("tv", parameters, cancellationToken);
    }

    /// <summary>
    /// Gets the trending / spotlight releases.
    /// </summary>
    /// <param name="limit">Maximum number of results.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The release list response.</returns>
    public Task<ReleaseListResponse?> GetTrendingAsync(int limit, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["limit"] = limit.ToString(CultureInfo.InvariantCulture)
        };
        return GetJsonAsync<ReleaseListResponse>("trending", parameters, cancellationToken);
    }

    /// <summary>
    /// Gets the indexer capabilities (categories and genres).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The capabilities response.</returns>
    public Task<CapsResponse?> GetCapsAsync(CancellationToken cancellationToken)
        => GetJsonAsync<CapsResponse>("caps", null, cancellationToken);

    /// <summary>
    /// Gets information about the current user (used to validate the configuration).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The user info.</returns>
    public Task<UserInfo?> GetUserAsync(CancellationToken cancellationToken)
        => GetJsonAsync<UserInfo>("user", null, cancellationToken);

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

    private async Task<T?> GetJsonAsync<T>(string path, Dictionary<string, string?>? parameters, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Treasure-Maps plugin is not configured (missing Base URL or API key).");
        }

        using var client = CreateClient();
        var url = BuildUrl(path, parameters);
        _logger.LogDebug("Treasure-Maps request: {Url}", url);

        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(_jsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", Config.ApiKey);
        client.DefaultRequestHeaders.Add("Accept", "application/json");
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
