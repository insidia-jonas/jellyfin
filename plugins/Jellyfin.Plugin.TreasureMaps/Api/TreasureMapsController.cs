using System;
using System.IO;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
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
    /// Grabs a release: downloads its NZB and either pushes it to SABnzbd (preferred) or writes
    /// it into the configured drop folder.
    /// </summary>
    /// <param name="guid">The release GUID.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result of the grab (SABnzbd job ids or the written file path).</returns>
    [HttpPost("Releases/{guid}/Grab")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Grab([FromRoute] string guid, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
        if (!SabnzbdClient.IsConfigured && string.IsNullOrWhiteSpace(config.NzbDropFolder))
        {
            return BadRequest(new { ok = false, message = "Configure SABnzbd or an NZB drop folder first." });
        }

        try
        {
            var payload = await _client.DownloadNzbAsync(guid, cancellationToken).ConfigureAwait(false);
            var safeName = guid.Replace(Path.DirectorySeparatorChar, '_').Replace(Path.AltDirectorySeparatorChar, '_');

            if (SabnzbdClient.IsConfigured)
            {
                var nzoIds = await _sabnzbd.AddNzbAsync(payload, safeName, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Grabbed Treasure-Maps release {Guid} into SABnzbd ({Ids})", guid, string.Join(",", nzoIds));
                return Ok(new { ok = true, target = "sabnzbd", nzoIds, bytes = payload.Length });
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
}
