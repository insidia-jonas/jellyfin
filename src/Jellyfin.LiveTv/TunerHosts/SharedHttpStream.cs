#pragma warning disable CA1711
#pragma warning disable CS1591

using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.LiveTv.Health;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.TunerHosts
{
    public class SharedHttpStream : LiveStream, IDirectStreamProvider
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IServerApplicationHost _appHost;
        private readonly TunerHostInfo _tunerHostInfo;
        private readonly ChannelHealthStore? _health;
        private readonly string _channelId;
        private readonly string? _ingestUrl;
        private int _providerStarted;

        public SharedHttpStream(
            MediaSourceInfo mediaSource,
            TunerHostInfo tunerHostInfo,
            string originalStreamId,
            IFileSystem fileSystem,
            IHttpClientFactory httpClientFactory,
            ILogger logger,
            IConfigurationManager configurationManager,
            IServerApplicationHost appHost,
            IStreamHelper streamHelper,
            ChannelHealthStore? health = null,
            string? channelId = null,
            IDisposable? playbackReservation = null)
            : base(mediaSource, tunerHostInfo, fileSystem, logger, configurationManager, streamHelper, playbackReservation)
        {
            _httpClientFactory = httpClientFactory;
            _appHost = appHost;
            _tunerHostInfo = tunerHostInfo;
            _health = health;
            _channelId = channelId ?? originalStreamId;
            OriginalStreamId = originalStreamId;
            // Open() rewrites MediaSource.Path (same object as OriginalMediaSource)
            // to the published LiveStreamFiles URL. Capture the IPTV URL first.
            _ingestUrl = SharedHttpStreamIngest.Capture(mediaSource.Path);
        }

        /// <inheritdoc />
        public override Task Open(CancellationToken openCancellationToken)
        {
            LiveStreamCancellationTokenSource.Token.ThrowIfCancellationRequested();

            Directory.CreateDirectory(Path.GetDirectoryName(TempFilePath) ?? throw new InvalidOperationException("Path can't be a root directory."));
            if (!File.Exists(TempFilePath))
            {
                File.WriteAllBytes(TempFilePath, []);
            }

            // Do not GET the IPTV URL here. Fire TV AutoOpenLiveStream calls Open
            // when focusing a tile; connecting now would steal the provider slot.
            MediaSource.Path = _appHost.GetApiUrlForLocalAccess() + "/LiveTv/LiveStreamFiles/" + UniqueId + "/stream.ts";
            MediaSource.Protocol = MediaProtocol.Http;
            // Direct playback is safe only after replacing the provider URL with our
            // shared authenticated proxy. The subsequent codec probe/profile check
            // still selects transcoding for formats the client cannot decode.
            MediaSource.SupportsDirectPlay = !MediaSource.RequiresLooping;
            DateOpened = DateTime.UtcNow;

            Logger.LogInformation(
                "Prepared {StreamType} live stream {Id} (provider connect deferred until first read)",
                GetType().Name,
                UniqueId);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public override Stream GetStream()
        {
            EnsureProviderConnected();
            EnsureTempFile();
            return base.GetStream();
        }

        private void EnsureTempFile()
        {
            var path = TempFilePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (!File.Exists(path))
            {
                using var created = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
            }
        }

        private void EnsureProviderConnected()
        {
            if (Interlocked.Exchange(ref _providerStarted, 1) != 0)
            {
                return;
            }

            var mediaSource = OriginalMediaSource;
            var ingestUrl = SharedHttpStreamIngest.ForFirstRead(_ingestUrl, mediaSource?.Path);
            if (mediaSource is null || string.IsNullOrEmpty(ingestUrl))
            {
                Logger.LogError("SharedHttpStream {Id} has no IPTV ingest URL; refusing to GET LiveStreamFiles", UniqueId);
                return;
            }

            _health?.BeginPlayback(_channelId, TunerHostId);
            StreamingTask = StartStreaming(
                ingestUrl,
                mediaSource,
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
                LiveStreamCancellationTokenSource.Token);
        }

        private async Task StartStreaming(string url, MediaSourceInfo mediaSource, TaskCompletionSource<bool> openTaskCompletionSource, CancellationToken cancellationToken)
        {
            var watch = Stopwatch.StartNew();
            var originalUrl = url;
            var currentOrigin = M3uUrlFailover.GetPrimaryUrl(_tunerHostInfo);
            var candidates = M3uUrlFailover.GetHealthCandidates(_tunerHostInfo);
            var recovery = new LiveStreamRecovery();
            var hangTimeout = M3uUrlFailover.GetHangTimeout(_tunerHostInfo);
            long totalBytes = 0;
            try
            {
                var destination = new FileStream(TempFilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, IODefaults.FileStreamBufferSize, FileOptions.Asynchronous);
                await using var destinationLifetime = destination.ConfigureAwait(false);
                while (!cancellationToken.IsCancellationRequested)
                {
                    DateTime? firstData = null;
                    try
                    {
                        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        headerTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                        using var request = new HttpRequestMessage(HttpMethod.Get, url);
                        request.Headers.ConnectionClose = true;
                        ApplyRequiredHeaders(request, mediaSource.RequiredHttpHeaders);
                        HttpResponseMessage response;
                        try
                        {
                            response = await _httpClientFactory.CreateClient(NamedClient.Iptv)
                                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            throw new HttpRequestException("Provider did not return HTTP headers before the deadline.");
                        }

                        headerTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
                        using (response)
                        {
                            response.EnsureSuccessStatusCode();
                            var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                            await using var sourceLifetime = source.ConfigureAwait(false);
                            var buffer = ArrayPool<byte>.Shared.Rent(IODefaults.CopyToBufferSize);
                            var sample = new byte[4096];
                            var sampled = 0;
                            var verified = false;
                            var reported = DateTime.MinValue;
                            try
                            {
                                while (true)
                                {
                                    using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                                    readTimeout.CancelAfter(hangTimeout);
                                    int read;
                                    try
                                    {
                                        read = await source.ReadAsync(buffer.AsMemory(), readTimeout.Token).ConfigureAwait(false);
                                    }
                                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                                    {
                                        throw new TimeoutException("Live stream stopped producing media data.");
                                    }

                                    if (read == 0)
                                    {
                                        throw new EndOfStreamException("Live source ended.");
                                    }

                                    var now = DateTime.UtcNow;
                                    firstData ??= now;
                                    totalBytes += read;
                                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                                    Resolve(openTaskCompletionSource);
                                    if (!verified && sampled < sample.Length)
                                    {
                                        var count = Math.Min(sample.Length - sampled, read);
                                        buffer.AsSpan(0, count).CopyTo(sample.AsSpan(sampled));
                                        sampled += count;
                                        verified = HasTransportPackets(sample.AsSpan(0, sampled));
                                    }

                                    if (verified && now - reported >= TimeSpan.FromSeconds(5))
                                    {
                                        _health?.Success(_channelId, TunerHostId, watch.ElapsedMilliseconds, totalBytes);
                                        reported = now;
                                    }
                                }
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(buffer);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException)
                    {
                        var failure = ChannelFailure.FromException(ex);
                        _health?.Failure(_channelId, TunerHostId, failure.Reason, failure.ProviderWide, firstData.HasValue);
                        Logger.LogWarning("Live channel {ChannelId}: {Reason}; recovery attempt {Attempt}", _channelId, failure.Reason, recovery.Failures + 1);
                        var keepTrying = recovery.Failed(DateTime.UtcNow, firstData.HasValue ? DateTime.UtcNow - firstData.Value : TimeSpan.Zero);
                        if (failure.StopRetries || !keepTrying)
                        {
                            break;
                        }

                        if (recovery.ShouldSwitch && candidates.Count > 1)
                        {
                            currentOrigin = M3uUrlFailover.GetNextUrl(candidates, currentOrigin);
                            url = M3uUrlFailover.RewriteStreamUrl(originalUrl, currentOrigin);
                            // A failing channel must not change the configured origin for every other channel.
                            Logger.LogInformation("Trying configured alternate host {SourceHost} for channel {ChannelId}", new Uri(url).Host, _channelId);
                        }

                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(recovery.Failures, 3)), cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A viewer stopping or changing channels is not a failed stream.
            }
            catch (Exception ex)
            {
                Logger.LogError("Live channel {ChannelId} stopped because of {ErrorType}", _channelId, ex.GetType().Name);
            }
            finally
            {
                openTaskCompletionSource.TrySetResult(false);
                EnableStreamSharing = false;
                await DeleteTempFiles(TempFilePath).ConfigureAwait(false);
            }
        }

        internal static bool HasTransportPackets(ReadOnlySpan<byte> data)
        {
            for (var offset = 0; offset < 188 && offset + (188 * 4) + 4 <= data.Length; offset++)
            {
                var valid = true;
                var payload = false;
                for (var packet = 0; packet < 5; packet++)
                {
                    var start = offset + (packet * 188);
                    valid &= data[start] == 0x47 && (data[start + 1] & 0x80) == 0 && (data[start + 3] & 0x30) != 0;
                    payload |= (data[start + 3] & 0x10) != 0 && ((data[start + 1] & 0x1f) != 0x1f || data[start + 2] != 0xff);
                }

                if (valid && payload)
                {
                    return true;
                }
            }

            return false;
        }

        private static void ApplyRequiredHeaders(HttpRequestMessage request, System.Collections.Generic.Dictionary<string, string> headers)
        {
            if (headers is null)
            {
                return;
            }

            foreach (var header in headers)
            {
                if (string.IsNullOrWhiteSpace(header.Key) || string.IsNullOrWhiteSpace(header.Value))
                {
                    continue;
                }

                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        private void Resolve(TaskCompletionSource<bool> openTaskCompletionSource)
        {
            if (openTaskCompletionSource.TrySetResult(true))
            {
                DateOpened = DateTime.UtcNow;
            }
        }
    }
}
