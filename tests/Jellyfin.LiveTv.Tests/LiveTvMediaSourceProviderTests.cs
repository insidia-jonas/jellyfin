using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.LiveTv;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.LiveTv.Tests;

public class LiveTvMediaSourceProviderTests
{
    [Fact]
    public async Task ReopeningChannelHasNewCloseHandleWhileSharedViewersKeepSameHandle()
    {
        var channel = new LiveTvChannel
        {
            Id = Guid.NewGuid(),
            ExternalId = "doku-channel",
            ServiceName = "test",
            ChannelType = ChannelType.TV
        };
        var library = new Mock<ILibraryManager>();
        library.Setup(m => m.GetItemById(channel.Id)).Returns(channel);
        var service = new Mock<ILiveTvService>();
        service.SetupGet(s => s.Name).Returns("test");
        var first = Stream("first-lifetime");
        var replacement = Stream("replacement-lifetime");
        service.As<ISupportsDirectStreamProvider>()
            .SetupSequence(s => s.GetChannelStreamWithDirectStreamProvider(
                channel.ExternalId, "same-source", It.IsAny<List<ILiveStream>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(first)
            .ReturnsAsync(first)
            .ReturnsAsync(replacement);
        var provider = new LiveTvMediaSourceProvider(
            NullLogger<LiveTvMediaSourceProvider>.Instance,
            Mock.Of<IServerApplicationHost>(),
            Mock.Of<IRecordingsManager>(),
            Mock.Of<IMediaSourceManager>(),
            library.Object,
            [service.Object]);
        var token = "LiveTvChannel_" + channel.Id.ToString("N", CultureInfo.InvariantCulture) + "_same-source";

        var opened = await provider.OpenMediaSource(token, [], CancellationToken.None);
        var oldHandle = opened.MediaSource.LiveStreamId;
        var shared = await provider.OpenMediaSource(token, [opened], CancellationToken.None);
        var reopened = await provider.OpenMediaSource(token, [], CancellationToken.None);

        Assert.Equal(oldHandle, shared.MediaSource.LiveStreamId);
        Assert.NotEqual(oldHandle, reopened.MediaSource.LiveStreamId);
        Assert.Equal(opened.MediaSource.Id, reopened.MediaSource.Id);
    }

    private static ILiveStream Stream(string uniqueId)
    {
        var stream = new Mock<ILiveStream>();
        stream.SetupGet(s => s.UniqueId).Returns(uniqueId);
        stream.SetupGet(s => s.MediaSource).Returns(new MediaSourceInfo { Id = "same-source" });
        return stream.Object;
    }
}
