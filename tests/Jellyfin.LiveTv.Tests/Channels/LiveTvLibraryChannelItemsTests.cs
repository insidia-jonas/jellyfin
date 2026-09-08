using Jellyfin.LiveTv.Channels;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.LiveTv;
using Xunit;

namespace Jellyfin.LiveTv.Tests.Channels;

public class LiveTvLibraryChannelItemsTests
{
    [Fact]
    public void Build_Root_CreatesGroupFoldersAndUngroupedChannels()
    {
        ChannelInfo[] channels =
        [
            new() { Id = "cnn", Name = "CNN", ChannelGroup = "News", ImageUrl = "https://logo/cnn.png" },
            new() { Id = "bbc", Name = "BBC", ChannelGroup = "News" },
            new() { Id = "extra", Name = "Extra" }
        ];

        var items = LiveTvLibraryChannelItems.Build(channels, null);

        Assert.Equal(2, items.Count);
        Assert.Equal(ChannelItemType.Folder, items[0].Type);
        Assert.Equal("News", items[0].Name);
        Assert.Equal("https://logo/cnn.png", items[0].ImageUrl);
        Assert.Equal(ChannelItemType.Media, items[1].Type);
        Assert.Equal("Extra", items[1].Name);
        Assert.True(items[1].IsLiveStream);
    }

    [Fact]
    public void Build_GroupFolder_ReturnsOnlyThatGroup()
    {
        ChannelInfo[] channels =
        [
            new() { Id = "cnn", Name = "CNN", ChannelGroup = "News" },
            new() { Id = "film", Name = "Film 1", ChannelGroup = "Movies" }
        ];

        var folderId = LiveTvLibraryChannelItems.EncodeGroupId("News");
        var items = LiveTvLibraryChannelItems.Build(channels, folderId);

        Assert.Single(items);
        Assert.Equal("CNN", items[0].Name);
        Assert.Equal("cnn", items[0].Id);
        Assert.True(items[0].IsLiveStream);
    }
}
