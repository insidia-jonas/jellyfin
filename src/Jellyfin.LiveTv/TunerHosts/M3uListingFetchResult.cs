using System;

namespace Jellyfin.LiveTv.TunerHosts;

/// <summary>
/// Result of a playlist listing GET (never an ingest/media URL).
/// </summary>
internal sealed class M3uListingFetchResult
{
    /// <summary>Gets a value indicating whether the server returned 304 Not Modified.</summary>
    public bool NotModified { get; init; }

    /// <summary>Gets the parsed playlist when the listing body was downloaded.</summary>
    public M3uPlaylist? Playlist { get; init; }

    /// <summary>Gets the ETag to store for the next conditional GET.</summary>
    public string? ETag { get; init; }

    /// <summary>Gets Last-Modified to store for the next conditional GET.</summary>
    public DateTimeOffset? LastModified { get; init; }

    /// <summary>Gets the playlist URL that was requested.</summary>
    public string PlaylistUrl { get; init; } = string.Empty;
}
