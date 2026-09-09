using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.TunerHosts;

/// <summary>
/// Probes M3U listing URLs and ingest hosts. Never opens a media stream — IPTV providers
/// treat a TS GET as a viewer and then refuse a second device.
/// </summary>
public sealed class M3uPlaylistHealthChecker
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<M3uPlaylistHealthChecker> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="M3uPlaylistHealthChecker"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public M3uPlaylistHealthChecker(IHttpClientFactory httpClientFactory, ILogger<M3uPlaylistHealthChecker> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    internal async Task<M3uPlaylistHealthResult> ProbeAsync(string playlistUrl, TunerHostInfo info, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistUrl);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (M3uUrlFailover.IsIngestEndpoint(playlistUrl))
            {
                var reachable = await ProbeHostAsync(playlistUrl, info, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();
                return Result(playlistUrl, reachable, stopwatch.ElapsedMilliseconds, 0, 0);
            }

            var probeInfo = new TunerHostInfo
            {
                Url = playlistUrl,
                UserAgent = info.UserAgent,
                Referrer = info.Referrer
            };

            var playlist = await new M3uParser(_logger, _httpClientFactory)
                .ParsePlaylist(probeInfo, "probe_", cancellationToken)
                .ConfigureAwait(false);

            stopwatch.Stop();
            var count = playlist.Channels.Count;
            return Result(playlistUrl, count > 0, stopwatch.ElapsedMilliseconds, stopwatch.ElapsedMilliseconds, 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "Health probe failed for playlist {Url}", playlistUrl);
            return Result(playlistUrl, false, stopwatch.ElapsedMilliseconds, 0, 0);
        }
    }

    /// <summary>
    /// Scores a listing or host probe. Failed probes are -1.
    /// </summary>
    /// <param name="success">Whether the probe reached a usable listing or host.</param>
    /// <param name="elapsedMs">Elapsed milliseconds.</param>
    /// <returns>The score.</returns>
    internal static double Score(bool success, long elapsedMs)
        => success ? 1000d / Math.Max(elapsedMs, 1d) : -1;

    private static M3uPlaylistHealthResult Result(string url, bool success, long elapsedMs, long playlistMs, int bytesRead)
        => new()
        {
            Url = url,
            Success = success,
            ElapsedMs = elapsedMs,
            PlaylistMs = playlistMs,
            BytesRead = bytesRead,
            Score = Score(success, elapsedMs)
        };

    private async Task<bool> ProbeHostAsync(string hostUrl, TunerHostInfo info, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(hostUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var root = new UriBuilder(uri) { Path = "/", Query = string.Empty }.Uri;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

            using var request = new HttpRequestMessage(HttpMethod.Head, root);
            request.Headers.ConnectionClose = true;
            if (!string.IsNullOrWhiteSpace(info.UserAgent))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", info.UserAgent);
            }

            using var response = await _httpClientFactory.CreateClient(NamedClient.Default)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);

            // Any HTTP response means the ingest host is reachable. Do not follow up with a TS GET.
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ingest host probe failed for {Url}", hostUrl);
            return false;
        }
    }
}
