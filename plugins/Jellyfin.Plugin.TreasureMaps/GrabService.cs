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

    // Artwork registry: remembers the cover of every grab (by SABnzbd job id and normalized job
    // name) so the Downloads folder can show poster tiles instead of plain text tiles.
    private readonly ConcurrentDictionary<string, string> _artworkByNzo = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _artworkByName = new(StringComparer.Ordinal);

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
    /// <param name="coverUrl">The title's cover URL (shown on the Downloads folder tile), may be null.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The created SABnzbd job ids (empty when skipped as duplicate).</returns>
    public async Task<IReadOnlyList<string>> GrabAsync(string guid, string name, bool isTv, string? coverUrl, CancellationToken cancellationToken)
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
            RegisterArtwork(ids, name, coverUrl);
            _logger.LogInformation("Grabbed '{Name}' into SABnzbd category '{Category}' ({Ids})", name, category, string.Join(",", ids));
            return ids;
        }
        catch
        {
            _handled.TryRemove(guid, out _);
            throw;
        }
    }

    /// <summary>
    /// Looks up the cover registered for a download (by SABnzbd job id, falling back to the
    /// normalized job name).
    /// </summary>
    /// <param name="nzoId">The SABnzbd job id.</param>
    /// <param name="name">The job name.</param>
    /// <returns>The cover URL, or null.</returns>
    public string? GetArtwork(string? nzoId, string? name)
    {
        if (!string.IsNullOrEmpty(nzoId) && _artworkByNzo.TryGetValue(nzoId, out var byId))
        {
            return byId;
        }

        var key = NormalizeName(name);
        return key.Length > 0 && _artworkByName.TryGetValue(key, out var byName) ? byName : null;
    }

    /// <summary>
    /// Registers the cover for a set of SABnzbd jobs (used by grab paths that queue directly).
    /// </summary>
    /// <param name="nzoIds">The SABnzbd job ids.</param>
    /// <param name="name">The job name.</param>
    /// <param name="coverUrl">The cover URL, may be null.</param>
    public void RegisterArtwork(IReadOnlyList<string> nzoIds, string name, string? coverUrl)
    {
        if (string.IsNullOrWhiteSpace(coverUrl))
        {
            return;
        }

        foreach (var id in nzoIds)
        {
            _artworkByNzo[id] = coverUrl!;
        }

        var key = NormalizeName(name);
        if (key.Length > 0)
        {
            _artworkByName[key] = coverUrl!;
        }
    }

    private static string NormalizeName(string? name)
        => new string((name ?? string.Empty).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static string Sanitize(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
