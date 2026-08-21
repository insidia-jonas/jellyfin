using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TreasureMaps.ReleaseNaming;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TreasureMaps.Api;

/// <summary>
/// API controller for the Treasure-Maps plugin (connection test and NZB grab).
/// </summary>
[ApiController]
[Authorize]
[Route("TreasureMaps")]
[Produces(MediaTypeNames.Application.Json)]
[ApiExplorerSettings(IgnoreApi = true)]
public class TreasureMapsController : ControllerBase
{
    private readonly TreasureMapsApiClient _client;
    private readonly SabnzbdClient _sabnzbd;
    private readonly Subtitles.OpenSubtitlesClient _openSubtitles;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<TreasureMapsController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TreasureMapsController"/> class.
    /// </summary>
    /// <param name="client">The Treasure-Maps API client.</param>
    /// <param name="sabnzbd">The SABnzbd client.</param>
    /// <param name="openSubtitles">The OpenSubtitles client.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="logger">The logger.</param>
    public TreasureMapsController(TreasureMapsApiClient client, SabnzbdClient sabnzbd, Subtitles.OpenSubtitlesClient openSubtitles, ILibraryManager libraryManager, ILogger<TreasureMapsController> logger)
    {
        _client = client;
        _sabnzbd = sabnzbd;
        _openSubtitles = openSubtitles;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Validates the OpenSubtitles configuration (API key + optional login).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The connection status.</returns>
    [HttpGet("OpenSubtitles/Test")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> TestOpenSubtitles(CancellationToken cancellationToken)
    {
        if (!Subtitles.OpenSubtitlesClient.IsEnabled)
        {
            return Ok(new { ok = false, message = "Enable OpenSubtitles and set an API key first." });
        }

        try
        {
            var token = await _openSubtitles.GetTokenAsync(cancellationToken).ConfigureAwait(false);
            var probe = await _openSubtitles.SearchAsync(
                new Dictionary<string, string?> { ["languages"] = "en", ["query"] = "matrix", ["type"] = "movie" },
                cancellationToken).ConfigureAwait(false);
            var count = probe?.Data?.Count ?? 0;
            return Ok(new { ok = true, loggedIn = !string.IsNullOrEmpty(token), sampleResults = count });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenSubtitles connection test failed");
            return Ok(new { ok = false, message = ex.Message });
        }
    }

    /// <summary>
    /// Validates the configured Base URL and API key by calling the provider's user endpoint.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The connection status.</returns>
    [HttpGet("Test")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Test(CancellationToken cancellationToken)
    {
        if (!TreasureMapsApiClient.IsConfigured)
        {
            return Ok(new { ok = false, message = "Base URL and API key must be configured first." });
        }

        try
        {
            var user = await _client.GetUserAsync(cancellationToken).ConfigureAwait(false);
            return Ok(new { ok = true, username = user?.Username, grabs = user?.Grabs });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Treasure-Maps connection test failed");
            return Ok(new { ok = false, message = ex.Message });
        }
    }

    /// <summary>
    /// Validates the configured SABnzbd connection.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The SABnzbd connection status.</returns>
    [HttpGet("Sabnzbd/Test")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> TestSabnzbd(CancellationToken cancellationToken)
    {
        if (!SabnzbdClient.IsConfigured)
        {
            return Ok(new { ok = false, message = "SABnzbd URL and API key must be configured first." });
        }

        try
        {
            var version = await _sabnzbd.GetVersionAsync(cancellationToken).ConfigureAwait(false);
            return Ok(new { ok = true, version });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SABnzbd connection test failed");
            return Ok(new { ok = false, message = ex.Message });
        }
    }

    /// <summary>
    /// Creates/updates the movie and TV categories (with their folders) in SABnzbd, so the plugin
    /// manages the SABnzbd configuration for the user.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result of the setup.</returns>
    [HttpPost("Sabnzbd/Setup")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> SetupSabnzbd(CancellationToken cancellationToken)
    {
        if (!SabnzbdClient.IsConfigured)
        {
            return Ok(new { ok = false, message = "Configure the SABnzbd URL and API key first." });
        }

        var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
        var applied = new List<object>();
        try
        {
            if (!string.IsNullOrWhiteSpace(config.SabnzbdMovieCategory))
            {
                await _sabnzbd.SetCategoryAsync(config.SabnzbdMovieCategory, config.SabnzbdMovieFolder, cancellationToken).ConfigureAwait(false);
                applied.Add(new { category = config.SabnzbdMovieCategory, dir = config.SabnzbdMovieFolder });
            }

            if (!string.IsNullOrWhiteSpace(config.SabnzbdTvCategory))
            {
                await _sabnzbd.SetCategoryAsync(config.SabnzbdTvCategory, config.SabnzbdTvFolder, cancellationToken).ConfigureAwait(false);
                applied.Add(new { category = config.SabnzbdTvCategory, dir = config.SabnzbdTvFolder });
            }

            var categories = await _sabnzbd.GetCategoryNamesAsync(cancellationToken).ConfigureAwait(false);
            return Ok(new { ok = true, applied, categories });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SABnzbd setup failed");
            return Ok(new { ok = false, message = ex.Message });
        }
    }

    /// <summary>
    /// Creates (or completes) the Jellyfin media libraries for the download folders, so grabbed
    /// movies and series show up in the top menu under "Movies" / "TV Shows" once SABnzbd has
    /// finished downloading them.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result of the setup.</returns>
    [HttpPost("Libraries/Setup")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> SetupLibraries(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

        // Category folders may be relative to SABnzbd's completed-downloads directory.
        var completeDir = SabnzbdClient.IsConfigured
            ? await _sabnzbd.GetCompleteDirAsync(cancellationToken).ConfigureAwait(false)
            : null;

        var moviesPath = ResolveDownloadFolder(config.SabnzbdMovieFolder, config.SabnzbdMovieCategory, completeDir);
        var tvPath = ResolveDownloadFolder(config.SabnzbdTvFolder, config.SabnzbdTvCategory, completeDir);
        if (moviesPath is null && tvPath is null)
        {
            return Ok(new { ok = false, message = "Configure the SABnzbd movie/TV folders (absolute paths) or connect SABnzbd first." });
        }

        var results = new List<object>();
        try
        {
            if (moviesPath is not null)
            {
                var status = await EnsureLibraryAsync("Movies", CollectionTypeOptions.movies, moviesPath).ConfigureAwait(false);
                results.Add(new { library = "Movies", path = moviesPath, status });
            }

            if (tvPath is not null)
            {
                var status = await EnsureLibraryAsync("TV Shows", CollectionTypeOptions.tvshows, tvPath).ConfigureAwait(false);
                results.Add(new { library = "TV Shows", path = tvPath, status });
            }

            return Ok(new { ok = true, libraries = results });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Library setup failed");
            return Ok(new { ok = false, message = ex.Message, libraries = results });
        }
    }

    private static string? ResolveDownloadFolder(string? folder, string? category, string? completeDir)
    {
        var value = !string.IsNullOrWhiteSpace(folder) ? folder : category;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Path.IsPathRooted(value))
        {
            return value;
        }

        return string.IsNullOrWhiteSpace(completeDir) ? null : Path.Combine(completeDir, value);
    }

    private async Task<string> EnsureLibraryAsync(string name, CollectionTypeOptions collectionType, string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create library folder {Path}", path);
        }

        var existing = _libraryManager.GetVirtualFolders()
            .FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            var options = new LibraryOptions
            {
                PathInfos = new[] { new MediaPathInfo(path) },
                EnableRealtimeMonitor = true
            };
            await _libraryManager.AddVirtualFolder(name, collectionType, options, true).ConfigureAwait(false);
            _logger.LogInformation("Created Jellyfin library '{Name}' -> {Path}", name, path);
            return "created";
        }

        if (existing.Locations?.Any(l => string.Equals(l, path, StringComparison.OrdinalIgnoreCase)) == true)
        {
            return "already configured";
        }

        _libraryManager.AddMediaPath(name, new MediaPathInfo(path));
        _logger.LogInformation("Added {Path} to existing Jellyfin library '{Name}'", path, name);
        return "path added";
    }

    /// <summary>
    /// Searches Treasure-Maps for the Browse &amp; Grab page.
    /// </summary>
    /// <param name="type">The media type to search: <c>movie</c> or <c>tv</c>.</param>
    /// <param name="q">The free-text query (optional).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A lightweight list of releases for the UI.</returns>
    [HttpGet("Search")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] string type, [FromQuery] string? q, [FromQuery] string? genre, CancellationToken cancellationToken)
    {
        if (!TreasureMapsApiClient.IsConfigured)
        {
            return Ok(new { ok = false, message = "Configure the Treasure-Maps connection first.", items = Array.Empty<object>() });
        }

        var kind = (type ?? "movie").ToLowerInvariant();
        var limit = Plugin.Instance?.Configuration.ResultLimit ?? 60;
        try
        {
            var response = kind switch
            {
                "trending" => await _client.GetTrendingAsync(limit, cancellationToken).ConfigureAwait(false),
                "tv" => await _client.SearchTvAsync(q, limit, cancellationToken).ConfigureAwait(false),
                _ => await _client.SearchMoviesAsync(q, genre, limit, cancellationToken).ConfigureAwait(false)
            };

            var items = (response?.Items ?? Enumerable.Empty<Release>())
                .Where(r => !string.IsNullOrWhiteSpace(r.Guid))
                .Select(r =>
                {
                    var parsed = ReleaseNameParser.Parse(r.Title);
                    var isTvItem = kind switch
                    {
                        "tv" => true,
                        "movie" => false,
                        _ => r.Tv is not null && r.Movie is null
                            || (r.Movie is null && (r.Category?.Name?.Contains("TV", StringComparison.OrdinalIgnoreCase) ?? false))
                    };
                    var title = r.Movie?.Title ?? r.Tv?.Title ?? r.Title;
                    var ratingStr = r.Movie?.Rating;
                    double? rating = double.TryParse(ratingStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var rv) ? rv : null;
                    var year = r.Movie?.Year ?? r.Tv?.FirstAired;
                    if (year is { Length: > 4 })
                    {
                        year = year[..4];
                    }

                    return new
                    {
                        guid = r.Guid,
                        title,
                        scene = r.Title,
                        year,
                        rating,
                        poster = r.Images?.Cover,
                        type = isTvItem ? "tv" : "movie",
                        genres = r.Movie?.Genres ?? new System.Collections.Generic.List<string>(),
                        quality = string.Join(" · ", parsed.DisplayTags)
                    };
                })
                .ToList();

            return Ok(new { ok = true, items });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Treasure-Maps search failed");
            return Ok(new { ok = false, message = ex.Message, items = Array.Empty<object>() });
        }
    }

    /// <summary>
    /// Returns the indexer's genres (for the Browse page genre selector).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The list of genre names.</returns>
    [HttpGet("Genres")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Genres(CancellationToken cancellationToken)
    {
        if (!TreasureMapsApiClient.IsConfigured)
        {
            return Ok(new { ok = false, genres = Array.Empty<string>() });
        }

        try
        {
            // The indexer exposes thousands of niche genres (incl. adult tags); only the curated
            // common-genre whitelist is surfaced.
            var caps = await _client.GetCapsAsync(cancellationToken).ConfigureAwait(false);
            var genres = Channels.CommonGenres.FilterAvailable(
                (caps?.Genres ?? Enumerable.Empty<Api.CapsNamedItem>()).Select(g => g.Name));

            return Ok(new { ok = true, genres });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Treasure-Maps caps failed");
            return Ok(new { ok = false, genres = Array.Empty<string>() });
        }
    }

    /// <summary>
    /// Grabs a release: downloads its NZB and pushes it to SABnzbd with the category that matches the
    /// media type (so movies and series land in their own folders), or writes it to the drop folder.
    /// </summary>
    /// <param name="guid">The release GUID.</param>
    /// <param name="type">The media type (<c>movie</c> or <c>tv</c>); auto-detected from the name when omitted.</param>
    /// <param name="name">An optional human-readable name for the SABnzbd job.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result of the grab (SABnzbd job ids or the written file path).</returns>
    [HttpPost("Releases/{guid}/Grab")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Grab([FromRoute] string guid, [FromQuery] string? type, [FromQuery] string? name, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
        if (!SabnzbdClient.IsConfigured && string.IsNullOrWhiteSpace(config.NzbDropFolder))
        {
            return BadRequest(new { ok = false, message = "Configure SABnzbd or an NZB drop folder first." });
        }

        var isTv = ResolveIsTv(type, name);
        var category = PickCategory(config, isTv);

        try
        {
            var payload = await _client.DownloadNzbAsync(guid, cancellationToken).ConfigureAwait(false);
            var jobName = string.IsNullOrWhiteSpace(name) ? guid : name!;
            var safeName = Sanitize(jobName);

            if (SabnzbdClient.IsConfigured)
            {
                var nzoIds = await _sabnzbd.AddNzbAsync(payload, safeName, category, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Grabbed {Guid} into SABnzbd category '{Category}' ({Ids})", guid, category, string.Join(",", nzoIds));
                return Ok(new { ok = true, target = "sabnzbd", category, mediaType = isTv ? "tv" : "movie", nzoIds, bytes = payload.Length });
            }

            Directory.CreateDirectory(config.NzbDropFolder);
            var path = Path.Combine(config.NzbDropFolder, safeName + ".nzb");
            await System.IO.File.WriteAllBytesAsync(path, payload, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Grabbed Treasure-Maps release {Guid} to {Path}", guid, path);
            return Ok(new { ok = true, target = "folder", path, bytes = payload.Length });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to grab Treasure-Maps release {Guid}", guid);
            return Ok(new { ok = false, message = ex.Message });
        }
    }

    private static bool ResolveIsTv(string? type, string? name)
    {
        if (string.Equals(type, "tv", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(type, "movie", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // No explicit type: infer from the scene name (SxxExx => series).
        return !string.IsNullOrWhiteSpace(name)
            && System.Text.RegularExpressions.Regex.IsMatch(name!, "S[0-9]{1,2}E[0-9]{1,3}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static string? PickCategory(Configuration.PluginConfiguration config, bool isTv)
    {
        var preferred = isTv ? config.SabnzbdTvCategory : config.SabnzbdMovieCategory;
        return !string.IsNullOrWhiteSpace(preferred) ? preferred : config.SabnzbdCategory;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
