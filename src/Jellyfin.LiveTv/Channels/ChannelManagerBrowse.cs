using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.LiveTv.Channels;

/// <summary>
/// How ChannelManager should finish an Items request for a channel folder.
/// </summary>
public enum ChannelFolderPaint
{
    /// <summary>
    /// Provider is still fetching. Return a completed empty list and do not
    /// present or delete existing library rows (Treasure-Maps indexer pending).
    /// </summary>
    PendingEmpty,

    /// <summary>
    /// Reuse stored folder children. Do not rewrite on this request.
    /// </summary>
    ReuseExisting,

    /// <summary>
    /// Live TV snapshot has children the library does not. Paint those items
    /// immediately; persist them in the background.
    /// </summary>
    PaintIncoming,

    /// <summary>
    /// Create/update/delete library rows on this request (normal channels).
    /// </summary>
    RewriteNow
}

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
    /// Live TV library channel (IPTV groups), not Treasure-Maps or other overlays.
    /// </summary>
    /// <param name="channel">The channel provider.</param>
    /// <returns>True when this is the Live TV My Media channel.</returns>
    public static bool IsLiveTvOverlay(IChannel? channel)
        => channel is LiveTvLibraryChannel;

    /// <summary>
    /// True when the provider is still fetching and the Items response must not
    /// present existing library rows as the current listing.
    /// </summary>
    /// <param name="result">The provider result.</param>
    /// <returns>True when library sync and disk cache must be skipped.</returns>
    public static bool IsRefreshPending(ChannelItemResult? result)
        => result is { RefreshPending: true };

    /// <summary>
    /// Treasure-Maps pending-empty must not apply to Live TV group folders.
    /// Live TV categories are snapshot filters, not indexer queries.
    /// </summary>
    /// <param name="channel">The channel provider.</param>
    /// <param name="result">The provider result.</param>
    /// <returns>True when Items should complete empty and hide stored rows.</returns>
    public static bool HideExistingOnPending(IChannel? channel, ChannelItemResult? result)
        => IsRefreshPending(result) && !IsLiveTvOverlay(channel);

    /// <summary>
    /// True when the provider returned current children to display.
    /// </summary>
    /// <param name="result">The provider result.</param>
    /// <returns>True when incoming items should be shown.</returns>
    public static bool HasIncomingItems(ChannelItemResult? result)
        => result is { RefreshPending: false, Items.Count: > 0 };

    /// <summary>
    /// True when <paramref name="folderId"/> is a Live TV group-title folder.
    /// </summary>
    /// <param name="folderId">The channel folder id.</param>
    /// <returns>True for <c>g:</c> group folders.</returns>
    public static bool IsLiveTvGroupFolderId(string? folderId)
        => !string.IsNullOrEmpty(folderId)
           && folderId.StartsWith(LiveTvLibraryChannelItems.GroupPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Finds the Live TV group folder whose deterministic library id matches
    /// <paramref name="parentId"/>. Painted group tiles can be opened before
    /// ChannelManager persists the folder entity.
    /// </summary>
    /// <param name="parentId">The Items ParentId from the client.</param>
    /// <param name="rootItems">Root snapshot items (groups).</param>
    /// <param name="toLibraryId">Maps a channel external id to its library Guid.</param>
    /// <returns>The matching group, or <c>null</c>.</returns>
    public static ChannelItemInfo? FindLiveTvGroupByLibraryId(
        Guid parentId,
        IReadOnlyList<ChannelItemInfo>? rootItems,
        Func<string, Guid> toLibraryId)
    {
        if (rootItems is null || toLibraryId is null || parentId.Equals(Guid.Empty))
        {
            return null;
        }

        foreach (var item in rootItems)
        {
            if (item is null
                || item.Type != ChannelItemType.Folder
                || string.IsNullOrEmpty(item.Id)
                || !IsLiveTvGroupFolderId(item.Id))
            {
                continue;
            }

            if (toLibraryId(item.Id).Equals(parentId))
            {
                return item;
            }
        }

        return null;
    }

    /// <summary>
    /// Decides whether Items may reuse rows, paint the Live TV snapshot, hide
    /// pending Treasure-Maps rows, or rewrite the folder on the request thread.
    /// </summary>
    /// <param name="channel">The channel provider.</param>
    /// <param name="result">The provider result.</param>
    /// <param name="hasExisting">True when the folder already has library rows.</param>
    /// <param name="existingMatch">True when stored ids match the provider.</param>
    /// <returns>The paint action for this open.</returns>
    public static ChannelFolderPaint DecidePaint(
        IChannel? channel,
        ChannelItemResult? result,
        bool hasExisting,
        bool existingMatch)
    {
        if (HideExistingOnPending(channel, result))
        {
            return ChannelFolderPaint.PendingEmpty;
        }

        if (IsLiveTvOverlay(channel))
        {
            if (existingMatch)
            {
                return ChannelFolderPaint.ReuseExisting;
            }

            if (HasIncomingItems(result) && !hasExisting)
            {
                return ChannelFolderPaint.PaintIncoming;
            }

            // Empty snapshot: keep last-good library rows (or complete empty).
            // Partial mismatch: keep stored rows; persist the snapshot later.
            return ChannelFolderPaint.ReuseExisting;
        }

        if (channel is IChannelPresentationOverlay && existingMatch)
        {
            return ChannelFolderPaint.ReuseExisting;
        }

        return ChannelFolderPaint.RewriteNow;
    }

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
