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
    private readonly ILogger<TreasureMapsController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TreasureMapsController"/> class.
    /// </summary>
    /// <param name="client">The Treasure-Maps API client.</param>
    /// <param name="logger">The logger.</param>
    public TreasureMapsController(TreasureMapsApiClient client, ILogger<TreasureMapsController> logger)
    {
        _client = client;
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
    /// Grabs a release by downloading its NZB into the configured drop folder.
    /// </summary>
    /// <param name="guid">The release GUID.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The path of the written NZB file.</returns>
    [HttpPost("Releases/{guid}/Grab")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Grab([FromRoute] string guid, CancellationToken cancellationToken)
    {
        var dropFolder = Plugin.Instance?.Configuration.NzbDropFolder ?? string.Empty;
        if (string.IsNullOrWhiteSpace(dropFolder))
        {
            return BadRequest(new { ok = false, message = "No NZB drop folder configured." });
        }

        try
        {
            Directory.CreateDirectory(dropFolder);
            var payload = await _client.DownloadNzbAsync(guid, cancellationToken).ConfigureAwait(false);
            var safeName = guid.Replace(Path.DirectorySeparatorChar, '_').Replace(Path.AltDirectorySeparatorChar, '_');
            var path = Path.Combine(dropFolder, safeName + ".nzb");
            await System.IO.File.WriteAllBytesAsync(path, payload, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Grabbed Treasure-Maps release {Guid} to {Path}", guid, path);
            return Ok(new { ok = true, path, bytes = payload.Length });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to grab Treasure-Maps release {Guid}", guid);
            return Ok(new { ok = false, message = ex.Message });
        }
    }
}
