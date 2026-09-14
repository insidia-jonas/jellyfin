using System.Collections.Generic;
using Jellyfin.LiveTv.Channels;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using Moq;
using Xunit;

namespace Jellyfin.LiveTv.Tests.Channels;

public class ChannelManagerBrowseTests
{
    [Fact]
    public void BypassDiskCache_OnlyOverlayChannels()
    {
        var overlay = new Mock<IChannel>();
        overlay.As<IChannelPresentationOverlay>();

        Assert.True(ChannelManagerBrowse.BypassDiskCache(overlay.Object));
        Assert.False(ChannelManagerBrowse.BypassDiskCache(Mock.Of<IChannel>()));
        Assert.False(ChannelManagerBrowse.BypassDiskCache(null));
    }

    [Fact]
    public void IsRefreshPending_IgnoresNormalResults()
    {
        Assert.True(ChannelManagerBrowse.IsRefreshPending(ChannelItemResult.Pending()));
        Assert.False(ChannelManagerBrowse.IsRefreshPending(new ChannelItemResult()));
        Assert.False(ChannelManagerBrowse.IsRefreshPending(null));
    }

    [Fact]
    public void ExistingItemsMatch_RequiresSameIds()
    {
        var incoming = new List<ChannelItemInfo>
        {
            new() { Id = "cnn" },
            new() { Id = "zdf" }
        };

        Assert.True(ChannelManagerBrowse.ExistingItemsMatch(["cnn", "zdf"], incoming));
        Assert.False(ChannelManagerBrowse.ExistingItemsMatch(["cnn"], incoming));
        Assert.False(ChannelManagerBrowse.ExistingItemsMatch(["cnn", "ard"], incoming));
        Assert.False(ChannelManagerBrowse.ExistingItemsMatch(["cnn", "zdf"], []));
    }

    [Fact]
    public void ExistingItemsMatch_DoesNotRewriteWhenOnlyPresentationChanged()
    {
        var stored = new BaseItem[]
        {
            new Video { ExternalId = "cnn", Name = "CNN", Overview = "old now/next" },
            new Video { ExternalId = "zdf", Name = "ZDF", Overview = "old now/next" }
        };
        var incoming = new List<ChannelItemInfo>
        {
            new() { Id = "cnn", Name = "CNN", Overview = "Jetzt: Tagesschau" },
            new() { Id = "zdf", Name = "ZDF", Overview = "Jetzt: Wetter" }
        };

        Assert.True(ChannelManagerBrowse.ExistingItemsMatch(ChannelManagerBrowse.ExternalIds(stored), incoming));
    }

    [Fact]
    public void LiveTvLibraryChannel_IsOverlayChannel_SoCacheKeyNeedNotRotate()
    {
        Assert.True(typeof(IChannelPresentationOverlay).IsAssignableFrom(typeof(LiveTvLibraryChannel)));
    }
}
