using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Entities;

namespace Jellyfin.LiveTv.Channels;

/// <summary>
/// Builds the Live TV library channel folders and playable items.
/// </summary>
internal static class LiveTvLibraryChannelItems
{
    internal const string GroupPrefix = "g:";

    /// <summary>
    /// Creates root groups and live channel items for the Live TV library channel.
    /// </summary>
    /// <param name="channels">Tuner channels.</param>
    /// <param name="folderId">The folder being browsed, or <c>null</c> for the root.</param>
    /// <param name="guide">Now/next EPG keyed by tuner channel id.</param>
    /// <returns>Channel items to display.</returns>
    public static IReadOnlyList<ChannelItemInfo> Build(
        IReadOnlyList<ChannelInfo> channels,
        string? folderId,
        IReadOnlyDictionary<string, LiveTvNowNext>? guide = null)
    {
        ArgumentNullException.ThrowIfNull(channels);

        if (!string.IsNullOrWhiteSpace(folderId))
        {
            var groupName = DecodeGroupId(folderId);
            return channels
                .Where(channel => string.Equals(channel.ChannelGroup, groupName, StringComparison.OrdinalIgnoreCase))
                .Select(channel => CreateMediaItem(channel, guide))
                .OrderBy(static item => item.SortName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var groups = channels
            .Select(static channel => channel.ChannelGroup)
            .Where(static group => !string.IsNullOrWhiteSpace(group))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static group => LiveTvLibraryChannelPresentation.CleanName(group), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var items = new List<ChannelItemInfo>();
        foreach (var group in groups)
        {
            var inGroup = channels
                .Where(channel => string.Equals(channel.ChannelGroup, group, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var label = LiveTvLibraryChannelPresentation.CleanName(group);
            if (string.IsNullOrWhiteSpace(label))
            {
                label = group;
            }

            items.Add(new ChannelItemInfo
            {
                Id = EncodeGroupId(group),
                Name = label + "  ·  " + inGroup.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                SortName = "0-" + label,
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                Overview = inGroup.Count == 1 ? "1 Sender" : inGroup.Count + " Sender",
                ImageUrl = inGroup.FirstOrDefault(static channel => !string.IsNullOrWhiteSpace(channel.ImageUrl))?.ImageUrl
            });
        }

        items.AddRange(channels
            .Where(static channel => string.IsNullOrWhiteSpace(channel.ChannelGroup))
            .Select(channel => CreateMediaItem(channel, guide))
            .OrderBy(static item => item.SortName, StringComparer.OrdinalIgnoreCase));

        return items;
    }

    /// <summary>
    /// Encodes a group name as a folder id.
    /// </summary>
    /// <param name="groupName">The group title.</param>
    /// <returns>The folder id.</returns>
    public static string EncodeGroupId(string groupName)
        => GroupPrefix + Uri.EscapeDataString(groupName);

    /// <summary>
    /// Decodes a folder id back to a group name.
    /// </summary>
    /// <param name="folderId">The folder id.</param>
    /// <returns>The group name, or the original value.</returns>
    public static string DecodeGroupId(string folderId)
    {
        if (folderId.StartsWith(GroupPrefix, StringComparison.Ordinal))
        {
            return Uri.UnescapeDataString(folderId[GroupPrefix.Length..]);
        }

        return folderId;
    }

    private static ChannelItemInfo CreateMediaItem(ChannelInfo channel, IReadOnlyDictionary<string, LiveTvNowNext>? guide)
    {
        LiveTvNowNext? nowNext = null;
        guide?.TryGetValue(channel.Id, out nowNext);
        var name = LiveTvLibraryChannelPresentation.CleanName(channel.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = channel.Name ?? channel.Id;
        }

        var image = channel.ImageUrl;
        if (string.IsNullOrWhiteSpace(image)
            && nowNext is not null
            && !string.IsNullOrWhiteSpace(nowNext.NowImageUrl)
            && nowNext.NowImageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            image = nowNext.NowImageUrl;
        }

        return new ChannelItemInfo
        {
            Id = channel.Id,
            Name = LiveTvLibraryChannelPresentation.CardName(name, nowNext),
            OriginalTitle = name,
            SortName = LiveTvLibraryChannelPresentation.SortName(channel.Number, name),
            ImageUrl = image,
            Overview = LiveTvLibraryChannelPresentation.Overview(nowNext),
            Type = ChannelItemType.Media,
            MediaType = ChannelMediaType.Video,
            ContentType = ChannelMediaContentType.TvExtra,
            IsLiveStream = true,
            Tags = string.IsNullOrWhiteSpace(channel.ChannelGroup) ? [] : [channel.ChannelGroup]
        };
    }
}
