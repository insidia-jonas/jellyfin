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

    /// <summary>
    /// Creates or updates a SABnzbd category (name + download folder) via
    /// <c>mode=set_config&amp;section=categories</c>. Requires the SABnzbd <b>full</b> API key.
    /// </summary>
    /// <param name="name">The category name (e.g. <c>movies</c>).</param>
    /// <param name="dir">The category download folder (relative or absolute).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the category is applied.</returns>
    public async Task SetCategoryAsync(string name, string? dir, CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient();
        var url = BuildApiUrl(new Dictionary<string, string?>
        {
            ["mode"] = "set_config",
            ["section"] = "categories",
            ["name"] = name,
            ["dir"] = dir
        });

        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (body.Contains("\"error\"", StringComparison.OrdinalIgnoreCase) || body.Contains("API Key Incorrect", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("SABnzbd rejected the category update (needs the full API key). Response: " + body);
        }

        _logger.LogInformation("Configured SABnzbd category '{Name}' -> '{Dir}'", name, dir);
    }

    /// <summary>
    /// Gets SABnzbd's completed-downloads base folder (<c>misc.complete_dir</c>), used to resolve
    /// relative category folders into absolute library paths. Returns null when unavailable.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The completed-downloads directory, or null.</returns>
    public async Task<string?> GetCompleteDirAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient();
            var url = BuildApiUrl(new Dictionary<string, string?>
            {
                ["mode"] = "get_config",
                ["section"] = "misc"
            });

            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("config", out var config)
                && config.TryGetProperty("misc", out var misc)
                && misc.TryGetProperty("complete_dir", out var dir)
                && dir.ValueKind == JsonValueKind.String)
            {
                var value = dir.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read SABnzbd complete_dir");
        }

        return null;
    }

    /// <summary>
    /// Gets the currently configured SABnzbd category names.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The category names.</returns>
    public async Task<IReadOnlyList<string>> GetCategoryNamesAsync(CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient();
        var url = BuildApiUrl(new Dictionary<string, string?>
        {
            ["mode"] = "get_config",
            ["section"] = "categories"
        });

        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var result = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<SabConfigResponse>(body, _jsonOptions);
        return result?.Config?.Categories?.Select(c => c.Name ?? string.Empty).Where(n => n.Length > 0).ToList()
            ?? new List<string>();
    }

    /// <summary>
    /// Gets the combined download status: active queue slots (with progress/speed/ETA) and the
    /// most recent history entries (completed/failed), for the client-side status display.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The overall speed and the download entries.</returns>
    public async Task<(string? Speed, IReadOnlyList<SabDownloadStatus> Items)> GetDownloadStatusAsync(CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient();

        var items = new List<SabDownloadStatus>();
        string? speed = null;

        var queueUrl = BuildApiUrl(new Dictionary<string, string?> { ["mode"] = "queue" });
        using (var response = await client.GetAsync(queueUrl, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("queue", out var queue))
            {
                speed = queue.TryGetProperty("speed", out var s) ? s.GetString() : null;
                if (queue.TryGetProperty("slots", out var slots) && slots.ValueKind == JsonValueKind.Array)
                {
                    foreach (var slot in slots.EnumerateArray())
                    {
                        items.Add(new SabDownloadStatus
                        {
                            Id = GetString(slot, "nzo_id"),
                            Name = GetString(slot, "filename"),
                            Status = GetString(slot, "status") ?? "Downloading",
                            Percent = double.TryParse(GetString(slot, "percentage"), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : 0,
                            TimeLeft = GetString(slot, "timeleft"),
                            SizeMb = GetString(slot, "mb"),
                            LeftMb = GetString(slot, "mbleft")
                        });
                    }
                }
            }
        }

        var historyUrl = BuildApiUrl(new Dictionary<string, string?> { ["mode"] = "history", ["limit"] = "30" });
        using (var response = await client.GetAsync(historyUrl, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("history", out var history)
                && history.TryGetProperty("slots", out var slots)
                && slots.ValueKind == JsonValueKind.Array)
            {
                foreach (var slot in slots.EnumerateArray())
                {
                    items.Add(new SabDownloadStatus
                    {
                        Id = GetString(slot, "nzo_id"),
                        Name = GetString(slot, "name"),
                        Status = GetString(slot, "status") ?? "Completed",
                        Percent = 100,
                        FailMessage = GetString(slot, "fail_message")
                    });
                }
            }
        }

        return (speed, items);
    }

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
            : null;

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

    /// <summary>
    /// One SABnzbd download entry (queue or history) for the status display.
    /// </summary>
    public sealed class SabDownloadStatus
    {
        /// <summary>Gets or sets the SABnzbd job id (nzo id).</summary>
        public string? Id { get; set; }

        /// <summary>Gets or sets the job name.</summary>
        public string? Name { get; set; }

        /// <summary>Gets or sets the status (Downloading, Queued, Completed, Failed, ...).</summary>
        public string? Status { get; set; }

        /// <summary>Gets or sets the progress percentage (0-100).</summary>
        public double Percent { get; set; }

        /// <summary>Gets or sets the remaining time (HH:MM:SS).</summary>
        public string? TimeLeft { get; set; }

        /// <summary>Gets or sets the total size in MB.</summary>
        public string? SizeMb { get; set; }

        /// <summary>Gets or sets the remaining size in MB.</summary>
        public string? LeftMb { get; set; }

        /// <summary>Gets or sets the failure message, if any.</summary>
        public string? FailMessage { get; set; }
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

    private sealed class SabConfigResponse
    {
        [JsonPropertyName("config")]
        public SabConfig? Config { get; set; }
    }

    private sealed class SabConfig
    {
        [JsonPropertyName("categories")]
        public List<SabCategory>? Categories { get; set; }
    }

    private sealed class SabCategory
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("dir")]
        public string? Dir { get; set; }
    }
}
