using System;
using System.Linq;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TreasureMaps.Configuration;
using Jellyfin.Plugin.TreasureMaps.Management;
using Jellyfin.Plugin.TreasureMaps.Subtitles;
using MediaBrowser.Common;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.TreasureMaps.Api;

/// <summary>Unified management uses Jellyfin's administrator authentication.</summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("TreasureMaps/Management")]
public sealed class ManagementController(ServiceManagement services, ILibraryManager library, IApplicationHost application, ILogger<ManagementController> logger, TreasureMapsApiClient indexers) : ControllerBase
{
    private static readonly SemaphoreSlim IndexerChanges = new(1, 1);

    [HttpGet("Indexers")]
    public IActionResult Indexers() => Ok(new
    {
        items = IndexerSource.Sources(Plugin.Instance!.Configuration, true).Select(s => new
        {
            id = s.Id, name = s.Name, protocol = s.Protocol, url = s.Url, enabled = s.Enabled,
            movieCategories = s.MovieCategories, tvCategories = s.TvCategories,
            keyPresent = !string.IsNullOrWhiteSpace(s.ApiKey)
        })
    });

    [HttpPost("Indexers")]
    public async Task<IActionResult> SaveIndexer([FromBody] IndexerSource source, CancellationToken ct)
    {
        await IndexerChanges.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = IndexerSource.Sources(Plugin.Instance!.Configuration, true);
            if (string.IsNullOrEmpty(source.Id)) { source.Id = Guid.NewGuid().ToString("N"); }
            var previous = existing.FirstOrDefault(s => s.Id == source.Id);
            if (previous == null && existing.Count >= 8) { return BadRequest(new { message = "Maximal acht Indexer gleichzeitig konfigurieren." }); }
            if (string.IsNullOrWhiteSpace(source.ApiKey)) { source.ApiKey = previous?.ApiKey ?? string.Empty; }
            source.Url = source.Url.Trim().TrimEnd('/');
            source.Name = source.Name.Trim();
            source.Validate();
            if (source.Enabled) { await indexers.TestSourceAsync(source, ct).ConfigureAwait(false); }
            // Read after the asynchronous test so another settings form cannot be overwritten.
            var c = JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(Plugin.Instance!.Configuration))!;
            c.Indexers = IndexerSource.Sources(c, true).Select(s => s.Id == source.Id ? source : s).ToList();
            if (!c.Indexers.Any(s => s.Id == source.Id)) { c.Indexers.Add(source); }
            if (source.Id == "legacy") { c.BaseUrl = source.Url; c.ApiKey = source.ApiKey; }
            Plugin.Instance.UpdateConfiguration(c);
            return Ok(new { ok = true, id = source.Id });
        }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        catch (Exception ex) when (ServiceError(ex) || ex is System.Xml.XmlException)
        { return StatusCode(502, new { message = "Indexer-Test fehlgeschlagen. Adresse, Protokoll und Schlüssel prüfen. Nichts wurde gespeichert." }); }
        finally { IndexerChanges.Release(); }
    }

    [HttpDelete("Indexers/{id}")]
    public async Task<IActionResult> DeleteIndexer(string id, CancellationToken ct)
    {
        await IndexerChanges.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var c = JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(Plugin.Instance!.Configuration))!;
            var existing = IndexerSource.Sources(c, true);
            if (!existing.Any(s => s.Id == id)) { return NotFound(); }
            c.Indexers = existing.Where(s => s.Id != id).ToList();
            if (id == "legacy" || c.Indexers.Count == 0) { c.BaseUrl = string.Empty; c.ApiKey = string.Empty; }
            Plugin.Instance.UpdateConfiguration(c);
            return Ok(new { ok = true });
        }
        finally { IndexerChanges.Release(); }
    }

    [HttpGet("Summary")]
    public async Task<IActionResult> Summary(CancellationToken ct)
    {
        var status = await Task.WhenAll(new[] { "treasuremaps", "radarr", "sonarr", "sabnzbd" }.Select(s => services.StatusAsync(s, ct))).ConfigureAwait(false);
        return Ok(new
        {
            version = typeof(Plugin).Assembly.GetName().Version?.ToString(),
            serverVersion = application.ApplicationVersionString,
            checkedAt = DateTimeOffset.UtcNow,
            services = status,
            libraries = library.GetVirtualFolders().Select(l => new { name = l.Name, kind = l.CollectionType?.ToString(), paths = l.Locations }).ToArray(),
            features = new[]
            {
                new { id = "targeted-search", name = "Gezielte Indexersuche", ready = TreasureMapsApiClient.IsConfigured, detail = "Titel · Jahr · IMDb · Staffel / Folge" },
                new { id = "fast-import", name = "Schnelle Bibliotheksimporte", ready = SabnzbdClient.IsConfigured, detail = "Abschlüsse alle 5 Sekunden · gezielte Dateierkennung" },
                new { id = "grok-subtitles", name = "KI-Untertitel", ready = AiSubtitleService.IsEnabled, detail = "Spracherkennung · Übersetzung · feste Zeitmarken" },
                new { id = "service-management", name = "Serviceverwaltung", ready = true, detail = "Vorhandenes erkennen · Verbindungen prüfen · gezielt ergänzen" }
            }
        });
    }

    [HttpGet("Queue/{service}")]
    public async Task<IActionResult> Queue(string service, CancellationToken ct)
    {
        if (service is not ("radarr" or "sonarr" or "sabnzbd")) { return BadRequest(); }
        try { return Ok(new { ok = true, items = await services.QueueAsync(service, ct).ConfigureAwait(false) }); }
        catch (Exception ex) when (ServiceError(ex)) { return StatusCode(502, new { message = "Warteschlange momentan nicht erreichbar." }); }
    }

    [HttpGet("Settings")]
    public IActionResult Settings()
    {
        var c = Plugin.Instance!.Configuration;
        return Ok(new
        {
            connections = new[]
            {
                new { service = "treasuremaps", url = c.BaseUrl, keyPresent = !string.IsNullOrWhiteSpace(c.ApiKey), profile = 0, root = string.Empty },
                new { service = "radarr", url = c.RadarrUrl, keyPresent = !string.IsNullOrWhiteSpace(c.RadarrApiKey), profile = c.RadarrQualityProfileId, root = c.RadarrRootFolder },
                new { service = "sonarr", url = c.SonarrUrl, keyPresent = !string.IsNullOrWhiteSpace(c.SonarrApiKey), profile = c.SonarrQualityProfileId, root = c.SonarrRootFolder },
                new { service = "sabnzbd", url = c.SabnzbdUrl, keyPresent = !string.IsNullOrWhiteSpace(c.SabnzbdApiKey), profile = 0, root = string.Empty }
            },
            callbackUrl = c.JellyfinCallbackUrl
        });
    }

    public sealed record ConnectionSettings(string Service, string Url, string? ApiKey = null, int? Profile = null, string? Root = null);

    [HttpPost("Settings")]
    public async Task<IActionResult> SaveSettings([FromBody] ConnectionSettings settings, CancellationToken ct)
    {
        try
        {
            var url = ServiceManagement.ValidateUrl(settings.Url);
            if (settings.Profile is < 1) { return BadRequest(new { message = "Ein gültiges Qualitätsprofil auswählen." }); }
            // Validate a copy; a rejected request must not partially change the live settings.
            var c = JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(Plugin.Instance!.Configuration))!;
            switch (settings.Service)
            {
                case "radarr":
                    c.RadarrUrl = url;
                    if (!string.IsNullOrWhiteSpace(settings.ApiKey)) { c.RadarrApiKey = settings.ApiKey.Trim(); }
                    if (settings.Profile.HasValue) { c.RadarrQualityProfileId = settings.Profile.Value; }
                    if (settings.Root is not null) { c.RadarrRootFolder = settings.Root; }
                    break;
                case "sonarr":
                    c.SonarrUrl = url;
                    if (!string.IsNullOrWhiteSpace(settings.ApiKey)) { c.SonarrApiKey = settings.ApiKey.Trim(); }
                    if (settings.Profile.HasValue) { c.SonarrQualityProfileId = settings.Profile.Value; }
                    if (settings.Root is not null) { c.SonarrRootFolder = settings.Root; }
                    break;
                case "sabnzbd":
                    c.SabnzbdUrl = url;
                    if (!string.IsNullOrWhiteSpace(settings.ApiKey)) { c.SabnzbdApiKey = settings.ApiKey.Trim(); }
                    break;
                case "treasuremaps":
                    c.BaseUrl = url;
                    if (!string.IsNullOrWhiteSpace(settings.ApiKey)) { c.ApiKey = settings.ApiKey.Trim(); }
                    break;
                case "jellyfin": c.JellyfinCallbackUrl = url; break;
                default: return BadRequest();
            }

            await services.ValidateSettingsAsync(settings.Service, c, ct).ConfigureAwait(false);
            Plugin.Instance.UpdateConfiguration(c);
            return Ok(new { ok = true });
        }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        catch (Exception ex) when (ServiceError(ex)) { return StatusCode(502, new { message = "Verbindungstest fehlgeschlagen. Einstellungen wurden nicht gespeichert." }); }
    }

    [HttpGet("Options/{service}")]
    public async Task<IActionResult> Options(string service, CancellationToken ct)
    {
        if (service is not ("radarr" or "sonarr")) { return BadRequest(); }
        try { return Ok(await services.OptionsAsync(service, ct).ConfigureAwait(false)); }
        catch (Exception ex) when (ServiceError(ex)) { return StatusCode(502, new { message = "Profile und Zielordner konnten nicht geladen werden." }); }
    }

    [HttpGet("Connections")]
    public async Task<IActionResult> Connections(CancellationToken ct)
        => Ok(new { links = await ConnectionsWithLibrariesAsync(false, ct).ConfigureAwait(false) });

    [HttpPost("Connections/Apply")]
    public async Task<IActionResult> ApplyConnections(CancellationToken ct)
        => Ok(new { links = await ConnectionsWithLibrariesAsync(true, ct).ConfigureAwait(false) });

    private async Task<System.Collections.Generic.List<ServiceManagement.Connection>> ConnectionsWithLibrariesAsync(bool apply, CancellationToken ct)
    {
        var links = (await services.ConnectionsAsync(apply, ct).ConfigureAwait(false)).ToList();
        var c = Plugin.Instance!.Configuration;
        foreach (var (service, name, type, root) in new[] { ("Radarr", "Filme", CollectionTypeOptions.movies, c.RadarrRootFolder), ("Sonarr", "Serien", CollectionTypeOptions.tvshows, c.SonarrRootFolder) })
        {
            var exists = !string.IsNullOrWhiteSpace(root) && Directory.Exists(root);
            var attached = exists && library.GetVirtualFolders().Any(v => v.CollectionType == type && v.Locations.Any(p => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar) == Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)));
            var changed = false;
            if (apply && exists && !attached)
            {
                var result = await LibrarySetup.EnsureAsync(library, name, type, root, logger).ConfigureAwait(false);
                changed = result is "created" or "path added";
                attached = changed || result == "already configured";
            }

            links.Add(new ServiceManagement.Connection(service.ToLowerInvariant() + "-library", service, "Jellyfin-Bibliothek", attached ? "ready" : exists ? "missing" : "error",
                attached ? "Importordner ist in der Bibliothek eingebunden." : exists ? "Importordner wird zur vorhandenen Bibliothek ergänzt." : "Zielordner fehlt oder ist für Jellyfin nicht erreichbar.", changed));
        }

        return links;
    }

    [HttpPost("Actions/{service}/{action}")]
    public async Task<IActionResult> Action(string service, string action, CancellationToken ct)
    {
        try
        {
            if (service == "jellyfin" && action == "scan") { library.QueueLibraryScan(); }
            else { await services.ActionAsync(service, action, ct).ConfigureAwait(false); }
            return Ok(new { ok = true });
        }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        catch (Exception ex) when (ServiceError(ex)) { return StatusCode(502, new { message = "Aktion fehlgeschlagen. Verbindung zum Dienst prüfen." }); }
    }

    private static bool ServiceError(Exception ex)
        => ex is HttpRequestException or InvalidOperationException or OperationCanceledException or JsonException;
}
