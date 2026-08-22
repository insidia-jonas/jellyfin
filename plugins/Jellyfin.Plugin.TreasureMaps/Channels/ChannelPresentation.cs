using System;
using System.Globalization;

namespace Jellyfin.Plugin.TreasureMaps.Channels;

/// <summary>
/// Sort keys and Fire-TV presentation helpers. Android TV clients default to name sort, so
/// category folders and title cards need an explicit <c>ForcedSortName</c> or they appear
/// A–Z / scattered instead of the intended order.
/// </summary>
public static class ChannelPresentation
{
    /// <summary>
    /// Sort key for a category folder so the defined order wins the client's name sort.
    /// </summary>
    /// <param name="order">Zero-based folder order.</param>
    /// <param name="name">The folder display name.</param>
    /// <returns>The sort key.</returns>
    public static string FolderSortName(int order, string name)
        => order.ToString("D2", CultureInfo.InvariantCulture) + "-" + name;

    /// <summary>
    /// Sort key for a title card: newest <paramref name="posted"/> first under name sort.
    /// </summary>
    /// <param name="posted">The release posted date, if known.</param>
    /// <param name="title">The title.</param>
    /// <returns>The sort key.</returns>
    public static string TitleSortName(DateTimeOffset? posted, string title)
    {
        var ticks = posted?.UtcTicks ?? 0;
        var inverted = (DateTimeOffset.MaxValue.UtcTicks - ticks).ToString("D19", CultureInfo.InvariantCulture);
        return inverted + "-" + title;
    }

    /// <summary>
    /// Sort key for a Downloads card: active jobs first, then by title.
    /// </summary>
    /// <param name="active">Whether the job is still downloading.</param>
    /// <param name="title">The movie/show title.</param>
    /// <returns>The sort key.</returns>
    public static string DownloadSortName(bool active, string title)
        => (active ? "0-" : "1-") + title;
}
