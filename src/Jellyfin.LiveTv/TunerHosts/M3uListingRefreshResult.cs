using System;
using System.Collections.Generic;
using MediaBrowser.Controller.LiveTv;

namespace Jellyfin.LiveTv.TunerHosts;

/// <summary>
/// Outcome of a tuner listing refresh (playlist GET or HDHR lineup).
/// </summary>
internal sealed class M3uListingRefreshResult
{
    /// <summary>Gets the channels to store when the listing changed.</summary>
    public List<ChannelInfo> Channels { get; init; } = [];

    /// <summary>Gets a value indicating whether the remote listing was unchanged (HTTP 304).</summary>
    public bool NotModified { get; init; }

    /// <summary>Gets the playlist ETag.</summary>
    public string? ETag { get; init; }

    /// <summary>Gets Last-Modified.</summary>
    public DateTimeOffset? LastModified { get; init; }

    /// <summary>Gets the listing URL that was contacted.</summary>
    public string? PlaylistUrl { get; init; }
}
