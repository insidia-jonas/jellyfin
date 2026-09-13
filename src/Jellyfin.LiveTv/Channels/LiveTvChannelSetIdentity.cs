using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.LiveTv.TunerHosts;
using MediaBrowser.Controller.LiveTv;

namespace Jellyfin.LiveTv.Channels;

/// <summary>
/// Process-wide Live TV channel-id set. Used as IChannel cache key so now/next
/// ticks do not force ChannelManager to rebuild folders.
/// </summary>
internal static class LiveTvChannelSetIdentity
{
    private static readonly ConcurrentDictionary<string, string[]> TunerIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the hash of every known tuner channel id, or empty when none are loaded.
    /// </summary>
    public static string Current
    {
        get
        {
            var ids = TunerIds.Values.SelectMany(static list => list);
            return M3uListingRefreshPolicy.ChannelSetIdentity(ids);
        }
    }

    /// <summary>
    /// Replaces the channel ids stored for one tuner (or the library channel aggregate).
    /// </summary>
    /// <param name="tunerId">The tuner or source key.</param>
    /// <param name="channelIds">The current ids.</param>
    public static void Replace(string tunerId, IEnumerable<string?> channelIds)
    {
        ArgumentException.ThrowIfNullOrEmpty(tunerId);
        ArgumentNullException.ThrowIfNull(channelIds);

        var copy = channelIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id!.Trim())
            .ToArray();

        TunerIds[tunerId] = copy;
    }

    /// <summary>
    /// Records ids from a channel snapshot.
    /// </summary>
    /// <param name="tunerId">The tuner or source key.</param>
    /// <param name="channels">The snapshot channels.</param>
    public static void ReplaceFromChannels(string tunerId, IEnumerable<ChannelInfo> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        Replace(tunerId, channels.Select(static channel => channel.Id));
    }

    /// <summary>
    /// Clears all stored identities. Used by tests.
    /// </summary>
    internal static void Reset() => TunerIds.Clear();
}
