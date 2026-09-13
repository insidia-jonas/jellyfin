using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.LiveTv.Channels;
using Jellyfin.LiveTv.Tests;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
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
    public async Task GetChannelItems_GroupFolder_UsesSameTunerSnapshot()
    {
        var calls = 0;
        var host = new Mock<ITunerHost>();
        host.Setup(h => h.GetChannels(true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                calls++;
                return new List<ChannelInfo>
                {
                    new() { Id = "cnn", Name = "CNN", ChannelGroup = "News" },
                    new() { Id = "film", Name = "Film", ChannelGroup = "Movies" }
                };
            });

        var manager = new Mock<ITunerHostManager>();
        manager.Setup(m => m.TunerHosts).Returns([host.Object]);
        var channel = CreateChannel(manager.Object, Mock.Of<ILibraryManager>());

        var root = await channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);
        var group = await channel.GetChannelItems(
            new InternalChannelItemQuery { FolderId = LiveTvLibraryChannelItems.EncodeGroupId("News") },
            CancellationToken.None);

        Assert.Equal(2, root.Items.Count);
        Assert.Equal("CNN", Assert.Single(group.Items).Name);
        Assert.Equal(2, calls);
        host.Verify(h => h.GetChannels(false, It.IsAny<CancellationToken>()), Times.Never);
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
}
