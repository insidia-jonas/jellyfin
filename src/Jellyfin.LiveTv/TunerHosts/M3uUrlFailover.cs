using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Model.LiveTv;

namespace Jellyfin.LiveTv.TunerHosts;

/// <summary>
/// Helpers for multi-server M3U playlists: URL lists, host rewriting, and stable channel keys.
/// </summary>
internal static class M3uUrlFailover
{
    /// <summary>
    /// Number of consecutive confirmed hangs required before switching ingest servers.
    /// </summary>
    public const int ConfirmedHangsBeforeSwitch = 2;

    private static readonly char[] UrlSeparators = ['|', '\n', '\r'];

    /// <summary>
    /// Splits a tuner URL field that may contain several ingest playlists.
    /// </summary>
    /// <param name="value">A single URL or pipe/newline-separated URLs.</param>
    /// <returns>Distinct trimmed URLs.</returns>
    public static IReadOnlyList<string> SplitUrls(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split(UrlSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static part => part.Contains("://", StringComparison.Ordinal)
                                  || part.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
                                  || part.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Gets every configured playlist URL for health checks and failover.
    /// </summary>
    /// <param name="info">The tuner host.</param>
    /// <returns>Candidate playlist URLs, active URL first when present.</returns>
    public static IReadOnlyList<string> GetCandidateUrls(TunerHostInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var urls = new List<string>();
        urls.AddRange(SplitUrls(info.ActiveUrl));
        urls.AddRange(SplitUrls(info.Url));
        if (info.AlternateUrls is not null)
        {
            foreach (var alternate in info.AlternateUrls)
            {
                urls.AddRange(SplitUrls(alternate));
            }
        }

        return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Gets the URL that should be used for stream-host failover (active ingest or playlist).
    /// </summary>
    /// <param name="info">The tuner host.</param>
    /// <returns>The active ingest host or the first candidate URL.</returns>
    public static string GetPrimaryUrl(TunerHostInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var health = GetHealthCandidates(info);
        if (!string.IsNullOrWhiteSpace(info.ActiveUrl))
        {
            var active = SplitUrls(info.ActiveUrl);
            if (active.Count == 1
                && health.Contains(active[0], StringComparer.OrdinalIgnoreCase))
            {
                return active[0];
            }
        }

        return health.Count > 0 ? health[0] : info.Url;
    }

    /// <summary>
    /// Gets the M3U listing URL. Ingest-only hosts (no path) are never fetched as playlists.
    /// </summary>
    /// <param name="info">The tuner host.</param>
    /// <returns>The playlist URL.</returns>
    public static string GetPlaylistUrl(TunerHostInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        foreach (var url in GetCandidateUrls(info))
        {
            if (!IsIngestEndpoint(url))
            {
                return url;
            }
        }

        return !string.IsNullOrWhiteSpace(info.Url) ? SplitUrls(info.Url).FirstOrDefault() ?? info.Url : info.Url;
    }

    /// <summary>
    /// Gets URLs to probe and fail over between.
    /// When host-only ingest endpoints are present, playlist listing URLs are excluded
    /// so health checks rewrite streams onto those hosts instead of the playlist CDN.
    /// </summary>
    /// <param name="info">The tuner host.</param>
    /// <returns>Ingest hosts, or all playlist URLs when no ingest hosts exist.</returns>
    public static IReadOnlyList<string> GetHealthCandidates(TunerHostInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var all = GetCandidateUrls(info);
        var ingest = all.Where(IsIngestEndpoint).ToArray();
        return ingest.Length > 0 ? ingest : all;
    }

    /// <summary>
    /// Returns true when the URL is an ingest origin (scheme + host, no playlist path).
    /// </summary>
    /// <param name="url">The candidate URL.</param>
    /// <returns><c>true</c> if the URL should be used only to rewrite stream hosts.</returns>
    public static bool IsIngestEndpoint(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        return (uri.AbsolutePath.Length == 0 || uri.AbsolutePath == "/")
               && string.IsNullOrEmpty(uri.Query);
    }

    /// <summary>
    /// Stores pipe-separated playlist URLs as a primary URL plus alternates.
    /// Host-only ingest endpoints stay in <see cref="TunerHostInfo.AlternateUrls"/>;
    /// the listing playlist remains in <see cref="TunerHostInfo.Url"/>.
    /// </summary>
    /// <param name="info">The tuner host to normalize.</param>
    public static void NormalizeTunerUrls(TunerHostInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var candidates = GetCandidateUrls(info);
        if (candidates.Count == 0)
        {
            return;
        }

        var playlist = candidates.FirstOrDefault(static url => !IsIngestEndpoint(url)) ?? candidates[0];
        info.Url = playlist;
        info.AlternateUrls = candidates
            .Where(url => !string.Equals(url, playlist, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var health = GetHealthCandidates(info);
        if (string.IsNullOrWhiteSpace(info.ActiveUrl)
            || !health.Contains(info.ActiveUrl, StringComparer.OrdinalIgnoreCase))
        {
            info.ActiveUrl = health.Count > 0 ? health[0] : playlist;
        }
    }

    /// <summary>
    /// Rewrites a channel stream onto another ingest or playlist host/scheme/port.
    /// </summary>
    /// <param name="streamUrl">The current stream URL.</param>
    /// <param name="playlistUrl">The playlist or ingest URL whose host should be used.</param>
    /// <returns>The rewritten stream URL, or the original if rewriting is not possible.</returns>
    public static string RewriteStreamUrl(string streamUrl, string? playlistUrl)
    {
        if (string.IsNullOrWhiteSpace(streamUrl) || string.IsNullOrWhiteSpace(playlistUrl))
        {
            return streamUrl;
        }

        if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out var stream)
            || !Uri.TryCreate(playlistUrl, UriKind.Absolute, out var playlist))
        {
            return streamUrl;
        }

        if (!ShouldRewriteStreamHost(stream, playlist))
        {
            return streamUrl;
        }

        var builder = new UriBuilder(stream)
        {
            Scheme = playlist.Scheme,
            Host = playlist.Host,
            Port = playlist.IsDefaultPort ? -1 : playlist.Port
        };

        return builder.Uri.AbsoluteUri;
    }

    /// <summary>
    /// Returns false when <paramref name="target"/> is a listing playlist on another host
    /// and the stream path is not the same style (CDN playlist + regional MPEG-TS).
    /// </summary>
    /// <param name="stream">The channel stream URL.</param>
    /// <param name="target">The playlist or ingest URL whose host would be applied.</param>
    /// <returns><c>true</c> if the stream host should be rewritten onto <paramref name="target"/>.</returns>
    internal static bool ShouldRewriteStreamHost(Uri stream, Uri target)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(target);

        // Host-only ingest URLs (scheme + host, no playlist path) always rewrite.
        // Do not use the authority alone: every http URL's host looks like an ingest endpoint.
        if (IsIngestEndpoint(target.AbsoluteUri))
        {
            return true;
        }

        if (!LooksLikePlaylistPath(target.AbsolutePath)
            || string.Equals(stream.Host, target.Host, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(
            FirstPathSegment(stream.AbsolutePath),
            FirstPathSegment(target.AbsolutePath),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePlaylistPath(string path)
        => path.Contains(".m3u", StringComparison.OrdinalIgnoreCase)
           || path.Contains("/iptv/", StringComparison.OrdinalIgnoreCase);

    private static string FirstPathSegment(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0] : string.Empty;
    }

    /// <summary>
    /// Returns a host-independent key so channel IDs survive ingest failover.
    /// </summary>
    /// <param name="mediaUrl">The channel stream URL.</param>
    /// <returns>Path and query, or the original value.</returns>
    public static string StableStreamKey(string mediaUrl)
    {
        if (Uri.TryCreate(mediaUrl, UriKind.Absolute, out var uri))
        {
            return uri.AbsolutePath + uri.Query;
        }

        return mediaUrl;
    }

    /// <summary>
    /// Gets the next playlist URL after <paramref name="currentPlaylistUrl"/>.
    /// </summary>
    /// <param name="candidates">All playlist URLs.</param>
    /// <param name="currentPlaylistUrl">The URL that just failed.</param>
    /// <returns>The next URL, wrapping to the first.</returns>
    public static string? GetNextUrl(IReadOnlyList<string> candidates, string? currentPlaylistUrl)
    {
        if (candidates is null || candidates.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(currentPlaylistUrl))
        {
            return candidates[0];
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            if (string.Equals(candidates[i], currentPlaylistUrl, StringComparison.OrdinalIgnoreCase))
            {
                return candidates[(i + 1) % candidates.Count];
            }
        }

        return candidates[0];
    }

    /// <summary>
    /// Gets the hang timeout for a tuner, falling back to 10 seconds.
    /// </summary>
    /// <param name="info">The tuner host.</param>
    /// <returns>The idle timeout.</returns>
    public static TimeSpan GetHangTimeout(TunerHostInfo? info)
    {
        var seconds = info?.HangTimeoutSeconds ?? 0;
        if (seconds <= 0)
        {
            seconds = 10;
        }

        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Determines whether the 15-minute health check should change the active playlist URL.
    /// Switches when the current ingest failed and another succeeded, or when another score is better.
    /// </summary>
    /// <param name="currentUrl">The URL currently selected.</param>
    /// <param name="current">The probe result for the current URL, if any.</param>
    /// <param name="best">The best probe result from this round.</param>
    /// <returns><c>true</c> if the active URL should change.</returns>
    public static bool ShouldSwitchActiveUrl(string? currentUrl, M3uPlaylistHealthResult? current, M3uPlaylistHealthResult best)
    {
        ArgumentNullException.ThrowIfNull(best);

        if (!best.Success)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(currentUrl)
            || string.Equals(currentUrl, best.Url, StringComparison.OrdinalIgnoreCase))
        {
            return !string.Equals(currentUrl, best.Url, StringComparison.OrdinalIgnoreCase) && best.Success;
        }

        // Current ingest failed or was not probed: switch on doubt.
        if (current is null || !current.Success)
        {
            return true;
        }

        return best.Score > current.Score;
    }

    /// <summary>
    /// Determines whether a live hang is certain enough to change ingest servers.
    /// One idle timeout reconnects the same URL; a second consecutive hang switches.
    /// </summary>
    /// <param name="consecutiveConfirmedHangs">How many idle timeouts happened in a row.</param>
    /// <returns><c>true</c> if the ingest URL should change.</returns>
    public static bool ShouldSwitchAfterHang(int consecutiveConfirmedHangs)
        => consecutiveConfirmedHangs >= ConfirmedHangsBeforeSwitch;

    /// <summary>
    /// Returns true when the media path is HLS rather than MPEG-TS.
    /// </summary>
    /// <param name="path">The stream or playlist path.</param>
    /// <param name="container">Optional container hint.</param>
    /// <returns><c>true</c> if the stream is HLS.</returns>
    public static bool IsHls(string? path, string? container = null)
    {
        if (string.Equals(container, "hls", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return path.Contains("m3u8", StringComparison.OrdinalIgnoreCase)
               || path.Contains("/hls", StringComparison.OrdinalIgnoreCase);
    }
}
