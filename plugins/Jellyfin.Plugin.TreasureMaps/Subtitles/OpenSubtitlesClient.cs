using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Jellyfin.Plugin.TreasureMaps.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TreasureMaps.Subtitles;

/// <summary>
/// Client for the OpenSubtitles REST API (opensubtitles.com v1): login, search and download.
/// </summary>
public sealed class OpenSubtitlesClient : IDisposable
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenSubtitlesClient> _logger;
    private readonly SemaphoreSlim _loginLock = new(1, 1);

    private string? _token;
    private DateTime _tokenAcquired = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenSubtitlesClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public OpenSubtitlesClient(IHttpClientFactory httpClientFactory, ILogger<OpenSubtitlesClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>Gets a value indicating whether OpenSubtitles is enabled and has an API key.</summary>
    public static bool IsEnabled =>
        Config.EnableOpenSubtitles && !string.IsNullOrWhiteSpace(Config.OpenSubtitlesApiKey);

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("Api-Key", Config.OpenSubtitlesApiKey);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin.Plugin.TreasureMaps/1.0");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static string BaseUrl => (string.IsNullOrWhiteSpace(Config.OpenSubtitlesBaseUrl)
        ? "https://api.opensubtitles.com/api/v1"
        : Config.OpenSubtitlesBaseUrl).TrimEnd('/');

    /// <summary>
    /// Logs in (when username/password are configured) and caches the bearer token.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The bearer token, or null when no credentials are configured.</returns>
    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Config.OpenSubtitlesUsername) || string.IsNullOrWhiteSpace(Config.OpenSubtitlesPassword))
        {
            return null;
        }

        if (_token is not null && (DateTime.UtcNow - _tokenAcquired) < TimeSpan.FromHours(12))
        {
            return _token;
        }

        await _loginLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_token is not null && (DateTime.UtcNow - _tokenAcquired) < TimeSpan.FromHours(12))
            {
                return _token;
            }

            using var client = CreateClient();
            var payload = JsonSerializer.Serialize(new { username = Config.OpenSubtitlesUsername, password = Config.OpenSubtitlesPassword });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(BaseUrl + "/login", content, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var login = JsonSerializer.Deserialize<OsLoginResponse>(body, _jsonOptions);
            _token = login?.Token;
            _tokenAcquired = DateTime.UtcNow;
            return _token;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    /// <summary>
    /// Searches for subtitles.
    /// </summary>
    /// <param name="parameters">The query parameters.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The search response.</returns>
    public async Task<OsSearchResponse?> SearchAsync(IReadOnlyDictionary<string, string?> parameters, CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        var query = HttpUtility.ParseQueryString(string.Empty);
        foreach (var kvp in parameters.Where(k => !string.IsNullOrWhiteSpace(k.Value)))
        {
            query[kvp.Key] = kvp.Value;
        }

        var url = BaseUrl + "/subtitles?" + query;
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<OsSearchResponse>(body, _jsonOptions);
    }

    /// <summary>
    /// Requests a download link for a subtitle file (requires login).
    /// </summary>
    /// <param name="fileId">The subtitle file id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The download response with the link.</returns>
    public async Task<OsDownloadResponse?> RequestDownloadAsync(int fileId, CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var payload = JsonSerializer.Serialize(new { file_id = fileId });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(BaseUrl + "/download", content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<OsDownloadResponse>(body, _jsonOptions);
    }

    /// <summary>
    /// Fetches the subtitle content from a download link.
    /// </summary>
    /// <param name="link">The direct download link.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The subtitle bytes.</returns>
    public async Task<byte[]> DownloadContentAsync(string link, CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient();
        using var response = await client.GetAsync(link, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _loginLock.Dispose();
    }
}
