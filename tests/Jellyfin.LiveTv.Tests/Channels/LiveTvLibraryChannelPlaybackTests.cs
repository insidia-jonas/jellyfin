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
    public void EnsureLiveStreamId_FillsMissingIdFromMediaSource()
    {
        var source = new MediaSourceInfo { Id = "src-md5" };
        var stream = Mock.Of<ILiveStream>(s => s.MediaSource == source);

        LiveTvLibraryChannelPlayback.EnsureLiveStreamId(stream, "m3u_channel1");

        Assert.Equal("src-md5", source.LiveStreamId);
    }

    [Fact]
    public async Task OpenAsync_UsesMatchingTunerHost()
    {
        var source = new MediaSourceInfo { Id = "src1" };
        var expected = Mock.Of<ILiveStream>(stream => stream.MediaSource == source);
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
        Assert.Equal("src1", opened.MediaSource.LiveStreamId);
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
