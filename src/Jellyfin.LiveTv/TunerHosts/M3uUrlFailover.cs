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
    /// Gets the playlist URL that should be fetched now.
    /// </summary>
    /// <param name="info">The tuner host.</param>
    /// <returns>The active or first candidate URL.</returns>
    public static string GetPrimaryUrl(TunerHostInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var candidates = GetCandidateUrls(info);
        if (!string.IsNullOrWhiteSpace(info.ActiveUrl))
        {
            var active = SplitUrls(info.ActiveUrl);
            if (active.Count == 1)
            {
                return active[0];
            }
        }

        return candidates.Count > 0 ? candidates[0] : info.Url;
    }

    /// <summary>
    /// Stores pipe-separated playlist URLs as a primary URL plus alternates.
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

        info.Url = candidates[0];
        info.AlternateUrls = candidates.Count > 1 ? candidates.Skip(1).ToArray() : [];
        if (string.IsNullOrWhiteSpace(info.ActiveUrl)
            || !candidates.Contains(info.ActiveUrl, StringComparer.OrdinalIgnoreCase))
        {
            info.ActiveUrl = candidates[0];
        }
    }

    /// <summary>
    /// Rewrites a channel stream onto another playlist's host/scheme/port.
    /// </summary>
    /// <param name="streamUrl">The current stream URL.</param>
    /// <param name="playlistUrl">The playlist URL whose host should be used.</param>
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

        var builder = new UriBuilder(stream)
        {
            Scheme = playlist.Scheme,
            Host = playlist.Host,
            Port = playlist.IsDefaultPort ? -1 : playlist.Port
        };

        return builder.Uri.AbsoluteUri;
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
