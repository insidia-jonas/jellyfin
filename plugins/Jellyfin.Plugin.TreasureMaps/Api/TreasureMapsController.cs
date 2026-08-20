using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TreasureMaps.ReleaseNaming;
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
    private readonly ILogger<TreasureMapsController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TreasureMapsController"/> class.
    /// </summary>
    /// <param name="client">The Treasure-Maps API client.</param>
    /// <param name="sabnzbd">The SABnzbd client.</param>
    /// <param name="logger">The logger.</param>
    public TreasureMapsController(TreasureMapsApiClient client, SabnzbdClient sabnzbd, ILogger<TreasureMapsController> logger)
    {
        _client = client;
        _sabnzbd = sabnzbd;
        _logger = logger;
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
            return Ok(new { ok = true, username = user?.Username, role = user?.Role });
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
    /// Searches Treasure-Maps for the Browse &amp; Grab page.
    /// </summary>
    /// <param name="type">The media type to search: <c>movie</c> or <c>tv</c>.</param>
    /// <param name="q">The free-text query (optional).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A lightweight list of releases for the UI.</returns>
    [HttpGet("Search")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] string type, [FromQuery] string? q, CancellationToken cancellationToken)
    {
        if (!TreasureMapsApiClient.IsConfigured)
        {
            return Ok(new { ok = false, message = "Configure the Treasure-Maps connection first.", items = Array.Empty<object>() });
        }

        var isTv = string.Equals(type, "tv", StringComparison.OrdinalIgnoreCase);
        var limit = Plugin.Instance?.Configuration.ResultLimit ?? 60;
        try
        {
            var response = isTv
                ? await _client.SearchTvAsync(q, limit, cancellationToken).ConfigureAwait(false)
                : await _client.SearchMoviesAsync(q, null, limit, cancellationToken).ConfigureAwait(false);

            var items = (response?.Items ?? Enumerable.Empty<Release>())
                .Where(r => !string.IsNullOrWhiteSpace(r.Guid))
                .Select(r =>
                {
                    var parsed = ReleaseNameParser.Parse(r.Title);
                    var title = r.Movie?.Title ?? r.Tv?.Title ?? r.Title;
                    return new
                    {
                        guid = r.Guid,
                        title,
                        scene = r.Title,
                        year = r.Movie?.Year ?? r.Tv?.FirstAired,
                        poster = r.Images?.Cover,
                        type = isTv ? "tv" : "movie",
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
