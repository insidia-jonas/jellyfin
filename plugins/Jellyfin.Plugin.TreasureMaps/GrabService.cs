using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TreasureMaps;

/// <summary>
/// Shared grab pipeline: downloads a release's NZB from the indexer and queues it in SABnzbd
/// with the category matching the media type. Used by the favorite handler, the API controller
/// and the channel's play-to-download callback; de-duplicates repeated grabs per session.
/// </summary>
public class GrabService
{
    private readonly TreasureMapsApiClient _client;
    private readonly SabnzbdClient _sabnzbd;
    private readonly ILogger<GrabService> _logger;
    private readonly ConcurrentDictionary<string, byte> _handled = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="GrabService"/> class.
    /// </summary>
    /// <param name="client">The Treasure-Maps API client.</param>
    /// <param name="sabnzbd">The SABnzbd client.</param>
    /// <param name="logger">The logger.</param>
    public GrabService(TreasureMapsApiClient client, SabnzbdClient sabnzbd, ILogger<GrabService> logger)
    {
        _client = client;
        _sabnzbd = sabnzbd;
        _logger = logger;
    }

    /// <summary>
    /// Grabs a release into SABnzbd. Repeated calls for the same guid in one session are ignored.
    /// </summary>
    /// <param name="guid">The release guid.</param>
    /// <param name="name">The SABnzbd job name (usually the scene/badge name).</param>
    /// <param name="isTv">Whether the release is a TV release (category selection).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The created SABnzbd job ids (empty when skipped as duplicate).</returns>
    public async Task<IReadOnlyList<string>> GrabAsync(string guid, string name, bool isTv, CancellationToken cancellationToken)
    {
        if (!_handled.TryAdd(guid, 0))
        {
            _logger.LogDebug("Grab for {Guid} skipped (already grabbed this session)", guid);
            return Array.Empty<string>();
        }

        try
        {
            if (!SabnzbdClient.IsConfigured)
            {
                throw new InvalidOperationException("SABnzbd is not configured.");
            }

            var payload = await _client.DownloadNzbAsync(guid, cancellationToken).ConfigureAwait(false);
            var config = Plugin.Instance!.Configuration;
            var preferred = isTv ? config.SabnzbdTvCategory : config.SabnzbdMovieCategory;
            var category = string.IsNullOrWhiteSpace(preferred) ? config.SabnzbdCategory : preferred;

            var ids = await _sabnzbd.AddNzbAsync(payload, Sanitize(name), category, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Grabbed '{Name}' into SABnzbd category '{Category}' ({Ids})", name, category, string.Join(",", ids));
            return ids;
        }
        catch
        {
            _handled.TryRemove(guid, out _);
            throw;
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
