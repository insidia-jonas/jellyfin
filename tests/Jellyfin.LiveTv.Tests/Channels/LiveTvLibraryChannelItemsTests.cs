using System;
using System.Collections.Generic;
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
        Assert.Equal("2 Sender", items[0].Overview);
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

    [Fact]
    public void Build_ChannelTile_ShowsNowAndNext()
    {
        ChannelInfo[] channels =
        [
            new() { Id = "cnn", Name = "DE | Das Erste", Number = "1" }
        ];
        var guide = new Dictionary<string, LiveTvNowNext>(StringComparer.OrdinalIgnoreCase)
        {
            ["cnn"] = new()
            {
                NowTitle = "Tagesschau",
                NowStart = new DateTime(2026, 9, 13, 18, 0, 0, DateTimeKind.Utc),
                NowEnd = new DateTime(2026, 9, 13, 18, 15, 0, DateTimeKind.Utc),
                NextTitle = "Wetter"
            }
        };

        var now = new DateTime(2026, 9, 13, 18, 6, 0, DateTimeKind.Utc);
        var items = LiveTvLibraryChannelItems.Build(channels, null, guide, now);

        var tile = Assert.Single(items);
        Assert.Equal("Das Erste", tile.Name);
        Assert.DoesNotContain("Tagesschau", tile.Name, StringComparison.Ordinal);
        Assert.StartsWith("Jetzt: Tagesschau", tile.OriginalTitle, StringComparison.Ordinal);
        Assert.Contains("Danach: Wetter", tile.OriginalTitle, StringComparison.Ordinal);
        Assert.Contains("Jetzt: Tagesschau", tile.Overview, StringComparison.Ordinal);
        Assert.Contains("Danach: Wetter", tile.Overview, StringComparison.Ordinal);
        Assert.Equal(new DateTime(2026, 9, 13, 18, 0, 0, DateTimeKind.Utc), tile.PremiereDate);
        Assert.Equal(new DateTime(2026, 9, 13, 18, 0, 0, DateTimeKind.Utc), tile.StartDate);
        Assert.Equal(new DateTime(2026, 9, 13, 18, 15, 0, DateTimeKind.Utc), tile.EndDate);
        Assert.Equal(40, tile.CompletionPercentage);
        Assert.Equal(TimeSpan.FromMinutes(15).Ticks, tile.RunTimeTicks);
        Assert.Equal("1", tile.ProviderIds[LiveTvLibraryChannelItems.ProviderKey]);
        Assert.Equal("Tagesschau", tile.ProviderIds[LiveTvLibraryChannelItems.ProviderNowKey]);
        Assert.Equal("Wetter", tile.ProviderIds[LiveTvLibraryChannelItems.ProviderNextKey]);
        Assert.Contains("livestream", tile.Tags);
        Assert.StartsWith("00001.00", tile.SortName, StringComparison.Ordinal);
        Assert.Equal(default, tile.DateCreated);
    }
}
