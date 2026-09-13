using System;
using System.Globalization;

namespace Jellyfin.Plugin.TreasureMaps.Listing;

/// <summary>
/// ChannelManager cache identity for Treasure-Maps. Includes the listing generation and a
/// browse-TTL epoch so a 3-hour disk hit cannot present expired indexer rows as current.
/// Download progress is overlaid locally and must not be folded into a 2-minute time bucket
/// (that rebuilt every folder on every open).
/// </summary>
public static class TreasureMapsChannelCacheKey
{
    /// <summary>
    /// Builds the <see cref="MediaBrowser.Controller.Channels.IHasCacheKey"/> value.
    /// </summary>
    /// <param name="userId">The user id, may be empty.</param>
    /// <param name="dataVersion">The channel <c>DataVersion</c>.</param>
    /// <param name="generation">The listing-cache invalidation generation.</param>
    /// <param name="utcNow">The current UTC time.</param>
    /// <returns>The cache key.</returns>
    public static string Build(string? userId, string dataVersion, int generation, DateTimeOffset utcNow)
    {
        var epoch = TreasureMapsListingCache.BrowseEpoch(utcNow);
        return (userId ?? string.Empty)
               + "-"
               + dataVersion
               + "-"
               + generation.ToString(CultureInfo.InvariantCulture)
               + "-"
               + epoch.ToString(CultureInfo.InvariantCulture);
    }
}
