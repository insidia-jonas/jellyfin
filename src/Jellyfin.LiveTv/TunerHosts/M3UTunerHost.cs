#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Extensions;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.LiveTv.TunerHosts
{
    public class M3UTunerHost : BaseTunerHost, ITunerHost, IConfigurableTunerHost
    {
        private static readonly string[] _manifestExtensions = [".m3u8", ".m3u", ".mpd"];

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IServerApplicationHost _appHost;
        private readonly INetworkManager _networkManager;
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly IStreamHelper _streamHelper;

        public M3UTunerHost(
            IServerConfigurationManager config,
            IMediaSourceManager mediaSourceManager,
            ILogger<M3UTunerHost> logger,
            IFileSystem fileSystem,
            IHttpClientFactory httpClientFactory,
            IServerApplicationHost appHost,
            INetworkManager networkManager,
            IStreamHelper streamHelper)
            : base(config, logger, fileSystem)
        {
            _httpClientFactory = httpClientFactory;
            _appHost = appHost;
            _networkManager = networkManager;
            _mediaSourceManager = mediaSourceManager;
            _streamHelper = streamHelper;
        }

        public override string Type => "m3u";

        public virtual string Name => "M3U Tuner";

        private string GetFullChannelIdPrefix(TunerHostInfo info)
        {
            return ChannelIdPrefix + info.Url.GetMD5().ToString("N", CultureInfo.InvariantCulture);
        }

        protected override async Task<List<ChannelInfo>> GetChannelsInternal(TunerHostInfo tuner, CancellationToken cancellationToken)
        {
            var channelIdPrefix = GetFullChannelIdPrefix(tuner);

            var playlist = await new M3uParser(Logger, _httpClientFactory)
                .ParsePlaylist(tuner, channelIdPrefix, cancellationToken)
                .ConfigureAwait(false);

            ApplyPlaylistMetadata(tuner, playlist);
            return playlist.Channels;
        }

        protected override async Task<ILiveStream> GetChannelStream(TunerHostInfo tunerHost, ChannelInfo channel, string streamId, IList<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
        {
            var tunerCount = tunerHost.TunerCount;

            if (tunerCount > 0)
            {
                var tunerHostId = tunerHost.Id;
                var liveStreams = currentLiveStreams.Where(i => string.Equals(i.TunerHostId, tunerHostId, StringComparison.OrdinalIgnoreCase));

                if (liveStreams.Count() >= tunerCount)
                {
                    throw new LiveTvConflictException("M3U simultaneous stream limit has been reached.");
                }
            }

            var sources = await GetChannelStreamMediaSources(tunerHost, channel, cancellationToken).ConfigureAwait(false);

            var mediaSource = sources[0];

            // MPEG-TS IPTV must go through the HTTP proxy so hang detection can fail over ingest hosts.
            // HLS playlists stay on ffmpeg, which already has HTTP reconnect flags.
            if (mediaSource.Protocol == MediaProtocol.Http
                && !mediaSource.RequiresLooping
                && !M3uUrlFailover.IsHls(mediaSource.Path, mediaSource.Container))
            {
                return new SharedHttpStream(mediaSource, tunerHost, streamId, FileSystem, _httpClientFactory, Logger, Config, _appHost, _streamHelper);
            }

            return new LiveStream(mediaSource, tunerHost, FileSystem, Logger, Config, _streamHelper);
        }

        public async Task Validate(TunerHostInfo info)
        {
            M3uUrlFailover.NormalizeTunerUrls(info);

            var playlist = await new M3uParser(Logger, _httpClientFactory)
                .ParsePlaylist(info, GetFullChannelIdPrefix(info), CancellationToken.None)
                .ConfigureAwait(false);

            ApplyPlaylistMetadata(info, playlist);
        }

        protected override Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(TunerHostInfo tuner, ChannelInfo channel, CancellationToken cancellationToken)
        {
            return Task.FromResult(new List<MediaSourceInfo> { CreateMediaSourceInfo(tuner, channel) });
        }

        protected virtual MediaSourceInfo CreateMediaSourceInfo(TunerHostInfo info, ChannelInfo channel)
        {
            var path = M3uUrlFailover.RewriteStreamUrl(
                channel.Path,
                M3uUrlFailover.GetPrimaryUrl(info));

            // Never advertise DirectPlay. Fire TV would hit the raw IPTV URL (no VLC
            // user-agent) while AutoOpen also holds a server connection — two slots,
            // and the client play usually fails.
            var supportsDirectPlay = false;
            var supportsDirectStream = !info.EnableStreamLooping;

            var protocol = _mediaSourceManager.GetPathProtocol(path);

            var isRemote = true;
            Uri.TryCreate(path, UriKind.Absolute, out var uri);
            if (uri is not null)
            {
                isRemote = !_networkManager.IsInLocalNetwork(uri.Host);
            }

            // A manifest is not a byte stream. Serving one directly hands the client a playlist whose
            // variant and segment URIs are relative to the origin, and those do not resolve against the
            // Jellyfin url the client fetched it from. Remux or transcode these instead.
            if (IsManifest(path, uri))
            {
                supportsDirectPlay = false;
            }

            var httpHeaders = new Dictionary<string, string>();

            if (protocol == MediaProtocol.Http)
            {
                // Use user-defined user-agent. If there isn't one, make it look like a browser.
                httpHeaders[HeaderNames.UserAgent] = string.IsNullOrWhiteSpace(info.UserAgent) ?
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36" :
                    info.UserAgent;

                if (!string.IsNullOrWhiteSpace(info.Referrer))
                {
                    httpHeaders[HeaderNames.Referer] = info.Referrer;
                }
            }

            var mediaSource = new MediaSourceInfo
            {
                Path = path,
                Protocol = protocol,
                Container = InferLiveContainer(path),
                MediaStreams = new MediaStream[]
                {
                    new MediaStream
                    {
                        Type = MediaStreamType.Video,
                        // Set the index to -1 because we don't know the exact index of the video stream within the container
                        Index = -1,
                        IsInterlaced = true
                    },
                    new MediaStream
                    {
                        Type = MediaStreamType.Audio,
                        // Set the index to -1 because we don't know the exact index of the audio stream within the container
                        Index = -1
                    }
                },
                RequiresOpening = true,
                RequiresClosing = true,
                RequiresLooping = info.EnableStreamLooping,
                SupportsProbing = false,

                ReadAtNativeFramerate = info.ReadAtNativeFramerate,

                Id = channel.Path.GetMD5().ToString("N", CultureInfo.InvariantCulture),
                IsInfiniteStream = true,
                IsRemote = isRemote,

                IgnoreDts = info.IgnoreDts,
                GenPtsInput = true,
                AnalyzeDurationMs = 5000,
                BufferMs = 3000,
                SupportsDirectPlay = supportsDirectPlay,
                SupportsDirectStream = supportsDirectStream,

                RequiredHttpHeaders = httpHeaders,
                UseMostCompatibleTranscodingProfile = !info.AllowFmp4TranscodingContainer,
                FallbackMaxStreamingBitrate = info.FallbackMaxStreamingBitrate
            };

            mediaSource.InferTotalBitrate();

            return mediaSource;
        }

        /// <summary>
        /// Determines whether a channel path points at an HLS or DASH manifest rather than at a byte stream.
        /// </summary>
        /// <param name="path">The channel path.</param>
        /// <param name="uri">The channel path parsed as an absolute uri, or <c>null</c> if it is not one.</param>
        /// <returns><c>true</c> if the path names a streaming manifest.</returns>
        private static bool IsManifest(string path, Uri uri)
        {
            // Use the uri path when there is one so that a query string does not hide the extension.
            var extension = Path.GetExtension(uri is null ? path : uri.AbsolutePath);

            return _manifestExtensions.Contains(extension, StringComparison.OrdinalIgnoreCase);
        }

        public Task<List<TunerHostInfo>> DiscoverDevices(int discoveryDurationMs, CancellationToken cancellationToken)
        {
            return Task.FromResult(new List<TunerHostInfo>());
        }

        private static void ApplyPlaylistMetadata(TunerHostInfo info, M3uPlaylist playlist)
        {
            if (string.IsNullOrWhiteSpace(info.UserAgent) && !string.IsNullOrWhiteSpace(playlist.UserAgent))
            {
                info.UserAgent = playlist.UserAgent;
            }

            if (string.IsNullOrWhiteSpace(info.Referrer) && !string.IsNullOrWhiteSpace(playlist.Referrer))
            {
                info.Referrer = playlist.Referrer;
            }

            if (string.IsNullOrWhiteSpace(info.EpgUrl) && !string.IsNullOrWhiteSpace(playlist.EpgUrl))
            {
                info.EpgUrl = playlist.EpgUrl;
            }
        }

        private static string InferLiveContainer(string path)
        {
            if (!Uri.TryCreate(path, UriKind.Absolute, out var uri))
            {
                return null;
            }

            var relativePath = uri.AbsolutePath;
            if (relativePath.Contains("m3u8", StringComparison.OrdinalIgnoreCase)
                || relativePath.Contains("/hls", StringComparison.OrdinalIgnoreCase))
            {
                return "hls";
            }

            if (relativePath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
                || relativePath.EndsWith(".m2t", StringComparison.OrdinalIgnoreCase)
                || relativePath.EndsWith(".mp2t", StringComparison.OrdinalIgnoreCase)
                || relativePath.Contains("mpegts", StringComparison.OrdinalIgnoreCase))
            {
                return "mpegts";
            }

            return null;
        }
    }
}
