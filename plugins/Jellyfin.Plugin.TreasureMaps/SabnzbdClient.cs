using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Jellyfin.Plugin.TreasureMaps.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TreasureMaps;

/// <summary>
/// Minimal client for the SABnzbd HTTP API. Used to push grabbed NZBs straight into the
/// SABnzbd download queue.
/// </summary>
public class SabnzbdClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SabnzbdClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SabnzbdClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public SabnzbdClient(IHttpClientFactory httpClientFactory, ILogger<SabnzbdClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Gets a value indicating whether SABnzbd is configured.
    /// </summary>
    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Config.SabnzbdUrl) && !string.IsNullOrWhiteSpace(Config.SabnzbdApiKey);

    /// <summary>
    /// Verifies connectivity by querying the SABnzbd version endpoint.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The reported SABnzbd version.</returns>
    public async Task<string?> GetVersionAsync(CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient();
        var url = BuildApiUrl(new Dictionary<string, string?> { ["mode"] = "version" });
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await ReadJsonAsync<SabVersionResponse>(response, cancellationToken).ConfigureAwait(false);
        return result?.Version;
    }

    /// <summary>
    /// Adds an NZB payload to the SABnzbd queue via <c>mode=addfile</c>.
    /// </summary>
    /// <param name="nzbContent">The raw NZB bytes.</param>
    /// <param name="name">A human-readable name for the download.</param>
    /// <param name="category">The SABnzbd category (determines the completed folder). May be null.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The SABnzbd job ids (nzo_ids) that were created.</returns>
    public async Task<IReadOnlyList<string>> AddNzbAsync(byte[] nzbContent, string name, string? category, CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient();
        var url = BuildApiUrl(new Dictionary<string, string?>
        {
            ["mode"] = "addfile",
            ["cat"] = string.IsNullOrWhiteSpace(category) ? null : category,
            ["nzbname"] = name
        });

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(nzbContent);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-nzb");
        form.Add(fileContent, "name", SanitizeFileName(name) + ".nzb");

        using var response = await client.PostAsync(url, form, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await ReadJsonAsync<SabAddResponse>(response, cancellationToken).ConfigureAwait(false);

        if (result is null || !result.Status)
        {
            throw new InvalidOperationException("SABnzbd rejected the NZB (status=false).");
        }

        _logger.LogInformation("Queued NZB '{Name}' in SABnzbd as {Ids}", name, string.Join(",", result.NzoIds ?? new List<string>()));
        return result.NzoIds ?? new List<string>();
    }

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // SABnzbd sometimes returns application/json as text/*; read the body ourselves.
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(body) ? default : JsonSerializer.Deserialize<T>(body, _jsonOptions);
    }

    private static string BuildApiUrl(Dictionary<string, string?> parameters)
    {
        var baseUrl = Config.SabnzbdUrl.TrimEnd('/') + "/api";
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["apikey"] = Config.SabnzbdApiKey;
        query["output"] = "json";
        foreach (var kvp in parameters.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value)))
        {
            query[kvp.Key] = kvp.Value;
        }

        return baseUrl + "?" + query;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private sealed class SabVersionResponse
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }
    }

    private sealed class SabAddResponse
    {
        [JsonPropertyName("status")]
        public bool Status { get; set; }

        [JsonPropertyName("nzo_ids")]
        public List<string>? NzoIds { get; set; }
    }
}
