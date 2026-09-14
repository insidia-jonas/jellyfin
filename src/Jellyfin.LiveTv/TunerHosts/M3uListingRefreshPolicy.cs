using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.LiveTv.TunerHosts;

/// <summary>
/// Conservative M3U listing refresh rules: playlist GET only, never ingest streams.
/// </summary>
internal static class M3uListingRefreshPolicy
{
    /// <summary>
    /// How long a successful playlist snapshot stays fresh before a background conditional GET.
    /// </summary>
    public static readonly TimeSpan ListingRefreshInterval = TimeSpan.FromMinutes(45);

    /// <summary>
    /// Whether the in-memory/disk snapshot should be refreshed from the playlist URL.
    /// </summary>
    /// <param name="lastSuccessUtc">When the snapshot was last confirmed (200 or 304).</param>
    /// <param name="utcNow">The current UTC time.</param>
    /// <param name="cachedPlaylistUrl">The URL the snapshot was fetched from.</param>
    /// <param name="currentPlaylistUrl">The tuner's current listing URL.</param>
    /// <returns><c>true</c> when a playlist GET should run.</returns>
    public static bool ShouldRefreshListing(
        DateTime? lastSuccessUtc,
        DateTime utcNow,
        string? cachedPlaylistUrl,
        string? currentPlaylistUrl)
    {
        if (string.IsNullOrWhiteSpace(currentPlaylistUrl))
        {
            return false;
        }

        if (lastSuccessUtc is null
            || string.IsNullOrWhiteSpace(cachedPlaylistUrl))
        {
            return true;
        }

        if (!string.Equals(cachedPlaylistUrl, currentPlaylistUrl, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return utcNow - lastSuccessUtc.Value >= ListingRefreshInterval;
    }

    /// <summary>
    /// Whether a playlist GET should send <c>If-None-Match</c> / <c>If-Modified-Since</c>.
    /// </summary>
    /// <param name="etag">The last ETag, if any.</param>
    /// <param name="lastModified">The last Last-Modified, if any.</param>
    /// <returns><c>true</c> when validators are available.</returns>
    public static bool ShouldSendConditionalGet(string? etag, DateTimeOffset? lastModified)
        => !string.IsNullOrWhiteSpace(etag) || lastModified.HasValue;

    /// <summary>
    /// Stable identity of a channel lineup (ids only — not now/next text).
    /// </summary>
    /// <param name="channelIds">Tuner channel ids.</param>
    /// <returns>A hex hash, or empty when there are no ids.</returns>
    public static string ChannelSetIdentity(IEnumerable<string?> channelIds)
    {
        ArgumentNullException.ThrowIfNull(channelIds);

        var normalized = channelIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', normalized));
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
