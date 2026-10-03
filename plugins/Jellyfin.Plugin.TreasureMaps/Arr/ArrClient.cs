using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TreasureMaps.Configuration;

namespace Jellyfin.Plugin.TreasureMaps.Arr;

/// <summary>A title identified by the Jellyfin item's metadata, never an arbitrary client URL.</summary>
public sealed record ArrTitle(string Name, bool IsSeries, string ImdbId, int? Year);

/// <summary>Safe status returned to clients; API credentials remain on the server.</summary>
public sealed record ArrStatus(bool Enabled, string Service, int? Id, bool Monitored, bool Available, double? Percent, string Message);

/// <summary>Uses the configured Radarr/Sonarr profiles, folders and indexers for requests.</summary>
public sealed class ArrClient : IDisposable
{
    private readonly IHttpClientFactory _http;
    private readonly Func<PluginConfiguration> _configuration;
    private readonly SemaphoreSlim _requests = new(1, 1);

    public ArrClient(IHttpClientFactory http) : this(http, () => Plugin.Instance!.Configuration) { }

    public ArrClient(IHttpClientFactory http, Func<PluginConfiguration> configuration)
    {
        _http = http;
        _configuration = configuration;
    }

    public void Dispose() => _requests.Dispose();

    private sealed record Settings(string Name, string Url, string Key, string Resource, string Identity, int Profile, string Root);

    private Settings GetSettings(bool series)
    {
        var c = _configuration();
        return series
            ? new("Sonarr", c.SonarrUrl, c.SonarrApiKey, "series", "tvdbId", c.SonarrQualityProfileId, c.SonarrRootFolder)
            : new("Radarr", c.RadarrUrl, c.RadarrApiKey, "movie", "tmdbId", c.RadarrQualityProfileId, c.RadarrRootFolder);
    }

    private static bool Configured(Settings s) => !string.IsNullOrWhiteSpace(s.Url) && !string.IsNullOrWhiteSpace(s.Key) && s.Profile > 0 && !string.IsNullOrWhiteSpace(s.Root);

    private async Task<JsonNode> SendAsync(Settings s, HttpMethod method, string route, JsonNode? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, s.Url.TrimEnd('/') + "/api/v3/" + route);
        request.Headers.Add("X-Api-Key", s.Key);
        if (body is not null) { request.Content = JsonContent.Create(body); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        using var response = await _http.CreateClient("TreasureMaps.Arr").SendAsync(request, deadline.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Response bodies and request URLs can contain credentials or private paths.
            throw new InvalidOperationException($"{s.Name} meldet HTTP {(int)response.StatusCode}.");
        }

        return await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: deadline.Token).ConfigureAwait(false) ?? new JsonObject();
    }

    private async Task<JsonObject?> FindAsync(Settings s, ArrTitle title, CancellationToken ct)
    {
        var all = await SendAsync(s, HttpMethod.Get, s.Resource, null, ct).ConfigureAwait(false);
        return all.AsArray().OfType<JsonObject>().FirstOrDefault(x => string.Equals(x["imdbId"]?.GetValue<string>(), title.ImdbId, StringComparison.OrdinalIgnoreCase));
    }

    private static ArrStatus Status(Settings s, JsonObject? item, double? percent = null)
    {
        var monitored = item?["monitored"]?.GetValue<bool>() == true;
        var available = item?["hasFile"]?.GetValue<bool>() == true || (item?["statistics"]?["episodeFileCount"]?.GetValue<int>() ?? 0) > 0;
        var message = percent.HasValue ? $"Download: {Math.Round(percent.Value)} %"
            : available ? (s.Resource == "series" ? "Episoden in der Bibliothek verfügbar" : "In der Bibliothek verfügbar") : monitored ? $"In {s.Name} überwacht" : $"Über {s.Name} anfordern";
        return new(Configured(s), s.Name, item?["id"]?.GetValue<int>(), monitored, available, percent, message);
    }

    public async Task<ArrStatus> GetStatusAsync(ArrTitle title, CancellationToken ct)
    {
        var s = GetSettings(title.IsSeries);
        if (!Configured(s)) { return new(false, s.Name, null, false, false, null, "Nicht eingerichtet"); }
        if (!System.Text.RegularExpressions.Regex.IsMatch(title.ImdbId, "^tt[0-9]+$"))
        {
            return new(false, s.Name, null, false, false, null, "Für diesen Titel fehlt eine eindeutige IMDb-ID.");
        }
        var item = await FindAsync(s, title, ct).ConfigureAwait(false);
        double? percent = null;
        if (item is not null)
        {
            var queue = await SendAsync(s, HttpMethod.Get, "queue?page=1&pageSize=200", null, ct).ConfigureAwait(false);
            var jobs = queue["records"]?.AsArray().OfType<JsonObject>().Where(x => x[title.IsSeries ? "seriesId" : "movieId"]?.GetValue<int>() == item["id"]!.GetValue<int>()).ToList();
            if (jobs is { Count: > 0 })
            {
                var size = jobs.Sum(x => x["size"]?.GetValue<double>() ?? 0);
                var left = jobs.Sum(x => x["sizeleft"]?.GetValue<double>() ?? 0);
                if (size > 0) { percent = Math.Clamp(100 * (size - left) / size, 0, 100); }
            }
        }

        return Status(s, item, percent);
    }

    public async Task<ArrStatus> RequestAsync(ArrTitle title, CancellationToken ct)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(title.ImdbId, "^tt[0-9]+$"))
        {
            throw new InvalidOperationException("Für diesen Titel fehlt eine eindeutige IMDb-ID.");
        }

        var s = GetSettings(title.IsSeries);
        if (!Configured(s)) { throw new InvalidOperationException($"{s.Name} ist noch nicht eingerichtet."); }
        await _requests.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = await FindAsync(s, title, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                // Repeated button presses must not queue duplicate searches or overwrite profiles.
                if (existing["monitored"]?.GetValue<bool>() == true || existing["hasFile"]?.GetValue<bool>() == true)
                {
                    return Status(s, existing);
                }

                existing["monitored"] = true;
                await SendAsync(s, HttpMethod.Put, s.Resource + "/" + existing["id"]!.GetValue<int>(), existing, ct).ConfigureAwait(false);
                await SearchAsync(s, existing["id"]!.GetValue<int>(), title.IsSeries, ct).ConfigureAwait(false);
                return Status(s, existing);
            }

            var results = await SendAsync(s, HttpMethod.Get, s.Resource + "/lookup?term=" + Uri.EscapeDataString("imdb:" + title.ImdbId), null, ct).ConfigureAwait(false);
            var matches = results.AsArray().OfType<JsonObject>().Where(x => string.Equals(x["imdbId"]?.GetValue<string>(), title.ImdbId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1 || (matches[0][s.Identity]?.GetValue<int>() ?? 0) <= 0)
            {
                throw new InvalidOperationException($"{s.Name} konnte diesen Titel nicht eindeutig zuordnen.");
            }

            var candidate = (JsonObject)matches[0].DeepClone();
            candidate.Remove("id");
            candidate.Remove("path");
            candidate["qualityProfileId"] = s.Profile;
            candidate["rootFolderPath"] = s.Root;
            candidate["monitored"] = true;
            if (title.IsSeries)
            {
                candidate["seasonFolder"] = true;
                foreach (var season in candidate["seasons"]?.AsArray().OfType<JsonObject>() ?? [])
                {
                    season["monitored"] = (season["seasonNumber"]?.GetValue<int>() ?? 0) > 0;
                }

                candidate["addOptions"] = new JsonObject { ["monitor"] = "all", ["searchForMissingEpisodes"] = true };
            }
            else
            {
                candidate["minimumAvailability"] = "released";
                candidate["addOptions"] = new JsonObject { ["searchForMovie"] = true };
            }

            var created = await SendAsync(s, HttpMethod.Post, s.Resource, candidate, ct).ConfigureAwait(false);
            return Status(s, created.AsObject());
        }
        finally { _requests.Release(); }
    }

    private async Task SearchAsync(Settings s, int id, bool series, CancellationToken ct)
    {
        var command = new JsonObject { ["name"] = series ? "SeriesSearch" : "MoviesSearch" };
        if (series) { command["seriesId"] = id; }
        else { command["movieIds"] = new JsonArray(JsonValue.Create(id)); }
        await SendAsync(s, HttpMethod.Post, "command", command, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Registers a completed direct download already in the final library, then rescans only
    /// that title. Existing monitoring, profiles and paths are preserved; no search is started.
    /// Returns false while metadata/configuration is missing so the caller can retry.
    /// </summary>
    public async Task<bool> SyncCompletedAsync(bool series, string storage, ArrTitle? title, CancellationToken ct)
    {
        var s = GetSettings(series);
        if (!Configured(s) || !Path.IsPathFullyQualified(storage) || !Path.IsPathFullyQualified(s.Root)) { return false; }
        var relative = Path.GetRelativePath(s.Root, storage);
        if (relative == "." || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative)) { return false; }
        var folder = Path.Combine(s.Root, relative.Split(Path.DirectorySeparatorChar)[0]);
        await _requests.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = (await SendAsync(s, HttpMethod.Get, s.Resource, null, ct).ConfigureAwait(false)).AsArray().OfType<JsonObject>().ToList();
            // The existing folder identity wins over potentially incorrect Jellyfin metadata.
            var existing = all.FirstOrDefault(x => SamePath(x["path"]?.ToString(), folder));
            if (existing is null)
            {
                if (title is null || title.IsSeries != series || !System.Text.RegularExpressions.Regex.IsMatch(title.ImdbId, "^tt[0-9]+$")) { return false; }
                // Do not relocate an existing title or silently create a second copy.
                if (all.Any(x => string.Equals(x["imdbId"]?.ToString(), title.ImdbId, StringComparison.OrdinalIgnoreCase))) { return false; }
                var lookup = await SendAsync(s, HttpMethod.Get, s.Resource + "/lookup?term=" + Uri.EscapeDataString("imdb:" + title.ImdbId), null, ct).ConfigureAwait(false);
                var matches = lookup.AsArray().OfType<JsonObject>().Where(x => x["imdbId"]?.ToString() == title.ImdbId).ToList();
                if (matches.Count != 1 || (matches[0][s.Identity]?.GetValue<int>() ?? 0) <= 0) { return false; }
                var candidate = (JsonObject)matches[0].DeepClone();
                candidate.Remove("id");
                candidate["path"] = folder;
                candidate["rootFolderPath"] = s.Root;
                candidate["qualityProfileId"] = s.Profile;
                candidate["monitored"] = series;
                if (series)
                {
                    candidate["seasonFolder"] = true;
                    candidate["monitorNewItems"] = "all";
                    candidate["addOptions"] = new JsonObject { ["monitor"] = "future", ["searchForMissingEpisodes"] = false, ["searchForCutoffUnmetEpisodes"] = false };
                }
                else
                {
                    candidate["minimumAvailability"] = "released";
                    candidate["addOptions"] = new JsonObject { ["searchForMovie"] = false };
                }

                existing = (await SendAsync(s, HttpMethod.Post, s.Resource, candidate, ct).ConfigureAwait(false)).AsObject();
            }

            var command = new JsonObject { ["name"] = series ? "RescanSeries" : "RescanMovie", [series ? "seriesId" : "movieId"] = existing["id"]!.GetValue<int>() };
            if (!series && existing["hasFile"]?.GetValue<bool>() != true)
            {
                // Scene-obfuscated filenames can remain unassigned after an ordinary disk scan.
                // Only accept one unambiguous, approved file already in this movie's own folder.
                var rows = (await SendAsync(s, HttpMethod.Get, "manualimport?movieId=" + existing["id"]!.GetValue<int>() + "&filterExistingFiles=true", null, ct).ConfigureAwait(false)).AsArray();
                var approved = rows.OfType<JsonObject>().Where(r => r["movie"]?["id"]?.GetValue<int>() == existing["id"]!.GetValue<int>()
                    && (r["rejections"]?.AsArray().Count ?? 0) == 0 && SamePath(Path.GetDirectoryName(r["path"]?.ToString()), folder)).ToList();
                if (approved.Count == 1)
                {
                    var file = (JsonObject)approved[0].DeepClone();
                    file["movieId"] = existing["id"]!.GetValue<int>();
                    command = new JsonObject { ["name"] = "ManualImport", ["importMode"] = "copy", ["files"] = new JsonArray(file) };
                }
            }

            await SendAsync(s, HttpMethod.Post, "command", command, ct).ConfigureAwait(false);
            return true;
        }
        finally { _requests.Release(); }
    }

    private static bool SamePath(string? left, string right)
        => !string.IsNullOrWhiteSpace(left) && Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar).Equals(
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
