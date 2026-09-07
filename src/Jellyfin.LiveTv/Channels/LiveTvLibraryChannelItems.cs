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
    /// <returns>Channel items to display.</returns>
    public static IReadOnlyList<ChannelItemInfo> Build(IReadOnlyList<ChannelInfo> channels, string? folderId)
    {
        ArgumentNullException.ThrowIfNull(channels);

        if (!string.IsNullOrWhiteSpace(folderId))
        {
            var groupName = DecodeGroupId(folderId);
            return channels
                .Where(channel => string.Equals(channel.ChannelGroup, groupName, StringComparison.OrdinalIgnoreCase))
                .Select(CreateMediaItem)
                .ToArray();
        }

        var groups = channels
            .Select(channel => channel.ChannelGroup)
            .Where(static group => !string.IsNullOrWhiteSpace(group))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static group => group, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var items = new List<ChannelItemInfo>();
        foreach (var group in groups)
        {
            items.Add(new ChannelItemInfo
            {
                Id = EncodeGroupId(group),
                Name = group,
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                ImageUrl = channels.FirstOrDefault(channel =>
                    string.Equals(channel.ChannelGroup, group, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(channel.ImageUrl))?.ImageUrl
            });
        }

        items.AddRange(channels
            .Where(static channel => string.IsNullOrWhiteSpace(channel.ChannelGroup))
            .Select(CreateMediaItem));

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

    private static ChannelItemInfo CreateMediaItem(ChannelInfo channel)
    {
        return new ChannelItemInfo
        {
            Id = channel.Id,
            Name = channel.Name,
            ImageUrl = channel.ImageUrl,
            Type = ChannelItemType.Media,
            MediaType = ChannelMediaType.Video,
            ContentType = ChannelMediaContentType.TvExtra,
            IsLiveStream = true,
            DateCreated = DateTime.UtcNow,
            Tags = string.IsNullOrWhiteSpace(channel.ChannelGroup) ? [] : [channel.ChannelGroup]
        };
    }
}
