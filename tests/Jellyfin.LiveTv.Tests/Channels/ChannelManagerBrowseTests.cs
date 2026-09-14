using System;
using System.Collections.Generic;
using Jellyfin.LiveTv.Channels;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
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
        Assert.True(ChannelManagerBrowse.IsLiveTvOverlay(CreateLiveTv()));
        Assert.False(ChannelManagerBrowse.IsLiveTvOverlay(Mock.Of<IChannel>()));
    }

    [Fact]
    public void HideExistingOnPending_DoesNotApplyToLiveTv()
    {
        Assert.True(ChannelManagerBrowse.HideExistingOnPending(Mock.Of<IChannel>(), ChannelItemResult.Pending()));
        Assert.False(ChannelManagerBrowse.HideExistingOnPending(CreateLiveTv(), ChannelItemResult.Pending()));
        Assert.False(ChannelManagerBrowse.HideExistingOnPending(CreateLiveTv(), new ChannelItemResult()));
    }

    [Fact]
    public void DecidePaint_LiveTvGroupFolder_PaintsSnapshotWhenLibraryEmpty()
    {
        var incoming = new ChannelItemResult
        {
            Items = [new ChannelItemInfo { Id = "cnn", Name = "CNN" }]
        };

        Assert.Equal(
            ChannelFolderPaint.PaintIncoming,
            ChannelManagerBrowse.DecidePaint(CreateLiveTv(), incoming, hasExisting: false, existingMatch: false));
        Assert.Equal(
            ChannelFolderPaint.ReuseExisting,
            ChannelManagerBrowse.DecidePaint(CreateLiveTv(), incoming, hasExisting: true, existingMatch: true));
        Assert.True(ChannelManagerBrowse.HasIncomingItems(incoming));
        Assert.False(ChannelManagerBrowse.HasIncomingItems(ChannelItemResult.Pending()));
        Assert.False(ChannelManagerBrowse.HasIncomingItems(new ChannelItemResult()));
    }

    [Fact]
    public void DecidePaint_LiveTvEmptySnapshot_ReusesExistingInsteadOfPending()
    {
        Assert.Equal(
            ChannelFolderPaint.ReuseExisting,
            ChannelManagerBrowse.DecidePaint(CreateLiveTv(), new ChannelItemResult(), hasExisting: true, existingMatch: false));
        Assert.Equal(
            ChannelFolderPaint.ReuseExisting,
            ChannelManagerBrowse.DecidePaint(CreateLiveTv(), ChannelItemResult.Pending(), hasExisting: true, existingMatch: false));
    }

    [Fact]
    public void DecidePaint_TreasureMapsPending_StaysPendingEmpty()
    {
        var overlay = new Mock<IChannel>();
        overlay.As<IChannelPresentationOverlay>();

        Assert.Equal(
            ChannelFolderPaint.PendingEmpty,
            ChannelManagerBrowse.DecidePaint(overlay.Object, ChannelItemResult.Pending(), hasExisting: true, existingMatch: false));
    }

    [Fact]
    public void IsLiveTvGroupFolderId_OnlyGroupPrefix()
    {
        Assert.True(ChannelManagerBrowse.IsLiveTvGroupFolderId(LiveTvLibraryChannelItems.EncodeGroupId("News")));
        Assert.False(ChannelManagerBrowse.IsLiveTvGroupFolderId("movies|abc"));
        Assert.False(ChannelManagerBrowse.IsLiveTvGroupFolderId(null));
    }

    [Fact]
    public void FindLiveTvGroupByLibraryId_MatchesPaintedFolderBeforePersist()
    {
        var newsId = LiveTvLibraryChannelItems.EncodeGroupId("News");
        var sportsId = LiveTvLibraryChannelItems.EncodeGroupId("Sports");
        var newsGuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var root = new List<ChannelItemInfo>
        {
            new() { Id = newsId, Name = "News", Type = ChannelItemType.Folder },
            new() { Id = sportsId, Name = "Sports", Type = ChannelItemType.Folder }
        };

        var match = ChannelManagerBrowse.FindLiveTvGroupByLibraryId(
            newsGuid,
            root,
            id => id == newsId ? newsGuid : Guid.NewGuid());

        Assert.Equal("News", match!.Name);
        Assert.Null(ChannelManagerBrowse.FindLiveTvGroupByLibraryId(Guid.NewGuid(), root, _ => Guid.NewGuid()));
        Assert.Null(ChannelManagerBrowse.FindLiveTvGroupByLibraryId(newsGuid, [], _ => newsGuid));
    }

    private static LiveTvLibraryChannel CreateLiveTv()
        => new(
            Mock.Of<ITunerHostManager>(),
            Mock.Of<ILibraryManager>(),
            Mock.Of<IUserManager>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LiveTvLibraryChannel>.Instance);
}
