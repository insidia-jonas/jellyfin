using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.LiveTv.Channels;
using Jellyfin.LiveTv.Tests;
using Jellyfin.LiveTv.TunerHosts;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.LiveTv.Tests.TunerHosts;

[Collection(LiveTvChannelSetIdentityCollection.Name)]
public sealed class BaseTunerHostListingCacheTests
{
    private readonly string _cachePath = Path.Combine(Path.GetTempPath(), "jf-livetv-cache-" + Guid.NewGuid().ToString("N"));

    public BaseTunerHostListingCacheTests()
    {
        Directory.CreateDirectory(_cachePath);
        LiveTvChannelSetIdentity.Reset();
    }

    [Fact]
    public async Task GetChannels_ServesSnapshotWhileRefreshInFlight()
    {
        var tuner = Tuner();
        var host = CreateHost(tuner);
        host.EnableBackgroundListingRefresh = true;
        host.SeedListingSnapshot(tuner.Id, FreshSnapshot(tuner.Url, stale: true));

        host.BlockNextRefresh();
        var immediate = await host.GetChannels(tuner, true, CancellationToken.None);
        Assert.Equal("old", Assert.Single(immediate).Id);
        await host.EnteredRefresh.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        var servedDuringRefresh = await host.GetChannels(tuner, true, CancellationToken.None);
        Assert.Equal("old", Assert.Single(servedDuringRefresh).Id);
        Assert.Equal(0, host.CompletedRefreshes);

        host.ReleaseRefresh.TrySetResult();
        await host.CompletedRefresh.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(1, host.CompletedRefreshes);
    }

    [Fact]
    public async Task GetChannels_AfterFailedRefresh_KeepsLastGoodSnapshot()
    {
        var tuner = Tuner();
        var host = CreateHost(tuner);
        host.EnableBackgroundListingRefresh = false;
        host.SeedListingSnapshot(tuner.Id, FreshSnapshot(tuner.Url, stale: false));
        host.FailNextRefresh = true;

        var served = await host.GetChannels(tuner, false, CancellationToken.None);

        Assert.Equal("old", Assert.Single(served).Id);
        Assert.Equal(1, host.CompletedRefreshes);
    }

    [Fact]
    public async Task GetCachedChannels_DoesNotWaitForPlaylistHttp()
    {
        var tuner = Tuner();
        var host = CreateHost(tuner);
        host.EnableBackgroundListingRefresh = true;
        host.SeedListingSnapshot(tuner.Id, FreshSnapshot(tuner.Url, stale: true));
        host.BlockNextRefresh();

        var started = DateTime.UtcNow;
        var served = host.GetCachedChannels();
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1));
        Assert.Equal("old", Assert.Single(served).Id);
        Assert.Equal(0, host.CompletedRefreshes);

        host.ReleaseRefresh.TrySetResult();
        await host.CompletedRefresh.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void GetCachedChannels_ColdStart_ReturnsEmptyWithoutWaiting()
    {
        var tuner = Tuner();
        var host = CreateHost(tuner);
        host.EnableBackgroundListingRefresh = false;
        host.BlockNextRefresh();

        var started = DateTime.UtcNow;
        var served = host.GetCachedChannels();
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1));
        Assert.Empty(served);
        Assert.Equal(0, host.CompletedRefreshes);
        Assert.False(host.IsListingRefreshInFlight);
    }

    [Fact]
    public async Task GetCachedChannels_ColdStart_SchedulesFetchAndEventuallyHasChannels()
    {
        var tuner = Tuner();
        var host = CreateHost(tuner);
        host.EnableBackgroundListingRefresh = true;
        host.FirstLoadWait = TimeSpan.Zero;
        host.BlockNextRefresh();

        var started = DateTime.UtcNow;
        var first = host.GetCachedChannels();
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1));
        Assert.Empty(first);
        Assert.True(host.IsListingRefreshInFlight);

        host.ReleaseRefresh.TrySetResult();
        await host.CompletedRefresh.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        var second = host.GetCachedChannels();
        Assert.Equal("new", Assert.Single(second).Id);
        Assert.Equal(1, host.CompletedRefreshes);
    }

    [Fact]
    public async Task GetChannels_CacheHit_DoesNotCallRefresh()
    {
        var tuner = Tuner();
        var host = CreateHost(tuner);
        host.EnableBackgroundListingRefresh = false;
        host.SeedListingSnapshot(tuner.Id, FreshSnapshot(tuner.Url, stale: false));

        var first = await host.GetChannels(tuner, true, CancellationToken.None);
        var second = await host.GetChannels(tuner, true, CancellationToken.None);

        Assert.Equal("old", Assert.Single(first).Id);
        Assert.Equal("old", Assert.Single(second).Id);
        Assert.Equal(0, host.CompletedRefreshes);
    }

    private TestTunerHost CreateHost(TunerHostInfo tuner)
    {
        var paths = new Mock<IServerApplicationPaths>();
        paths.Setup(p => p.CachePath).Returns(_cachePath);

        var config = new Mock<IServerConfigurationManager>();
        config.Setup(c => c.ApplicationPaths).Returns(paths.Object);
        config.Setup(c => c.GetConfiguration("livetv")).Returns(new LiveTvOptions { TunerHosts = [tuner] });

        return new TestTunerHost(config.Object);
    }

    private static TunerHostInfo Tuner() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Type = "test",
        Url = "https://cdn.example/playlist.m3u"
    };

    private static M3uListingSnapshot FreshSnapshot(string url, bool stale)
        => new()
        {
            Channels = [new ChannelInfo { Id = "old", Name = "Old" }],
            PlaylistUrl = url,
            FetchedUtc = DateTime.UtcNow.AddMinutes(stale ? -90 : -1)
        };

    private sealed class TestTunerHost : BaseTunerHost
    {
        public TestTunerHost(IServerConfigurationManager config)
            : base(config, NullLogger<BaseTunerHost>.Instance, Mock.Of<IFileSystem>())
        {
        }

        public override string Type => "test";

        public int CompletedRefreshes { get; private set; }

        public bool FailNextRefresh { get; set; }

        public TaskCompletionSource EnteredRefresh { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseRefresh { get; private set; } = CompletedSource();

        public TaskCompletionSource CompletedRefresh { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void BlockNextRefresh()
        {
            EnteredRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ReleaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CompletedRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
            EnteredRefresh.TrySetResult();
            await ReleaseRefresh.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            CompletedRefreshes++;
            CompletedRefresh.TrySetResult();
            if (FailNextRefresh)
            {
                throw new InvalidOperationException("forced listing failure");
            }

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
