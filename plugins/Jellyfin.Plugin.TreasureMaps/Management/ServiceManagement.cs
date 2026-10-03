using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TreasureMaps.Configuration;
using MediaBrowser.Controller.Security;

namespace Jellyfin.Plugin.TreasureMaps.Management;

/// <summary>Admin-only service operations with credentials confined to the server.</summary>
public sealed class ServiceManagement : IDisposable
{
    private readonly IHttpClientFactory _http;
    private readonly IAuthenticationManager _authentication;
    private readonly Func<PluginConfiguration> _configuration;
    private readonly SemaphoreSlim _changes = new(1, 1);
    private readonly Dictionary<string, (string Identity, DateTimeOffset At, Task<object> Task)> _status = new();

    public ServiceManagement(IHttpClientFactory http, IAuthenticationManager authentication)
        : this(http, authentication, () => Plugin.Instance!.Configuration) { }

    public ServiceManagement(IHttpClientFactory http, IAuthenticationManager authentication, Func<PluginConfiguration> configuration)
    {
        _http = http;
        _authentication = authentication;
        _configuration = configuration;
    }

    public void Dispose() => _changes.Dispose();

    private (string Url, string Key) Settings(string service)
    {
        var c = _configuration();
        return service switch
        {
            "radarr" => (c.RadarrUrl, c.RadarrApiKey),
            "sonarr" => (c.SonarrUrl, c.SonarrApiKey),
            "sabnzbd" => (c.SabnzbdUrl, c.SabnzbdApiKey),
            "treasuremaps" => (c.BaseUrl, c.ApiKey),
            _ => throw new ArgumentException("Unknown service.", nameof(service))
        };
    }

    public static string ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("Eine HTTP(S)-Adresse ohne eingebettete Zugangsdaten oder Suchparameter ist erforderlich.");
        }

        return uri.AbsoluteUri.TrimEnd('/');
    }

    public async Task<JsonNode> SendAsync(string service, string route, CancellationToken ct, JsonNode? body = null, HttpMethod? method = null)
    {
        var (url, key) = Settings(service);
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key)) { throw new InvalidOperationException("Verbindung noch nicht eingerichtet."); }
        url = ValidateUrl(url);
        var suffix = service switch
        {
            "sabnzbd" => "/api?output=json&apikey=" + Uri.EscapeDataString(key) + "&" + route,
            "treasuremaps" => (url.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase) ? "/" : "/api/v1/") + route,
            _ => "/api/v3/" + route
        };
        using var request = new HttpRequestMessage(method ?? (body is null ? HttpMethod.Get : HttpMethod.Post), url + suffix);
        if (service != "sabnzbd") { request.Headers.Add("X-Api-Key", key); }
        if (body is not null) { request.Content = JsonContent.Create(body); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        using var client = _http.CreateClient("TreasureMaps.Management");
        using var response = await client.SendAsync(request, deadline.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) { throw new InvalidOperationException(service + " meldet HTTP " + (int)response.StatusCode + "."); }
        var text = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
        var data = string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text) ?? new JsonObject();
        if (data is JsonObject obj && (obj["error"] is not null || obj["status"]?.ToString() == "false"))
        {
            throw new InvalidOperationException(service + " hat die Anfrage abgelehnt. API-Schlüssel und Berechtigungen prüfen.");
        }

        return data;
    }

    public Task<object> StatusAsync(string service, CancellationToken ct)
    {
        var settings = Settings(service);
        var identity = settings.Url + "|" + settings.Key;
        lock (_status)
        {
            if (_status.TryGetValue(service, out var cached) && cached.Identity == identity
                && (!cached.Task.IsCompleted || DateTimeOffset.UtcNow - cached.At < TimeSpan.FromSeconds(30)))
            {
                return cached.Task.WaitAsync(ct);
            }

            var pending = ReadStatusAsync(service, CancellationToken.None);
            _status[service] = (identity, DateTimeOffset.UtcNow, pending);
            return pending.WaitAsync(ct);
        }
    }

    public async Task ValidateSettingsAsync(string service, PluginConfiguration settings, CancellationToken ct)
    {
        using var candidate = new ServiceManagement(_http, _authentication, () => settings);
        if (service == "jellyfin") { return; }
        await candidate.SendAsync(service, service switch { "sabnzbd" => "mode=queue&limit=0", "treasuremaps" => "user", _ => "system/status" }, ct).ConfigureAwait(false);
    }

    private async Task<object> ReadStatusAsync(string service, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        var (url, key) = Settings(service);
        var configured = !string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(key);
        try
        {
            var route = service switch { "sabnzbd" => "mode=queue&limit=0", "treasuremaps" => "user", _ => "system/status" };
            var status = await SendAsync(service, route, ct).ConfigureAwait(false);
            return new { id = service, configured, online = true, url, version = status is JsonObject ? (status["version"] ?? status["queue"]?["version"])?.ToString() : null, responseMs = timer.ElapsedMilliseconds, message = "Verbunden" };
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or OperationCanceledException or ArgumentException or System.Text.Json.JsonException)
        {
            ct.ThrowIfCancellationRequested();
            return new { id = service, configured, online = false, url, version = (string?)null, responseMs = timer.ElapsedMilliseconds,
                message = configured ? "Nicht erreichbar oder Anmeldung fehlgeschlagen" : "Einrichtung fehlt" };
        }
    }

    public async Task<object> OptionsAsync(string service, CancellationToken ct)
    {
        if (service is not ("sonarr" or "radarr")) { throw new ArgumentException("Only Sonarr/Radarr have quality profiles."); }
        var profiles = await SendAsync(service, "qualityprofile", ct).ConfigureAwait(false);
        var folders = await SendAsync(service, "rootfolder", ct).ConfigureAwait(false);
        return new
        {
            profiles = profiles.AsArray().Select(p => new { id = p?["id"]?.GetValue<int>(), name = p?["name"]?.ToString() }).ToArray(),
            folders = folders.AsArray().Select(p => new { path = p?["path"]?.ToString(), freeSpace = p?["freeSpace"]?.GetValue<long>() }).ToArray()
        };
    }

    public async Task<object[]> QueueAsync(string service, CancellationToken ct)
    {
        var response = await SendAsync(service, service == "sabnzbd" ? "mode=queue" : "queue?page=1&pageSize=30&includeUnknownSeriesItems=false&includeUnknownMovieItems=false", ct).ConfigureAwait(false);
        var rows = service == "sabnzbd" ? response["queue"]?["slots"]?.AsArray() : response["records"]?.AsArray();
        return (rows ?? new JsonArray()).Select(row => (object)new
        {
            service,
            title = row?[service == "sabnzbd" ? "filename" : "title"]?.ToString() ?? "Download",
            status = row?["status"]?.ToString() ?? "Unbekannt",
            timeLeft = row?[service == "sabnzbd" ? "timeleft" : "timeleft"]?.ToString(),
            percent = service == "sabnzbd" ? Number(row?["percentage"]) : Percent(row?["size"], row?["sizeleft"])
        }).ToArray();
    }

    private static double Number(JsonNode? value) => double.TryParse(value?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : 0;
    private static double Percent(JsonNode? size, JsonNode? left) => Number(size) > 0 ? Math.Clamp(100 * (1 - Number(left) / Number(size)), 0, 100) : 0;

    public async Task ActionAsync(string service, string action, CancellationToken ct)
    {
        if (service == "sabnzbd" && action is "pause" or "resume")
        {
            await SendAsync(service, "mode=" + action, ct).ConfigureAwait(false);
        }
        else if (service is "radarr" or "sonarr" && action == "refresh")
        {
            await SendAsync(service, "command", ct, new JsonObject { ["name"] = service == "radarr" ? "RefreshMovie" : "RefreshSeries" }).ConfigureAwait(false);
        }
        else { throw new ArgumentException("Diese Aktion wird nicht unterstützt."); }
    }

    public sealed record Connection(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("from")] string From,
        [property: JsonPropertyName("to")] string To,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("detail")] string Detail,
        [property: JsonPropertyName("changed")] bool Changed = false);

    /// <summary>Reconciles known API links. Existing profiles, categories and unrelated services survive.</summary>
    public async Task<IReadOnlyList<Connection>> ConnectionsAsync(bool apply, CancellationToken ct)
    {
        await _changes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var links = new List<Connection>();
            try
            {
                var c = _configuration();
                var categories = await SendAsync("sabnzbd", "mode=get_config&section=categories", ct).ConfigureAwait(false);
                foreach (var (name, folder) in new[] { (c.SabnzbdMovieCategory, c.SabnzbdMovieFolder), (c.SabnzbdTvCategory, c.SabnzbdTvFolder) })
                {
                    if (string.IsNullOrWhiteSpace(name)) { continue; }
                    var exists = categories["config"]?["categories"]?.AsArray().Any(r => r?["name"]?.ToString() == name) == true;
                    if (!exists && apply)
                    {
                        await SendAsync("sabnzbd", "mode=set_config&section=categories&name=" + Uri.EscapeDataString(name) + "&dir=" + Uri.EscapeDataString(folder ?? name), ct).ConfigureAwait(false);
                    }

                    links.Add(new Connection("treasuremaps-sab-" + name, "Treasure Maps", "SABnzbd", exists || apply ? "ready" : "missing", "Direktdownload-Kategorie: " + name, !exists && apply));
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or OperationCanceledException or ArgumentException or System.Text.Json.JsonException)
            {
                ct.ThrowIfCancellationRequested();
                links.Add(new Connection("treasuremaps-sab", "Treasure Maps", "SABnzbd", "error", "Downloadkategorien konnten nicht geprüft werden."));
            }

            foreach (var service in new[] { "radarr", "sonarr" })
            {
                try { await ReconcileArrAsync(service, apply, links, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or OperationCanceledException or ArgumentException or System.Text.Json.JsonException)
                {
                    ct.ThrowIfCancellationRequested();
                    links.Add(new Connection(service + "-unavailable", "Jellyfin", service, "error", "Verbindung prüfen; für diesen Dienst wurden keine weiteren Änderungen ausgeführt."));
                }
            }

            return links;
        }
        finally { _changes.Release(); }
    }

    private async Task ReconcileArrAsync(string service, bool apply, List<Connection> links, CancellationToken ct)
    {
        var c = _configuration();
        var isSeries = service == "sonarr";
        var profile = isSeries ? c.SonarrQualityProfileId : c.RadarrQualityProfileId;
        var root = isSeries ? c.SonarrRootFolder : c.RadarrRootFolder;
        var profiles = (await SendAsync(service, "qualityprofile", ct).ConfigureAwait(false)).AsArray();
        var roots = (await SendAsync(service, "rootfolder", ct).ConfigureAwait(false)).AsArray();
        var requestsReady = profiles.Any(p => p?["id"]?.GetValue<int>() == profile) && roots.Any(r => r?["path"]?.ToString() == root);
        links.Add(new Connection("jellyfin-" + service, "Jellyfin", service, requestsReady ? "ready" : "missing",
            requestsReady ? "Anfragen verwenden das vorhandene Profil und den Zielordner." : "Qualitätsprofil und Zielordner in den Verbindungen auswählen."));

        var callback = new Uri(ValidateUrl(c.JellyfinCallbackUrl));
        var notifications = (await SendAsync(service, "notification", ct).ConfigureAwait(false)).AsArray();
        var notification = FindResource(notifications, "MediaBrowser", "host", callback.Host, callback.Port);
        var callbackKey = Field(notification, "apiKey")?.ToString();
        var knownKeys = await _authentication.GetApiKeys().ConfigureAwait(false);
        var maskedKey = IsMasked(Field(notification, "apiKey"));
        var validKey = maskedKey || (!string.IsNullOrWhiteSpace(callbackKey) && knownKeys.Any(k => k.AccessToken == callbackKey));
        if (maskedKey) { callbackKey = knownKeys.FirstOrDefault(k => k.AppName == "Sonarr-Radarr Library Refresh")?.AccessToken; }
        if (!validKey && apply)
        {
            const string app = "Sonarr-Radarr Library Refresh";
            callbackKey = knownKeys.FirstOrDefault(k => k.AppName == app)?.AccessToken;
            if (callbackKey is null)
            {
                await _authentication.CreateApiKey(app).ConfigureAwait(false);
                callbackKey = (await _authentication.GetApiKeys().ConfigureAwait(false)).First(k => k.AppName == app).AccessToken;
            }
        }

        await EnsureResourceAsync(service, "notification", "MediaBrowser", "Jellyfin Library Refresh", notification,
            new JsonObject { ["host"] = callback.Host, ["port"] = callback.Port, ["useSsl"] = callback.Scheme == "https", ["urlBase"] = callback.AbsolutePath.TrimEnd('/'), ["apiKey"] = callbackKey ?? string.Empty, ["notify"] = false, ["updateLibrary"] = true },
            new JsonObject { ["onDownload"] = true, ["onUpgrade"] = true }, apply, service, "Jellyfin", links, ct, validKey).ConfigureAwait(false);

        var sab = new Uri(ValidateUrl(c.SabnzbdUrl));
        var clients = (await SendAsync(service, "downloadclient", ct).ConfigureAwait(false)).AsArray();
        var downloadClient = FindResource(clients, "Sabnzbd", "host", sab.Host, sab.Port);
        var categoryField = isSeries ? "tvCategory" : "movieCategory";
        var category = Field(downloadClient, categoryField)?.ToString();
        if (string.IsNullOrWhiteSpace(category)) { category = service; }
        var categories = await SendAsync("sabnzbd", "mode=get_config&section=categories", ct).ConfigureAwait(false);
        var categoryExists = categories["config"]?["categories"]?.AsArray().Any(r => r?["name"]?.ToString() == category) == true;
        if (!categoryExists && apply)
        {
            await SendAsync("sabnzbd", "mode=set_config&section=categories&name=" + Uri.EscapeDataString(category) + "&dir=" + Uri.EscapeDataString(category), ct).ConfigureAwait(false);
        }

        links.Add(new Connection(service + "-category", "SABnzbd", service, categoryExists || apply ? "ready" : "missing", "Downloadkategorie: " + category, !categoryExists && apply));

        await EnsureResourceAsync(service, "downloadclient", "Sabnzbd", "SABnzbd", downloadClient,
            new JsonObject { ["host"] = sab.Host, ["port"] = sab.Port, ["useSsl"] = sab.Scheme == "https", ["urlBase"] = sab.AbsolutePath.TrimEnd('/'), ["apiKey"] = c.SabnzbdApiKey, [categoryField] = category },
            new JsonObject { ["enable"] = true, ["priority"] = downloadClient?["priority"]?.DeepClone() ?? JsonValue.Create(1) }, apply, service, "SABnzbd", links, ct).ConfigureAwait(false);


        var indexers = (await SendAsync(service, "indexer", ct).ConfigureAwait(false)).AsArray();
        var indexerBase = c.BaseUrl.TrimEnd('/');
        if (indexerBase.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase)) { indexerBase = indexerBase[..^7]; }
        _ = ValidateUrl(indexerBase);
        var indexer = FindResource(indexers, "Newznab", "baseUrl", indexerBase);
        var fields = new JsonObject { ["baseUrl"] = indexerBase, ["apiKey"] = c.ApiKey };
        if (indexer is null) { fields["categories"] = new JsonArray(isSeries ? 5000 : 2000); }
        await EnsureResourceAsync(service, "indexer", "Newznab", "Treasure-Maps", indexer, fields,
            new JsonObject { ["enableRss"] = true, ["enableAutomaticSearch"] = true, ["enableInteractiveSearch"] = true }, apply, "Treasure Maps", service, links, ct).ConfigureAwait(false);
    }

    private async Task EnsureResourceAsync(string service, string resource, string implementation, string name, JsonObject? existing,
        JsonObject fields, JsonObject properties, bool apply, string from, string to, List<Connection> links, CancellationToken ct, bool credentialsValid = true)
    {
        var maskedKey = IsMasked(Field(existing, "apiKey"));
        var changed = existing is null || !credentialsValid || fields.Any(k => !(k.Key == "apiKey" && maskedKey) && !Equivalent(Field(existing, k.Key), k.Value))
            || properties.Any(k => !Equivalent(existing?[k.Key], k.Value));
        if (apply)
        {
            JsonObject candidate;
            if (existing is not null) { candidate = (JsonObject)existing.DeepClone(); }
            else
            {
                var schemas = (await SendAsync(service, resource + "/schema", ct).ConfigureAwait(false)).AsArray();
                candidate = (JsonObject)(schemas.OfType<JsonObject>().FirstOrDefault(s => s["implementation"]?.ToString() == implementation)?.DeepClone()
                    ?? throw new InvalidOperationException("Dienst unterstützt die benötigte Verbindung nicht."));
                candidate["name"] = name;
            }

            foreach (var property in properties) { candidate[property.Key] = property.Value?.DeepClone(); }
            foreach (var field in fields)
            {
                // Arr returns password masks. Testing the existing resource ID resolves its stored key.
                if (field.Key == "apiKey" && maskedKey) { continue; }
                var target = candidate["fields"]?.AsArray().OfType<JsonObject>().FirstOrDefault(f => f["name"]?.ToString() == field.Key);
                if (target is null) { throw new InvalidOperationException("API-Schema der Verbindung hat sich geändert."); }
                target["value"] = field.Value?.DeepClone();
            }

            // Validate against the real service before persisting, including unchanged links.
            try { await SendAsync(service, resource + "/test", ct, candidate).ConfigureAwait(false); }
            catch (InvalidOperationException) when (maskedKey)
            {
                var key = fields["apiKey"]?.ToString();
                if (string.IsNullOrWhiteSpace(key) && resource == "notification")
                {
                    const string app = "Sonarr-Radarr Library Refresh";
                    await _authentication.CreateApiKey(app).ConfigureAwait(false);
                    key = (await _authentication.GetApiKeys().ConfigureAwait(false)).First(k => k.AppName == app).AccessToken;
                }

                candidate["fields"]!.AsArray().OfType<JsonObject>().First(f => f["name"]?.ToString() == "apiKey")["value"] = key;
                await SendAsync(service, resource + "/test", ct, candidate).ConfigureAwait(false);
                changed = true;
            }
            if (changed)
            {
                var id = existing?["id"]?.GetValue<int>() ?? 0;
                await SendAsync(service, resource + (id > 0 ? "/" + id : string.Empty), ct, candidate, id > 0 ? HttpMethod.Put : HttpMethod.Post).ConfigureAwait(false);
            }
        }

        links.Add(new Connection(service + "-" + resource, from, to, apply ? "verified" : changed ? "missing" : "ready",
            apply ? (changed ? "Ergänzt und Verbindungstest bestanden." : "Vorhanden; Verbindungstest bestanden.")
                : changed ? "Verbindung fehlt oder benötigt eine Anpassung." : "Vorhandene Konfiguration passt.", apply && changed));
    }

    private static JsonObject? FindResource(JsonArray resources, string implementation, string field, string target, int? port = null)
        => resources.OfType<JsonObject>().FirstOrDefault(r => r["implementation"]?.ToString() == implementation
            && SameAddress(Field(r, field)?.ToString(), target)
            && (!port.HasValue || Number(Field(r, "port")) == port.Value));

    private static bool SameAddress(string? left, string right)
    {
        static string Normalize(string value) => value.TrimEnd('/').ToLowerInvariant() is "localhost" or "127.0.0.1" or "::1" ? "loopback" : value.TrimEnd('/').ToLowerInvariant();
        return left is not null && Normalize(left) == Normalize(right);
    }

    private static JsonNode? Field(JsonObject? resource, string name)
        => resource?["fields"]?.AsArray().OfType<JsonObject>().FirstOrDefault(f => f["name"]?.ToString() == name)?["value"];

    private static bool IsMasked(JsonNode? value) => value?.ToString() is { Length: > 0 } text && text.All(c => c == '*');

    private static bool Equivalent(JsonNode? left, JsonNode? right)
        => (left is null && right?.ToString() == string.Empty) || (right is null && left?.ToString() == string.Empty) || JsonNode.DeepEquals(left, right);
}
