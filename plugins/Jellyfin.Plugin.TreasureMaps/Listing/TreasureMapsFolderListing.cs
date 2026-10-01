using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Channels;

namespace Jellyfin.Plugin.TreasureMaps.Listing;

/// <summary>Loads a folder completely before returning it to clients that cannot observe pending refreshes.</summary>
public static class TreasureMapsFolderListing
{
    /// <summary>Shares a cold fetch and returns fresh cards, or propagates the fetch failure.</summary>
    public static async Task<ChannelItemResult> LoadAsync(
        TreasureMapsListingCache cache,
        string key,
        TimeSpan ttl,
        Func<CancellationToken, Task<ChannelItemResult>> build,
        CancellationToken cancellationToken)
    {
        var result = await cache.GetOrFetchAsync(
            key,
            ttl,
            async (_, token) =>
            {
                var listing = await build(token).ConfigureAwait(false);
                if (listing is null || listing.RefreshPending || TreasureMapsListingCache.IsEmptyListing(listing))
                {
                    throw new InvalidOperationException("Treasure-Maps did not finish loading this folder.");
                }

                return ListingFetch<ChannelItemResult>.Store(listing, hash: TreasureMapsListingCache.HashPayload(listing));
            },
            cancellationToken).ConfigureAwait(false);

        return result ?? throw new InvalidOperationException("Treasure-Maps returned no folder contents.");
    }
}
