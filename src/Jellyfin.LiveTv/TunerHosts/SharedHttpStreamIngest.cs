using System;

namespace Jellyfin.LiveTv.TunerHosts;

/// <summary>
/// Keeps the IPTV ingest URL after <see cref="SharedHttpStream.Open"/> rewrites
/// <see cref="MediaBrowser.Model.Dto.MediaSourceInfo.Path"/> to LiveStreamFiles.
/// </summary>
internal static class SharedHttpStreamIngest
{
    /// <summary>
    /// Captures the tuner URL before Open rewrites the published path.
    /// </summary>
    /// <param name="path">The media source path at construction.</param>
    /// <returns>The ingest URL, or null when the path is already a published LiveStreamFiles URL.</returns>
    public static string? Capture(string? path)
    {
        return IsPublishedLiveStreamFilesPath(path) ? null : path;
    }

    /// <summary>
    /// URL to GET on the first <c>/LiveTv/LiveStreamFiles/</c> read.
    /// Never returns the published LiveStreamFiles path (that would loop).
    /// </summary>
    /// <param name="capturedIngestUrl">URL stored at construction.</param>
    /// <param name="currentPath">Current media source path (may already be rewritten).</param>
    /// <returns>The IPTV URL, or null when none is safe to GET.</returns>
    public static string? ForFirstRead(string? capturedIngestUrl, string? currentPath)
    {
        if (!string.IsNullOrWhiteSpace(capturedIngestUrl) && !IsPublishedLiveStreamFilesPath(capturedIngestUrl))
        {
            return capturedIngestUrl;
        }

        if (!string.IsNullOrWhiteSpace(currentPath) && !IsPublishedLiveStreamFilesPath(currentPath))
        {
            return currentPath;
        }

        return null;
    }

    /// <summary>
    /// Returns true when <paramref name="path"/> is a Jellyfin-hosted live file URL.
    /// </summary>
    /// <param name="path">A media source path.</param>
    /// <returns><c>true</c> if a GET of this path would hit LiveStreamFiles.</returns>
    public static bool IsPublishedLiveStreamFilesPath(string? path)
    {
        return path is not null
               && path.Contains("/LiveTv/LiveStreamFiles/", StringComparison.OrdinalIgnoreCase);
    }
}
