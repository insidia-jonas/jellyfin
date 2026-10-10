using System.Collections.Generic;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;

namespace MediaBrowser.Controller.LiveTv;

/// <summary>Selects only configured IPTV origins; never changes tuner configuration.</summary>
public interface IIptvSourceSelector
{
    /// <summary>Reads choices for a channel already authorized by the caller.</summary>
    /// <param name="channelId">External tuner channel identifier.</param>
    /// <param name="liveStreamId">Optional current playback handle.</param>
    /// <returns>Cached observations; no provider connection.</returns>
    IptvChannelSources GetSources(string channelId, string? liveStreamId = null);

    /// <summary>Resolves an explicit selection against this channel's ordinary media source.</summary>
    /// <param name="channelId">Authorized external channel identifier.</param>
    /// <param name="sources">Ordinary media sources.</param>
    /// <param name="selectionId">Requested opaque media source identifier.</param>
    /// <returns>An independent source instance, or null for an invalid selection.</returns>
    MediaSourceInfo? Select(string channelId, IReadOnlyList<MediaSourceInfo> sources, string selectionId);
}
