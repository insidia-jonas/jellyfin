#pragma warning disable CA1711
#pragma warning disable CS1591

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.LiveTv.Configuration;
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
        private readonly IConfigurationManager _configurationManager;

        public SharedHttpStream(
            MediaSourceInfo mediaSource,
            TunerHostInfo tunerHostInfo,
            string originalStreamId,
            IFileSystem fileSystem,
            IHttpClientFactory httpClientFactory,
            ILogger logger,
            IConfigurationManager configurationManager,
            IServerApplicationHost appHost,
            IStreamHelper streamHelper)
            : base(mediaSource, tunerHostInfo, fileSystem, logger, configurationManager, streamHelper)
        {
            _httpClientFactory = httpClientFactory;
            _appHost = appHost;
            _tunerHostInfo = tunerHostInfo;
            _configurationManager = configurationManager;
            OriginalStreamId = originalStreamId;
        }

        public override async Task Open(CancellationToken openCancellationToken)
        {
            LiveStreamCancellationTokenSource.Token.ThrowIfCancellationRequested();

            var mediaSource = OriginalMediaSource;

            var url = mediaSource.Path;

            Directory.CreateDirectory(Path.GetDirectoryName(TempFilePath) ?? throw new InvalidOperationException("Path can't be a root directory."));

            var typeName = GetType().Name;
            Logger.LogInformation("Opening {StreamType} Live stream from {Url}", typeName, url);

            var taskCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            _ = StartStreaming(url, mediaSource, taskCompletionSource, LiveStreamCancellationTokenSource.Token);

            MediaSource.Path = _appHost.GetApiUrlForLocalAccess() + "/LiveTv/LiveStreamFiles/" + UniqueId + "/stream.ts";
            MediaSource.Protocol = MediaProtocol.Http;

            var res = await taskCompletionSource.Task.ConfigureAwait(false);
            if (!res)
            {
                Logger.LogWarning("Zero bytes copied from stream {StreamType} to {FilePath} but no exception raised", GetType().Name, TempFilePath);
                throw new EndOfStreamException(string.Format(CultureInfo.InvariantCulture, "Zero bytes copied from stream {0}", GetType().Name));
            }
        }

        private Task StartStreaming(string url, MediaSourceInfo mediaSource, TaskCompletionSource<bool> openTaskCompletionSource, CancellationToken cancellationToken)
        {
            return Task.Run(
                async () =>
                {
                    try
                    {
                        Logger.LogInformation("Beginning {StreamType} stream to {FilePath}", GetType().Name, TempFilePath);

                        var fileStream = new FileStream(
                            TempFilePath,
                            FileMode.Create,
                            FileAccess.Write,
                            FileShare.Read,
                            IODefaults.FileStreamBufferSize,
                            FileOptions.Asynchronous);

                        await using (fileStream.ConfigureAwait(false))
                        {
                            var attempt = 0;
                            var consecutiveHangs = 0;
                            var originalUrl = url;
                            var currentPlaylistUrl = M3uUrlFailover.GetPrimaryUrl(_tunerHostInfo);
                            var hangTimeout = M3uUrlFailover.GetHangTimeout(_tunerHostInfo);
                            var candidates = M3uUrlFailover.GetHealthCandidates(_tunerHostInfo);

                            while (!cancellationToken.IsCancellationRequested)
                            {
                                attempt++;
                                try
                                {
                                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                                    ApplyRequiredHeaders(request, mediaSource.RequiredHttpHeaders);

                                    var response = await _httpClientFactory.CreateClient(NamedClient.Default)
                                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                                        .ConfigureAwait(false);

                                    using (response)
                                    {
                                        response.EnsureSuccessStatusCode();
                                        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                                        await using (stream.ConfigureAwait(false))
                                        {
                                            await StreamHelper.CopyToAsync(
                                                stream,
                                                fileStream,
                                                IODefaults.CopyToBufferSize,
                                                () =>
                                                {
                                                    consecutiveHangs = 0;
                                                    Resolve(openTaskCompletionSource);
                                                },
                                                hangTimeout,
                                                cancellationToken).ConfigureAwait(false);
                                        }
                                    }

                                    if (cancellationToken.IsCancellationRequested)
                                    {
                                        break;
                                    }

                                    Logger.LogWarning("Live HTTP stream ended unexpectedly, reconnecting. Attempt {Attempt}. Path: {FilePath}", attempt, TempFilePath);
                                }
                                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                                {
                                    Logger.LogInformation("Copying of {StreamType} to {FilePath} was canceled", GetType().Name, TempFilePath);
                                    break;
                                }
                                catch (TimeoutException)
                                {
                                    consecutiveHangs++;
                                    Logger.LogWarning(
                                        "Confirmed idle hang {HangCount}/{Required} on {Url} after {Timeout}. Attempt {Attempt}",
                                        consecutiveHangs,
                                        M3uUrlFailover.ConfirmedHangsBeforeSwitch,
                                        url,
                                        hangTimeout,
                                        attempt);
                                }
                                catch (Exception ex)
                                {
                                    Logger.LogWarning(ex, "Error copying live stream {StreamType} to {FilePath}. Attempt {Attempt}", GetType().Name, TempFilePath, attempt);

                                    if (!openTaskCompletionSource.Task.IsCompleted && attempt >= 3)
                                    {
                                        openTaskCompletionSource.TrySetException(ex);
                                        return;
                                    }
                                }

                                if (cancellationToken.IsCancellationRequested)
                                {
                                    break;
                                }

                                if (M3uUrlFailover.ShouldSwitchAfterHang(consecutiveHangs) && candidates.Count > 1)
                                {
                                    var nextPlaylistUrl = M3uUrlFailover.GetNextUrl(candidates, currentPlaylistUrl);
                                    if (!string.IsNullOrWhiteSpace(nextPlaylistUrl))
                                    {
                                        currentPlaylistUrl = nextPlaylistUrl;
                                        url = M3uUrlFailover.RewriteStreamUrl(originalUrl, currentPlaylistUrl);
                                        PersistActiveUrl(currentPlaylistUrl);
                                        consecutiveHangs = 0;
                                        Logger.LogInformation("Hang confirmed. Failing over live stream to {Url}", url);
                                    }
                                }

                                var delayMs = Math.Min(1000 * attempt, 5000);
                                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (OperationCanceledException ex)
                    {
                        Logger.LogInformation("Copying of {StreamType} to {FilePath} was canceled", GetType().Name, TempFilePath);
                        openTaskCompletionSource.TrySetException(ex);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Error copying live stream {StreamType} to {FilePath}", GetType().Name, TempFilePath);
                        openTaskCompletionSource.TrySetException(ex);
                    }

                    openTaskCompletionSource.TrySetResult(false);

                    EnableStreamSharing = false;
                    await DeleteTempFiles(TempFilePath).ConfigureAwait(false);
                },
                CancellationToken.None);
        }

        private void PersistActiveUrl(string playlistUrl)
        {
            if (string.IsNullOrWhiteSpace(playlistUrl) || string.IsNullOrWhiteSpace(_tunerHostInfo.Id))
            {
                return;
            }

            _tunerHostInfo.ActiveUrl = playlistUrl;

            try
            {
                var config = _configurationManager.GetLiveTvConfiguration();
                var tuner = config.TunerHosts.FirstOrDefault(item =>
                    string.Equals(item.Id, _tunerHostInfo.Id, StringComparison.OrdinalIgnoreCase));
                if (tuner is null)
                {
                    return;
                }

                tuner.ActiveUrl = playlistUrl;
                _configurationManager.SaveConfiguration("livetv", config);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Unable to persist M3U failover URL {Url}", playlistUrl);
            }
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
            DateOpened = DateTime.UtcNow;
            openTaskCompletionSource.TrySetResult(true);
        }
    }
}
