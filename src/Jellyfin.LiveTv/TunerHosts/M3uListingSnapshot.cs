using System;
using System.Collections.Generic;
using MediaBrowser.Controller.LiveTv;

namespace Jellyfin.LiveTv.TunerHosts;

/// <summary>
/// Last-good parsed M3U channel list plus playlist validators.
/// </summary>
internal sealed class M3uListingSnapshot
{
    /// <summary>Gets or sets the parsed channels.</summary>
    public List<ChannelInfo> Channels { get; set; } = [];

    /// <summary>Gets or sets the playlist ETag from the last 200 response.</summary>
    public string? ETag { get; set; }

    /// <summary>Gets or sets Last-Modified from the last 200 response.</summary>
    public DateTimeOffset? LastModified { get; set; }

    /// <summary>Gets or sets when the snapshot was last confirmed (200 or 304).</summary>
    public DateTime FetchedUtc { get; set; }

    /// <summary>Gets or sets the playlist URL that produced this snapshot.</summary>
    public string? PlaylistUrl { get; set; }
}
