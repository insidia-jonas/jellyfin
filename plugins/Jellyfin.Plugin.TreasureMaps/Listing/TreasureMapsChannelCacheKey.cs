using System;
using System.Globalization;

namespace Jellyfin.Plugin.TreasureMaps.Listing;

/// <summary>
/// ChannelManager cache identity for Treasure-Maps. Includes the listing generation.
/// Download progress is overlaid locally and must not be folded into a 2-minute time bucket
/// (that rebuilt every folder on every open).
/// Browse freshness is enforced by <see cref="TreasureMapsListingCache"/> on the Items path
/// (expired rows are not returned as current); this key stays stable so ChannelManager
/// does not rebuild every folder when a TTL epoch rolls.
/// </summary>
public static class TreasureMapsChannelCacheKey
{
    /// <summary>
    /// Builds the <see cref="MediaBrowser.Controller.Channels.IHasCacheKey"/> value.
    /// </summary>
    /// <param name="userId">The user id, may be empty.</param>
    /// <param name="dataVersion">The channel <c>DataVersion</c>.</param>
    /// <param name="generation">The listing-cache invalidation generation.</param>
    /// <param name="utcNow">The current UTC time (ignored; kept so existing call sites compile).</param>
    /// <returns>The cache key.</returns>
    public static string Build(string? userId, string dataVersion, int generation, DateTimeOffset utcNow)
    {
        _ = utcNow;
        return (userId ?? string.Empty)
               + "-"
               + dataVersion
               + "-"
               + generation.ToString(CultureInfo.InvariantCulture);
    }
}
