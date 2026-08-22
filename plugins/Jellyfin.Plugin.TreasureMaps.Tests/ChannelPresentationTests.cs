using System;
using System.IO;
using Jellyfin.Plugin.TreasureMaps.Channels;
using Xunit;

namespace Jellyfin.Plugin.TreasureMaps.Tests;

public class ChannelPresentationTests
{
    [Fact]
    public void FolderSortName_PadsOrder()
    {
        Assert.Equal("00-For You", ChannelPresentation.FolderSortName(0, "For You"));
        Assert.Equal("04-Movies", ChannelPresentation.FolderSortName(4, "Movies"));
        Assert.True(string.CompareOrdinal(ChannelPresentation.FolderSortName(0, "For You"), ChannelPresentation.FolderSortName(4, "Movies")) < 0);
    }

    [Fact]
    public void TitleSortName_NewestFirst()
    {
        var older = ChannelPresentation.TitleSortName(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), "Alpha");
        var newer = ChannelPresentation.TitleSortName(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), "Zulu");
        Assert.True(string.CompareOrdinal(newer, older) < 0);
    }

    [Fact]
    public void DownloadSortName_ActiveFirst()
    {
        var active = ChannelPresentation.DownloadSortName(true, "Zulu");
        var done = ChannelPresentation.DownloadSortName(false, "Alpha");
        Assert.True(string.CompareOrdinal(active, done) < 0);
    }

    [Fact]
    public void GetPosterPath_CreatesPng()
    {
        var path = ChannelArtwork.GetPosterPath("test-movies", "Movies");
        if (path is null)
        {
            return; // ffmpeg/font missing in this environment
        }

        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 200);
    }
}
