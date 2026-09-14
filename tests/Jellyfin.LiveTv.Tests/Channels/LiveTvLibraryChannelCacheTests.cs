using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.LiveTv.Channels;
using Jellyfin.LiveTv.Tests;
using Jellyfin.LiveTv.TunerHosts;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.LiveTv.Tests.Channels;

[Collection(LiveTvChannelSetIdentityCollection.Name)]
public sealed class LiveTvLibraryChannelCacheTests
{
    public LiveTvLibraryChannelCacheTests()
    {
        LiveTvChannelSetIdentity.Reset();
    }

    [Fact]
    public void GetCacheKey_IsStableForSameChannelSet()
    {
        LiveTvChannelSetIdentity.Replace("t1", ["a", "b"]);
        var channel = CreateChannel(Mock.Of<ITunerHostManager>(), Mock.Of<ILibraryManager>());

        var first = channel.GetCacheKey("user");
        var second = channel.GetCacheKey("user");

        Assert.Equal(first, second);
        Assert.Equal(64, first!.Length);
    }

    [Fact]
    public void GetCacheKey_ChangesOnlyWhenChannelIdsChange()
    {
        LiveTvChannelSetIdentity.Replace("t1", ["a"]);
        var channel = CreateChannel(Mock.Of<ITunerHostManager>(), Mock.Of<ILibraryManager>());
        var before = channel.GetCacheKey(null);

        LiveTvChannelSetIdentity.Replace("t1", ["a", "b"]);
        var after = channel.GetCacheKey(null);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task GetChannelItems_Snapshot_DoesNotAwaitPlaylistHttp()
    {
        var cachePath = Path.Combine(Path.GetTempPath(), "jf-livetv-hang-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cachePath);
        var tuner = new TunerHostInfo
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = "test",
            Url = "https://cdn.example/playlist.m3u"
        };
        var host = CreateBlockedHost(cachePath, tuner);
        host.SeedListingSnapshot(tuner.Id, new M3uListingSnapshot
        {
            Channels = [new ChannelInfo { Id = "old", Name = "Old", ChannelGroup = "News" }],
            PlaylistUrl = tuner.Url,
            FetchedUtc = DateTime.UtcNow.AddMinutes(-90)
        });
        host.BlockNextRefresh();

        var manager = new Mock<ITunerHostManager>();
        manager.Setup(m => m.TunerHosts).Returns([host]);
        var channel = CreateChannel(manager.Object, Mock.Of<ILibraryManager>());

        var clock = Stopwatch.StartNew();
        var result = await channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Contains(result.Items, item => item.Name == "News" || item.Id.Contains("News", StringComparison.Ordinal));
        Assert.Equal(0, host.CompletedRefreshes);
        host.ReleaseRefresh.TrySetResult();
    }

    [Fact]
    public async Task GetChannelItems_ColdStart_DoesNotAwaitPlaylistHttp()
    {
        var cachePath = Path.Combine(Path.GetTempPath(), "jf-livetv-cold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cachePath);
        var tuner = new TunerHostInfo
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = "test",
            Url = "https://cdn.example/playlist.m3u"
        };
        var host = CreateBlockedHost(cachePath, tuner);
        host.BlockNextRefresh();

        var manager = new Mock<ITunerHostManager>();
        manager.Setup(m => m.TunerHosts).Returns([host]);
        var channel = CreateChannel(manager.Object, Mock.Of<ILibraryManager>());

        var clock = Stopwatch.StartNew();
        var result = await channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Empty(result.Items);
        Assert.Equal(0, host.CompletedRefreshes);
    }

    [Fact]
    public async Task GetChannelItems_GroupFolder_FiltersSnapshotWithoutPlaylistHttp()
    {
        var cachePath = Path.Combine(Path.GetTempPath(), "jf-livetv-group-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cachePath);
        var tuner = new TunerHostInfo
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = "test",
            Url = "https://cdn.example/playlist.m3u"
        };
        var host = CreateBlockedHost(cachePath, tuner);
        host.SeedListingSnapshot(tuner.Id, new M3uListingSnapshot
        {
            Channels =
            [
                new ChannelInfo { Id = "cnn", Name = "CNN", ChannelGroup = "News" },
                new ChannelInfo { Id = "film", Name = "Film", ChannelGroup = "Movies" }
            ],
            PlaylistUrl = tuner.Url,
            FetchedUtc = DateTime.UtcNow.AddMinutes(-20)
        });
        host.BlockNextRefresh();

        var manager = new Mock<ITunerHostManager>();
        manager.Setup(m => m.TunerHosts).Returns([host]);
        var channel = CreateChannel(manager.Object, Mock.Of<ILibraryManager>());

        var clock = Stopwatch.StartNew();
        var group = await channel.GetChannelItems(
            new InternalChannelItemQuery { FolderId = LiveTvLibraryChannelItems.EncodeGroupId("News") },
            CancellationToken.None);
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal("CNN", Assert.Single(group.Items).Name);
        Assert.False(group.RefreshPending);
        Assert.Equal(0, host.CompletedRefreshes);
        Assert.True(ChannelManagerBrowse.IsLiveTvGroupFolderId(LiveTvLibraryChannelItems.EncodeGroupId("News")));
        host.ReleaseRefresh.TrySetResult();
    }

    [Fact]
    public async Task GetChannelItems_GroupFolder_ColdStart_CompletesEmptyWithoutPending()
    {
        var cachePath = Path.Combine(Path.GetTempPath(), "jf-livetv-group-cold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cachePath);
        var tuner = new TunerHostInfo
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = "test",
            Url = "https://cdn.example/playlist.m3u"
        };
        var host = CreateBlockedHost(cachePath, tuner);
        host.BlockNextRefresh();

        var manager = new Mock<ITunerHostManager>();
        manager.Setup(m => m.TunerHosts).Returns([host]);
        var channel = CreateChannel(manager.Object, Mock.Of<ILibraryManager>());

        var clock = Stopwatch.StartNew();
        var group = await channel.GetChannelItems(
            new InternalChannelItemQuery { FolderId = LiveTvLibraryChannelItems.EncodeGroupId("Sports") },
            CancellationToken.None);
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Empty(group.Items);
        Assert.False(group.RefreshPending);
        Assert.Equal(0, host.CompletedRefreshes);
        host.ReleaseRefresh.TrySetResult();
    }

    [Fact]
    public async Task GetChannelItems_IgnoresNonSnapshotTunerHosts()
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new Mock<ITunerHost>();
        host.Setup(h => h.GetChannels(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await blocked.Task.WaitAsync(CancellationToken.None).ConfigureAwait(true);
                return new List<ChannelInfo> { new() { Id = "late", Name = "Late" } };
            });

        var manager = new Mock<ITunerHostManager>();
        manager.Setup(m => m.TunerHosts).Returns([host.Object]);
        var channel = CreateChannel(manager.Object, Mock.Of<ILibraryManager>());

        var clock = Stopwatch.StartNew();
        var result = await channel.GetChannelItems(
            new InternalChannelItemQuery { FolderId = LiveTvLibraryChannelItems.EncodeGroupId("News") },
            CancellationToken.None);
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Empty(result.Items);
        Assert.False(result.RefreshPending);
        host.Verify(h => h.GetChannels(It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        blocked.TrySetResult();
    }

    [Fact]
    public void OverlayPresentation_WritesNowNextWithoutChangingSenderName()
    {
        var channelId = Guid.NewGuid();
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(query =>
            {
                if (query.IncludeItemTypes is { Length: > 0 }
                    && query.IncludeItemTypes[0] == Jellyfin.Data.Enums.BaseItemKind.LiveTvChannel)
                {
                    return [new LiveTvChannel { Id = channelId, ExternalId = "cnn" }];
                }

                return
                [
                    new LiveTvProgram
                    {
                        ChannelId = channelId,
                        Name = "Tagesschau",
                        StartDate = DateTime.UtcNow.AddMinutes(-5),
                        EndDate = DateTime.UtcNow.AddMinutes(10)
                    }
                ];
            });

        var channel = CreateChannel(Mock.Of<ITunerHostManager>(), library.Object);
        var item = new Video
        {
            Name = "Das Erste",
            ExternalId = "cnn"
        };

        channel.OverlayPresentation([item]);

        Assert.Equal("Das Erste", item.Name);
        Assert.Contains("Tagesschau", item.OriginalTitle, StringComparison.Ordinal);
        Assert.Contains("Tagesschau", item.Overview, StringComparison.Ordinal);
        Assert.Equal("Tagesschau", item.ProviderIds[LiveTvLibraryChannelItems.ProviderNowKey]);
    }

    private static LiveTvLibraryChannel CreateChannel(ITunerHostManager tuners, ILibraryManager library)
        => new(tuners, library, Mock.Of<IUserManager>(), NullLogger<LiveTvLibraryChannel>.Instance);

    private static HangTunerHost CreateBlockedHost(string cachePath, TunerHostInfo tuner)
    {
        var paths = new Mock<IServerApplicationPaths>();
        paths.Setup(p => p.CachePath).Returns(cachePath);
        var config = new Mock<IServerConfigurationManager>();
        config.Setup(c => c.ApplicationPaths).Returns(paths.Object);
        config.Setup(c => c.GetConfiguration("livetv")).Returns(new LiveTvOptions { TunerHosts = [tuner] });
        return new HangTunerHost(config.Object);
    }

    private sealed class HangTunerHost : BaseTunerHost, ITunerHost
    {
        public HangTunerHost(IServerConfigurationManager config)
            : base(config, NullLogger<BaseTunerHost>.Instance, Mock.Of<IFileSystem>())
        {
        }

        public string Name => "test";

        public override string Type => "test";

        public int CompletedRefreshes { get; private set; }

        public TaskCompletionSource ReleaseRefresh { get; private set; } = CompletedSource();

        public Task<List<TunerHostInfo>> DiscoverDevices(int discoveryDurationMs, CancellationToken cancellationToken)
            => Task.FromResult(new List<TunerHostInfo>());

        public void BlockNextRefresh()
        {
            ReleaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static TaskCompletionSource CompletedSource()
        {
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.TrySetResult();
            return source;
        }

        protected override Task<List<ChannelInfo>> GetChannelsInternal(TunerHostInfo tuner, CancellationToken cancellationToken)
            => Task.FromResult(new List<ChannelInfo>());

        internal override async Task<M3uListingRefreshResult> RefreshListingAsync(
            TunerHostInfo tuner,
            M3uListingSnapshot previous,
            CancellationToken cancellationToken)
        {
            await ReleaseRefresh.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            CompletedRefreshes++;
            return new M3uListingRefreshResult
            {
                Channels = [new ChannelInfo { Id = "new", Name = "New" }],
                PlaylistUrl = tuner.Url
            };
        }

        protected override Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(TunerHostInfo tuner, ChannelInfo channel, CancellationToken cancellationToken)
            => Task.FromResult(new List<MediaSourceInfo>());

        protected override Task<ILiveStream> GetChannelStream(TunerHostInfo tunerHost, ChannelInfo channel, string streamId, IList<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
