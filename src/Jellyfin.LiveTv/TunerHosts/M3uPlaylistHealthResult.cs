namespace Jellyfin.LiveTv.TunerHosts;

/// <summary>
/// Result of probing one M3U ingest playlist.
/// </summary>
internal sealed class M3uPlaylistHealthResult
{
    /// <summary>
    /// Gets the probed playlist URL.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Gets a value indicating whether the listing or ingest host responded.
    /// Media streams are never opened during a probe.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Gets the total probe time in milliseconds.
    /// </summary>
    public long ElapsedMs { get; init; }

    /// <summary>
    /// Gets the playlist download time in milliseconds.
    /// </summary>
    public long PlaylistMs { get; init; }

    /// <summary>
    /// Gets how many stream bytes were read during the probe. Always 0: probes must not open IPTV media.
    /// </summary>
    public int BytesRead { get; init; }

    /// <summary>
    /// Gets the health score. Higher is better; failed probes are -1.
    /// </summary>
    public double Score { get; init; }
}
