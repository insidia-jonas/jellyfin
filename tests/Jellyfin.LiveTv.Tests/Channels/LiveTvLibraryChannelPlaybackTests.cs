using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.LiveTv.Channels;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using Moq;
using Xunit;

namespace Jellyfin.LiveTv.Tests.Channels;

public class LiveTvLibraryChannelPlaybackTests
{
    [Fact]
    public void PrepareMediaSources_SetsOpenTokenWhenMissing()
    {
        var sources = new List<MediaSourceInfo>
        {
            new() { Id = "src", RequiresOpening = true }
        };

        var prepared = LiveTvLibraryChannelPlayback.PrepareMediaSources(sources, "m3u_channel1");

        Assert.Equal("m3u_channel1", Assert.Single(prepared).OpenToken);
        Assert.True(sources[0].RequiresOpening);
    }

    [Fact]
    public void PrepareMediaSources_RefusesDirectPlay()
    {
        // German IPTV ships mpeg2video + mp2, which neither a browser nor Fire TV
        // hardware decodes; a direct play would also drop the VLC user agent the
        // provider expects. Every source has to go through the server.
        var sources = new List<MediaSourceInfo>
        {
            new() { Id = "src", SupportsDirectPlay = true },
            new() { Id = "src2", OpenToken = "already", SupportsDirectPlay = true }
        };

        var prepared = LiveTvLibraryChannelPlayback.PrepareMediaSources(sources, "m3u_channel1");

        Assert.All(prepared, source => Assert.False(source.SupportsDirectPlay));
        Assert.Equal("already", prepared[1].OpenToken);
    }

    [Fact]
    public void EnsureLiveStreamId_IdentifiesOpeningInsteadOfStableMediaSource()
    {
        var source = new MediaSourceInfo { Id = "src-md5" };
        var stream = Mock.Of<ILiveStream>(s => s.MediaSource == source && s.UniqueId == "opening-1");

        LiveTvLibraryChannelPlayback.EnsureLiveStreamId(stream);

        Assert.Equal("opening-1", source.LiveStreamId);
        var reopened = new MediaSourceInfo { Id = source.Id };
        LiveTvLibraryChannelPlayback.EnsureLiveStreamId(Mock.Of<ILiveStream>(s => s.MediaSource == reopened && s.UniqueId == "opening-2"));
        Assert.Equal("opening-2", reopened.LiveStreamId);
    }

    [Fact]
    public async Task OpenAsync_UsesMatchingTunerHost()
    {
        var source = new MediaSourceInfo { Id = "src1" };
        var expected = Mock.Of<ILiveStream>(stream => stream.MediaSource == source && stream.UniqueId == "opening-1");
        var hdhr = new Mock<ITunerHost>();
        hdhr.Setup(host => host.GetChannelStream(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IList<ILiveStream>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FileNotFoundException());

        var m3u = new Mock<ITunerHost>();
        m3u.Setup(host => host.GetChannelStream(
                "m3u_channel1",
                "m3u_channel1",
                It.IsAny<IList<ILiveStream>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var opened = await LiveTvLibraryChannelPlayback.OpenAsync(
            [hdhr.Object, m3u.Object],
            "m3u_channel1",
            [],
            CancellationToken.None);

        Assert.Same(expected, opened);
        Assert.Equal("m3u_channel1", opened.OriginalStreamId);
        Assert.Equal("opening-1", opened.MediaSource.LiveStreamId);
    }

    [Fact]
    public async Task OpenAsync_SelectedSourceDoesNotReuseDefaultButSharesIdenticalSelection()
    {
        var source = new MediaSourceInfo { Id = "source_iptv_choice" };
        var selected = new Mock<ILiveStream>();
        selected.SetupAllProperties();
        selected.SetupGet(s => s.MediaSource).Returns(source);
        selected.SetupGet(s => s.UniqueId).Returns("selected-opening");
        selected.SetupGet(s => s.EnableStreamSharing).Returns(true);
        var automatic = Mock.Of<ILiveStream>(s => s.OriginalStreamId == "m3u_channel1" && s.EnableStreamSharing);
        var host = new Mock<ITunerHost>(MockBehavior.Strict);
        host.Setup(h => h.GetChannelStream("m3u_channel1", source.Id, It.IsAny<IList<ILiveStream>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(selected.Object);

        var opened = await LiveTvLibraryChannelPlayback.OpenAsync([host.Object], "m3u_channel1|" + source.Id, [automatic], CancellationToken.None);
        Assert.Same(selected.Object, opened);
        Assert.Equal("m3u_channel1|source_iptv_choice", opened.OriginalStreamId);
        Assert.Equal("selected-opening", opened.MediaSource.LiveStreamId);
        var shared = await LiveTvLibraryChannelPlayback.OpenAsync([host.Object], "m3u_channel1|" + source.Id, [opened], CancellationToken.None);
        Assert.Same(opened, shared);
        host.Verify(h => h.GetChannelStream("m3u_channel1", source.Id, It.IsAny<IList<ILiveStream>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("m3u_channel|")]
    [InlineData("m3u_channel|https://unconfigured.example")]
    [InlineData("hdhr_channel|source_iptv_choice")]
    [InlineData("m3u_channel|source_iptv_choice|another")]
    public async Task OpenAsync_RejectsMalformedSelectionBeforeCallingTuner(string token)
    {
        var host = new Mock<ITunerHost>(MockBehavior.Strict);
        await Assert.ThrowsAsync<FileNotFoundException>(() => LiveTvLibraryChannelPlayback.OpenAsync([host.Object], token, [], CancellationToken.None));
    }

    [Fact]
    public async Task OpenAsync_ReusesSharedStream()
    {
        var shared = new Mock<ILiveStream>();
        shared.SetupProperty(stream => stream.ConsumerCount, 1);
        shared.SetupProperty(stream => stream.OriginalStreamId, "m3u_channel1");
        shared.SetupGet(stream => stream.EnableStreamSharing).Returns(true);

        var host = new Mock<ITunerHost>(MockBehavior.Strict);

        var opened = await LiveTvLibraryChannelPlayback.OpenAsync(
            [host.Object],
            "m3u_channel1",
            [shared.Object],
            CancellationToken.None);

        Assert.Same(shared.Object, opened);
        Assert.Equal(2, opened.ConsumerCount);
        host.Verify(
            h => h.GetChannelStream(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IList<ILiveStream>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("m3u_abc")]
    [InlineData("hdhr_12.1")]
    public void IsTunerChannelId_TunerIds_AreAccepted(string id)
    {
        Assert.True(LiveTvLibraryChannelPlayback.IsTunerChannelId(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("g:News")]
    [InlineData("imdb-tt123")]
    [InlineData("c5-movies")]
    public void IsTunerChannelId_NonTunerIds_AreRejected(string? id)
    {
        Assert.False(LiveTvLibraryChannelPlayback.IsTunerChannelId(id));
    }

    [Fact]
    public async Task OpenAsync_NonTunerId_ThrowsFileNotFound()
    {
        var host = new Mock<ITunerHost>(MockBehavior.Strict);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            LiveTvLibraryChannelPlayback.OpenAsync(
                [host.Object],
                "imdb-tt123",
                [],
                CancellationToken.None));
    }

    [Fact]
    public void ShouldCacheChannelItemMediaSources_SkipsEmptyAndBlankIds()
    {
        Assert.False(LiveTvLibraryChannelPlayback.ShouldCacheChannelItemMediaSources(null, [new MediaSourceInfo()]));
        Assert.False(LiveTvLibraryChannelPlayback.ShouldCacheChannelItemMediaSources(string.Empty, [new MediaSourceInfo()]));
        Assert.False(LiveTvLibraryChannelPlayback.ShouldCacheChannelItemMediaSources("m3u_channel1", []));
        Assert.False(LiveTvLibraryChannelPlayback.ShouldCacheChannelItemMediaSources("m3u_channel1", null));
        Assert.True(LiveTvLibraryChannelPlayback.ShouldCacheChannelItemMediaSources(
            "m3u_channel1",
            [new MediaSourceInfo { OpenToken = "m3u_channel1" }]));
    }

    [Fact]
    public async Task OpenAsync_MissingTuner_ThrowsResourceNotFound()
    {
        var host = new Mock<ITunerHost>();
        host.Setup(h => h.GetChannelStream(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IList<ILiveStream>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FileNotFoundException());

        await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            LiveTvLibraryChannelPlayback.OpenAsync(
                [host.Object],
                "m3u_missing",
                [],
                CancellationToken.None));
    }
}
