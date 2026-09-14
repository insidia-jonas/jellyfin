using System;
using System.Collections.Generic;
using Jellyfin.Plugin.TreasureMaps.Channels;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.TreasureMaps.Listing;

/// <summary>
/// Overlays SABnzbd progress onto existing Downloads cards without busting the
/// ChannelManager listing identity (indexer rows stay freshness-gated separately).
/// </summary>
public static class DownloadOverlay
{
    /// <summary>
    /// Applies live download progress onto items whose external id is a Downloads card.
    /// </summary>
    /// <param name="items">The current folder items.</param>
    /// <param name="status">The last SABnzbd status snapshot.</param>
    /// <param name="speed">The queue speed, if any.</param>
    /// <param name="lookup">Looks up a grab record by nzo id / name.</param>
    public static void Apply(
        IReadOnlyList<BaseItem> items,
        IReadOnlyList<SabnzbdClient.SabDownloadStatus> status,
        string? speed,
        Func<string?, string?, GrabRecord?> lookup)
    {
        if (items is null || items.Count == 0 || status is null || status.Count == 0)
        {
            return;
        }

        foreach (var item in items)
        {
            if (item is null || !TryParseDownloadId(item.ExternalId, out var nzoId, out var titleFromId))
            {
                continue;
            }

            var entry = Find(status, nzoId, titleFromId);
            if (entry is null)
            {
                continue;
            }

            var rec = lookup(entry.Id, entry.Name);
            var title = DownloadTitle.Resolve(entry.Name, rec?.Title ?? titleFromId);
            var quality = rec?.Quality;
            if (string.IsNullOrWhiteSpace(quality) && DownloadTitle.LooksLikeQualityLabel(entry.Name))
            {
                quality = entry.Name;
            }

            var active = !string.Equals(entry.Status, "Completed", StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(entry.Status, "Failed", StringComparison.OrdinalIgnoreCase);
            item.Overview = FormatOverview(entry, speed, quality);
            item.ForcedSortName = ChannelPresentation.DownloadSortName(active, title);
        }
    }

    /// <summary>
    /// Parses a Downloads card external id (<c>DL::{nzo}::{title}</c>).
    /// </summary>
    /// <param name="externalId">The channel item external id.</param>
    /// <param name="nzoId">The SABnzbd job id.</param>
    /// <param name="title">The encoded title payload, may be empty.</param>
    /// <returns>True when the id is a Downloads card.</returns>
    public static bool TryParseDownloadId(string? externalId, out string nzoId, out string title)
    {
        nzoId = string.Empty;
        title = string.Empty;
        var id = externalId ?? string.Empty;
        if (id.StartsWith("c5-", StringComparison.Ordinal))
        {
            id = id[3..];
        }

        if (!id.StartsWith("DL::", StringComparison.Ordinal) && !id.StartsWith("dl::", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = id.Split("::", StringSplitOptions.None);
        nzoId = parts.Length > 1 ? parts[1] : string.Empty;
        title = parts.Length > 2 ? parts[2] : string.Empty;
        return nzoId.Length > 0 || title.Length > 0;
    }

    /// <summary>
    /// Builds the Downloads card overview (progress / completed / failed).
    /// </summary>
    /// <param name="entry">The SABnzbd row.</param>
    /// <param name="speed">The queue speed, if any.</param>
    /// <param name="quality">The quality badge, if any.</param>
    /// <returns>The overview text.</returns>
    public static string FormatOverview(SabnzbdClient.SabDownloadStatus entry, string? speed, string? quality)
    {
        var badge = string.IsNullOrWhiteSpace(quality) ? string.Empty : quality.Trim() + "\n\n";
        if (string.Equals(entry.Status, "Completed", StringComparison.OrdinalIgnoreCase))
        {
            return badge + "Download complete. Open Movies or TV Shows once the library scan finishes.";
        }

        if (string.Equals(entry.Status, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            return badge + "Download failed." + (string.IsNullOrWhiteSpace(entry.FailMessage) ? string.Empty : " " + entry.FailMessage);
        }

        return badge + $"Downloading \u2013 {entry.Percent:0}%"
            + (string.IsNullOrWhiteSpace(speed) ? string.Empty : $" \u00B7 {speed}B/s")
            + (string.IsNullOrWhiteSpace(entry.TimeLeft) ? string.Empty : $" \u00B7 {entry.TimeLeft} left")
            + (string.IsNullOrWhiteSpace(entry.LeftMb) ? string.Empty : $" \u00B7 {entry.LeftMb}/{entry.SizeMb} MB remaining");
    }

    private static SabnzbdClient.SabDownloadStatus? Find(
        IReadOnlyList<SabnzbdClient.SabDownloadStatus> status,
        string nzoId,
        string title)
    {
        foreach (var entry in status)
        {
            if (!string.IsNullOrEmpty(nzoId)
                && string.Equals(entry.Id, nzoId, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        if (title.Length == 0)
        {
            return null;
        }

        foreach (var entry in status)
        {
            if (string.Equals(entry.Name, title, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }
}
