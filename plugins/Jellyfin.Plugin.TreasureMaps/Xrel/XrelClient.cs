using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Jellyfin.Plugin.TreasureMaps.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TreasureMaps.Xrel;

/// <summary>
/// Client for the public xREL v2 API. Looks up a release's ratings by its scene name
/// (<c>/release/info.json?dirname=...</c>) and caches results in memory.
/// </summary>
public class XrelClient
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    // Cache keyed by dirname; null value means "looked up, not found".
    private readonly ConcurrentDictionary<string, XrelRating?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<XrelClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="XrelClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public XrelClient(IHttpClientFactory httpClientFactory, ILogger<XrelClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Gets a value indicating whether xREL enrichment is enabled.
    /// </summary>
    public static bool IsEnabled => Config.EnableXrel && !string.IsNullOrWhiteSpace(Config.XrelBaseUrl);

    /// <summary>
    /// Parses an xREL <c>release</c> JSON payload into a rating (used for testing and reuse).
    /// </summary>
    /// <param name="json">The JSON payload.</param>
    /// <returns>The normalized rating, or null when the payload is empty/invalid.</returns>
    public static XrelRating? ParseRating(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        var release = JsonSerializer.Deserialize<XrelRelease>(json, _jsonOptions);
        return release?.ToRating();
    }

    /// <summary>
    /// Gets the xREL rating for a release by its scene/dirname, using an in-memory cache.
    /// </summary>
    /// <param name="dirname">The release scene name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The rating, or null when not found or on error.</returns>
    public async Task<XrelRating?> GetRatingByDirnameAsync(string? dirname, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dirname) || !IsEnabled)
        {
            return null;
        }

        if (_cache.TryGetValue(dirname, out var cached))
        {
            return cached;
        }

        XrelRating? rating = null;
        try
        {
            using var client = _httpClientFactory.CreateClient();
            var baseUrl = Config.XrelBaseUrl.TrimEnd('/');
            var url = baseUrl + "/release/info.json?dirname=" + HttpUtility.UrlEncode(dirname);
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _cache[dirname] = null;
                return null;
            }

            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            rating = ParseRating(body);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "xREL lookup failed for {Dirname}", dirname);
        }

        _cache[dirname] = rating;
        return rating;
    }
}
