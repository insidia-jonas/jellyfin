using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.LiveTv.Channels;

/// <summary>
/// Browse-path rules so Live TV and overlay channels can paint without a
/// ChannelManager rewrite storm or a rotating disk-cache key.
/// </summary>
public static class ChannelManagerBrowse
{
    /// <summary>
    /// Overlay channels decide freshness themselves. The 3-hour ChannelManager
    /// disk file must not be treated as current (that hid expired Treasure-Maps
    /// rows, and the TTL-epoch workaround rebuilt every folder on every open).
    /// </summary>
    /// <param name="channel">The channel provider.</param>
    /// <returns>True when the provider must be asked on every Items call.</returns>
    public static bool BypassDiskCache(IChannel? channel)
        => channel is IChannelPresentationOverlay;

    /// <summary>
    /// True when the provider is still fetching and the Items response must not
    /// present existing library rows as the current listing.
    /// </summary>
    /// <param name="result">The provider result.</param>
    /// <returns>True when library sync and disk cache must be skipped.</returns>
    public static bool IsRefreshPending(ChannelItemResult? result)
        => result is { RefreshPending: true };

    /// <summary>
    /// True when every incoming channel id already exists under the folder, so
    /// names/posters/people must not be rewritten on this open.
    /// </summary>
    /// <param name="existingExternalIds">External ids already stored for the folder.</param>
    /// <param name="incoming">Provider items for this open.</param>
    /// <returns>True when the folder can be reused as-is.</returns>
    public static bool ExistingItemsMatch(
        IEnumerable<string?>? existingExternalIds,
        IReadOnlyList<ChannelItemInfo>? incoming)
    {
        if (incoming is null || incoming.Count == 0)
        {
            return false;
        }

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (existingExternalIds is not null)
        {
            foreach (var id in existingExternalIds)
            {
                if (!string.IsNullOrEmpty(id))
                {
                    existing.Add(id);
                }
            }
        }

        if (existing.Count != incoming.Count)
        {
            return false;
        }

        foreach (var item in incoming)
        {
            if (string.IsNullOrEmpty(item?.Id) || !existing.Contains(item.Id))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Collects external ids from stored folder children.
    /// </summary>
    /// <param name="items">Library items.</param>
    /// <returns>External ids.</returns>
    public static IEnumerable<string?> ExternalIds(IEnumerable<BaseItem>? items)
    {
        if (items is null)
        {
            yield break;
        }

        foreach (var item in items)
        {
            yield return item?.ExternalId;
        }
    }
}
