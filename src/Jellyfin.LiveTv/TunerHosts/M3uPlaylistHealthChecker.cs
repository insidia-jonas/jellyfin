using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.TunerHosts;

/// <summary>
/// Probes M3U playlist ingest servers and scores them for hang-free delivery.
/// </summary>
public sealed class M3uPlaylistHealthChecker
{
    private const int ProbeBytes = 65536;

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
            var probeInfo = new TunerHostInfo
            {
                Url = playlistUrl,
                UserAgent = info.UserAgent,
                Referrer = info.Referrer
            };

            var playlist = await new M3uParser(_logger, _httpClientFactory)
                .ParsePlaylist(probeInfo, "probe_", cancellationToken)
                .ConfigureAwait(false);

            var playlistMs = stopwatch.ElapsedMilliseconds;
            var streamBytes = 0;
            var streamOk = false;

            foreach (var channel in playlist.Channels.Take(3))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var streamUrl = M3uUrlFailover.RewriteStreamUrl(channel.Path, playlistUrl);
                var (ok, bytes) = await ProbeStreamAsync(streamUrl, info, cancellationToken).ConfigureAwait(false);
                streamBytes = Math.Max(streamBytes, bytes);
                if (ok)
                {
                    streamOk = true;
                    break;
                }
            }

            stopwatch.Stop();
            var success = playlist.Channels.Count > 0 && streamOk;
            var score = success
                ? (streamBytes + 1d) / Math.Max(stopwatch.ElapsedMilliseconds, 1d)
                : -1;

            return new M3uPlaylistHealthResult
            {
                Url = playlistUrl,
                Success = success,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
                PlaylistMs = playlistMs,
                BytesRead = streamBytes,
                Score = score
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "Health probe failed for playlist {Url}", playlistUrl);
            return new M3uPlaylistHealthResult
            {
                Url = playlistUrl,
                Success = false,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
                Score = -1
            };
        }
    }

    private async Task<(bool Success, int BytesRead)> ProbeStreamAsync(string streamUrl, TunerHostInfo info, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, streamUrl);
            if (!string.IsNullOrWhiteSpace(info.UserAgent))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", info.UserAgent);
            }

            if (!string.IsNullOrWhiteSpace(info.Referrer))
            {
                request.Headers.TryAddWithoutValidation("Referer", info.Referrer);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));

            using var response = await _httpClientFactory.CreateClient(NamedClient.Default)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var buffer = new byte[8192];
                var total = 0;
                while (total < ProbeBytes)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), timeoutCts.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                }

                return (total > 0, total);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stream probe failed for {Url}", streamUrl);
            return (false, 0);
        }
    }
}
