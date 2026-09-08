using System.Collections.Generic;
using MediaBrowser.Controller.LiveTv;

namespace Jellyfin.LiveTv.TunerHosts;

/// <summary>
/// Parsed M3U playlist channels plus IPTV header metadata (EPG, HTTP identity).
/// </summary>
internal sealed class M3uPlaylist
{
    /// <summary>
    /// Gets the channels listed in the playlist.
    /// </summary>
    public List<ChannelInfo> Channels { get; } = [];

    /// <summary>
    /// Gets or sets the first HTTP(S) EPG URL from url-tvg / x-tvg-url / tvg-url.
    /// </summary>
    public string? EpgUrl { get; set; }

    /// <summary>
    /// Gets or sets a playlist-wide HTTP user agent from #EXTVLCOPT.
    /// </summary>
    public string? UserAgent { get; set; }

    /// <summary>
    /// Gets or sets a playlist-wide HTTP referrer from #EXTVLCOPT.
    /// </summary>
    public string? Referrer { get; set; }
}
