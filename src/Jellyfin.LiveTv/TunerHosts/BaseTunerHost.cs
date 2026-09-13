#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.LiveTv.Channels;
using Jellyfin.LiveTv.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.TunerHosts
{
    public abstract class BaseTunerHost
    {
        private readonly ConcurrentDictionary<string, M3uListingSnapshot> _listingSnapshots;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshGates;
        private readonly ConcurrentDictionary<string, byte> _backgroundRefresh;

        protected BaseTunerHost(IServerConfigurationManager config, ILogger<BaseTunerHost> logger, IFileSystem fileSystem)
        {
            Config = config;
            Logger = logger;
            FileSystem = fileSystem;
            _listingSnapshots = new ConcurrentDictionary<string, M3uListingSnapshot>(StringComparer.OrdinalIgnoreCase);
            _refreshGates = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
            _backgroundRefresh = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            EnableBackgroundListingRefresh = true;
        }

        protected IServerConfigurationManager Config { get; }

        protected ILogger<BaseTunerHost> Logger { get; }

        protected IFileSystem FileSystem { get; }

        public virtual bool IsSupported => true;

        public abstract string Type { get; }

        protected virtual string ChannelIdPrefix => Type + "_";

        /// <summary>
        /// Gets or sets a value indicating whether stale snapshots refresh on a background task.
        /// Tests disable this so listing HTTP can be asserted.
        /// </summary>
        internal bool EnableBackgroundListingRefresh { get; set; }

        /// <summary>
        /// Clears the in-memory channel list cache, optionally for one tuner.
        /// Disk snapshots stay so the next open can still serve last-good channels.
        /// </summary>
        /// <param name="tunerId">The tuner id, or <c>null</c> to clear every cached list.</param>
        public void ClearChannelCache(string tunerId = null)
        {
            if (string.IsNullOrEmpty(tunerId))
            {
                _listingSnapshots.Clear();
                return;
            }

            _listingSnapshots.TryRemove(tunerId, out _);
        }

        protected abstract Task<List<ChannelInfo>> GetChannelsInternal(TunerHostInfo tuner, CancellationToken cancellationToken);

        /// <summary>
        /// Refreshes one tuner listing. M3U hosts send a conditional GET on the playlist URL only.
        /// </summary>
        /// <param name="tuner">The tuner.</param>
        /// <param name="previous">The last-good snapshot, if any.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The refresh outcome.</returns>
        internal virtual async Task<M3uListingRefreshResult> RefreshListingAsync(
            TunerHostInfo tuner,
            M3uListingSnapshot previous,
            CancellationToken cancellationToken)
        {
            var list = await GetChannelsInternal(tuner, cancellationToken).ConfigureAwait(false);
            return new M3uListingRefreshResult
            {
                Channels = list ?? [],
                PlaylistUrl = previous?.PlaylistUrl ?? tuner?.Url
            };
        }

        public async Task<List<ChannelInfo>> GetChannels(TunerHostInfo tuner, bool enableCache, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(tuner);

            var snapshot = GetOrLoadSnapshot(tuner);
            if (enableCache && HasChannels(snapshot))
            {
                ScheduleBackgroundRefreshIfNeeded(tuner, snapshot);
                return snapshot.Channels;
            }

            return await RefreshTunerLockedAsync(tuner, enableCache, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Loads disk snapshots into memory and starts a background refresh when the listing is stale.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that loads local snapshots (network refresh is not awaited).</returns>
        public async Task WarmAllAsync(CancellationToken cancellationToken)
        {
            foreach (var tuner in GetTunerHosts())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await GetChannels(tuner, true, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Live TV listing warmup failed for tuner {TunerId}", tuner.Id);
                }
            }
        }

        /// <summary>
        /// Stores a listing snapshot after tuner save/validate so the next UI open is not a cold download.
        /// </summary>
        /// <param name="tuner">The tuner (must have an id).</param>
        /// <param name="channels">Parsed channels.</param>
        /// <param name="etag">Playlist ETag.</param>
        /// <param name="lastModified">Playlist Last-Modified.</param>
        /// <param name="playlistUrl">The listing URL.</param>
        internal void AcceptListingSnapshot(
            TunerHostInfo tuner,
            List<ChannelInfo> channels,
            string etag,
            DateTimeOffset? lastModified,
            string playlistUrl)
        {
            ArgumentNullException.ThrowIfNull(tuner);
            if (string.IsNullOrEmpty(tuner.Id) || channels is null || channels.Count == 0)
            {
                return;
            }

            var snapshot = new M3uListingSnapshot
            {
                Channels = channels,
                ETag = etag,
                LastModified = lastModified,
                FetchedUtc = DateTime.UtcNow,
                PlaylistUrl = playlistUrl
            };
            StoreSnapshot(tuner, snapshot);
        }

        internal M3uListingSnapshot PeekListingSnapshot(string tunerId)
        {
            if (string.IsNullOrEmpty(tunerId))
            {
                return null;
            }

            _listingSnapshots.TryGetValue(tunerId, out var snapshot);
            return snapshot;
        }

        internal void SeedListingSnapshot(string tunerId, M3uListingSnapshot snapshot)
        {
            ArgumentException.ThrowIfNullOrEmpty(tunerId);
            ArgumentNullException.ThrowIfNull(snapshot);
            _listingSnapshots[tunerId] = snapshot;
            LiveTvChannelSetIdentity.ReplaceFromChannels(tunerId, snapshot.Channels ?? []);
        }

        protected virtual IList<TunerHostInfo> GetTunerHosts()
        {
            return Config.GetLiveTvConfiguration().TunerHosts
                .Where(i => string.Equals(i.Type, Type, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        public async Task<List<ChannelInfo>> GetChannels(bool enableCache, CancellationToken cancellationToken)
        {
            var list = new List<ChannelInfo>();

            var hosts = GetTunerHosts();

            foreach (var host in hosts)
            {
                try
                {
                    var channels = await GetChannels(host, enableCache, cancellationToken).ConfigureAwait(false);
                    var newChannels = channels.Where(i => !list.Any(l => string.Equals(i.Id, l.Id, StringComparison.OrdinalIgnoreCase))).ToList();

                    list.AddRange(newChannels);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error getting channel list");

                    var fallback = GetOrLoadSnapshot(host);
                    if (HasChannels(fallback))
                    {
                        Logger.LogWarning("Serving last-good Live TV snapshot for tuner {TunerId} after listing failure", host.Id);
                        list.AddRange(fallback.Channels.Where(i => !list.Any(l => string.Equals(i.Id, l.Id, StringComparison.OrdinalIgnoreCase))));
                    }
                }
            }

            return list;
        }

        /// <summary>
        /// Returns last-good listings without waiting for a playlist GET.
        /// Missing tuners start a background refresh so the Items request can return.
        /// </summary>
        /// <returns>Cached channels, or an empty list when no snapshot exists yet.</returns>
        public List<ChannelInfo> GetCachedChannels()
        {
            var list = new List<ChannelInfo>();

            foreach (var host in GetTunerHosts())
            {
                try
                {
                    var snapshot = GetOrLoadSnapshot(host);
                    if (HasChannels(snapshot))
                    {
                        ScheduleBackgroundRefreshIfNeeded(host, snapshot);
                        list.AddRange(snapshot.Channels.Where(i => !list.Any(l => string.Equals(i.Id, l.Id, StringComparison.OrdinalIgnoreCase))));
                    }
                    else
                    {
                        ScheduleColdRefresh(host);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Error reading cached Live TV channels");
                }
            }

            return list;
        }

        private void ScheduleColdRefresh(TunerHostInfo tuner)
        {
            if (!EnableBackgroundListingRefresh || tuner is null || string.IsNullOrEmpty(tuner.Id))
            {
                return;
            }

            if (!_backgroundRefresh.TryAdd(tuner.Id, 0))
            {
                return;
            }

            _ = Task.Run(() => RefreshInBackgroundAsync(tuner), CancellationToken.None);
        }

        private async Task<List<ChannelInfo>> RefreshTunerLockedAsync(TunerHostInfo tuner, bool enableCache, CancellationToken cancellationToken)
        {
            var key = SnapshotKey(tuner);
            var gate = _refreshGates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var snapshot = GetOrLoadSnapshot(tuner);
                if (enableCache && HasChannels(snapshot))
                {
                    ScheduleBackgroundRefreshIfNeeded(tuner, snapshot);
                    return snapshot.Channels;
                }

                return await RefreshTunerCoreAsync(tuner, snapshot, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task<List<ChannelInfo>> RefreshTunerCoreAsync(
            TunerHostInfo tuner,
            M3uListingSnapshot previous,
            CancellationToken cancellationToken)
        {
            try
            {
                var refreshed = await RefreshListingAsync(tuner, previous, cancellationToken).ConfigureAwait(false);
                if (refreshed.NotModified && HasChannels(previous))
                {
                    previous.FetchedUtc = DateTime.UtcNow;
                    if (!string.IsNullOrWhiteSpace(refreshed.ETag))
                    {
                        previous.ETag = refreshed.ETag;
                    }

                    if (refreshed.LastModified.HasValue)
                    {
                        previous.LastModified = refreshed.LastModified;
                    }

                    StoreSnapshot(tuner, previous);
                    return previous.Channels;
                }

                var channels = refreshed.Channels ?? [];
                if (channels.Count == 0 && HasChannels(previous))
                {
                    Logger.LogWarning("Live TV listing refresh returned no channels for tuner {TunerId}; keeping last-good snapshot", tuner.Id);
                    return previous.Channels;
                }

                var snapshot = new M3uListingSnapshot
                {
                    Channels = channels,
                    ETag = refreshed.ETag ?? previous?.ETag,
                    LastModified = refreshed.LastModified ?? previous?.LastModified,
                    FetchedUtc = DateTime.UtcNow,
                    PlaylistUrl = refreshed.PlaylistUrl ?? previous?.PlaylistUrl
                };
                StoreSnapshot(tuner, snapshot);
                return channels;
            }
            catch (Exception ex)
            {
                if (HasChannels(previous))
                {
                    Logger.LogWarning(ex, "Live TV listing refresh failed for tuner {TunerId}; serving last-good snapshot", tuner.Id);
                    return previous.Channels;
                }

                throw;
            }
        }

        private void ScheduleBackgroundRefreshIfNeeded(TunerHostInfo tuner, M3uListingSnapshot snapshot)
        {
            if (!EnableBackgroundListingRefresh || tuner is null || string.IsNullOrEmpty(tuner.Id))
            {
                return;
            }

            var playlistUrl = CurrentPlaylistUrl(tuner);
            if (!M3uListingRefreshPolicy.ShouldRefreshListing(snapshot.FetchedUtc, DateTime.UtcNow, snapshot.PlaylistUrl, playlistUrl))
            {
                return;
            }

            if (!_backgroundRefresh.TryAdd(tuner.Id, 0))
            {
                return;
            }

            _ = Task.Run(() => RefreshInBackgroundAsync(tuner), CancellationToken.None);
        }

        private async Task RefreshInBackgroundAsync(TunerHostInfo tuner)
        {
            try
            {
                await RefreshTunerLockedAsync(tuner, enableCache: false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Background Live TV listing refresh failed for tuner {TunerId}", tuner.Id);
            }
            finally
            {
                _backgroundRefresh.TryRemove(tuner.Id, out _);
            }
        }

        private M3uListingSnapshot GetOrLoadSnapshot(TunerHostInfo tuner)
        {
            var key = SnapshotKey(tuner);
            if (!string.IsNullOrEmpty(key) && _listingSnapshots.TryGetValue(key, out var memory) && HasChannels(memory))
            {
                return memory;
            }

            var disk = LoadDiskSnapshot(tuner);
            if (HasChannels(disk) && !string.IsNullOrEmpty(key))
            {
                _listingSnapshots[key] = disk;
                LiveTvChannelSetIdentity.ReplaceFromChannels(key, disk.Channels);
                return disk;
            }

            return disk;
        }

        private void StoreSnapshot(TunerHostInfo tuner, M3uListingSnapshot snapshot)
        {
            var key = SnapshotKey(tuner);
            if (string.IsNullOrEmpty(key) || snapshot is null)
            {
                return;
            }

            _listingSnapshots[key] = snapshot;
            LiveTvChannelSetIdentity.ReplaceFromChannels(key, snapshot.Channels ?? []);
            PersistDiskSnapshot(tuner, snapshot);
        }

        private M3uListingSnapshot LoadDiskSnapshot(TunerHostInfo tuner)
        {
            var path = ChannelCacheFile(tuner);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                using var readStream = AsyncFile.OpenRead(path);
                using var document = JsonDocument.Parse(readStream);
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var channels = JsonSerializer.Deserialize<List<ChannelInfo>>(document.RootElement.GetRawText());
                    if (channels is { Count: > 0 })
                    {
                        return new M3uListingSnapshot
                        {
                            Channels = channels,
                            PlaylistUrl = CurrentPlaylistUrl(tuner)
                        };
                    }

                    return null;
                }

                return JsonSerializer.Deserialize<M3uListingSnapshot>(document.RootElement.GetRawText());
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                Logger.LogDebug(ex, "Could not read Live TV channel snapshot {Path}", path);
                return null;
            }
        }

        private void PersistDiskSnapshot(TunerHostInfo tuner, M3uListingSnapshot snapshot)
        {
            var path = ChannelCacheFile(tuner);
            if (string.IsNullOrEmpty(path) || snapshot is null || !HasChannels(snapshot))
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using var writeStream = AsyncFile.Create(path);
                JsonSerializer.Serialize(writeStream, snapshot);
            }
            catch (IOException ex)
            {
                Logger.LogDebug(ex, "Could not write Live TV channel snapshot {Path}", path);
            }
        }

        private string ChannelCacheFile(TunerHostInfo tuner)
        {
            if (tuner is null || string.IsNullOrEmpty(tuner.Id) || Config?.ApplicationPaths?.CachePath is null)
            {
                return null;
            }

            return Path.Combine(Config.ApplicationPaths.CachePath, tuner.Id + "_channels");
        }

        private static string SnapshotKey(TunerHostInfo tuner)
            => string.IsNullOrEmpty(tuner?.Id) ? tuner?.Url ?? "pending" : tuner.Id;

        private static string CurrentPlaylistUrl(TunerHostInfo tuner)
        {
            if (tuner is null)
            {
                return null;
            }

            try
            {
                return M3uUrlFailover.GetPlaylistUrl(tuner);
            }
            catch (Exception)
            {
                return tuner.Url;
            }
        }

        private static bool HasChannels(M3uListingSnapshot snapshot)
            => snapshot?.Channels is { Count: > 0 };

        protected abstract Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(TunerHostInfo tuner, ChannelInfo channel, CancellationToken cancellationToken);

        public async Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrEmpty(channelId);

            if (IsValidChannelId(channelId))
            {
                var hosts = GetTunerHosts();

                foreach (var host in hosts)
                {
                    try
                    {
                        var channels = await GetChannels(host, true, cancellationToken).ConfigureAwait(false);
                        var channelInfo = channels.FirstOrDefault(i => string.Equals(i.Id, channelId, StringComparison.OrdinalIgnoreCase));

                        if (channelInfo is not null)
                        {
                            return await GetChannelStreamMediaSources(host, channelInfo, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Error getting channels");
                    }
                }
            }

            return new List<MediaSourceInfo>();
        }

        protected abstract Task<ILiveStream> GetChannelStream(TunerHostInfo tunerHost, ChannelInfo channel, string streamId, IList<ILiveStream> currentLiveStreams, CancellationToken cancellationToken);

        public async Task<ILiveStream> GetChannelStream(string channelId, string streamId, IList<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrEmpty(channelId);

            if (!IsValidChannelId(channelId))
            {
                throw new FileNotFoundException();
            }

            var hosts = GetTunerHosts();

            var hostsWithChannel = new List<Tuple<TunerHostInfo, ChannelInfo>>();

            foreach (var host in hosts)
            {
                try
                {
                    var channels = await GetChannels(host, true, cancellationToken).ConfigureAwait(false);
                    var channelInfo = channels.FirstOrDefault(i => string.Equals(i.Id, channelId, StringComparison.OrdinalIgnoreCase));

                    if (channelInfo is not null)
                    {
                        hostsWithChannel.Add(new Tuple<TunerHostInfo, ChannelInfo>(host, channelInfo));
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error getting channels");
                }
            }

            foreach (var hostTuple in hostsWithChannel)
            {
                var host = hostTuple.Item1;
                var channelInfo = hostTuple.Item2;

                try
                {
                    var liveStream = await GetChannelStream(host, channelInfo, streamId, currentLiveStreams, cancellationToken).ConfigureAwait(false);
                    var startTime = DateTime.UtcNow;
                    await liveStream.Open(cancellationToken).ConfigureAwait(false);
                    var endTime = DateTime.UtcNow;
                    Logger.LogInformation("Live stream opened after {0}ms", (endTime - startTime).TotalMilliseconds);
                    return liveStream;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error opening tuner");
                }
            }

            throw new LiveTvConflictException("Unable to find host to play channel");
        }

        protected virtual bool IsValidChannelId(string channelId)
        {
            ArgumentException.ThrowIfNullOrEmpty(channelId);

            return channelId.StartsWith(ChannelIdPrefix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
