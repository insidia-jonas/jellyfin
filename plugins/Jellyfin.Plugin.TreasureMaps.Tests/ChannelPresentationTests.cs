using System;
using System.IO;
using Jellyfin.Plugin.TreasureMaps.Channels;
using Xunit;

namespace Jellyfin.Plugin.TreasureMaps.Tests;

public class ChannelPresentationTests
{
    [Fact]
    public void Artwork_ReusesDurableFileAndReplacesTruncatedWrites()
    {
        var directory = Path.Combine(Path.GetTempPath(), "artwork-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = ChannelArtwork.GetPosterPath("movies", "Movies", directory);
            Assert.Equal(Path.Combine(directory, "movies.png"), path);
            var expected = File.ReadAllBytes(path!);
            var modified = File.GetLastWriteTimeUtc(path!);
            Assert.Equal(path, ChannelArtwork.GetPosterPath("movies", "Movies", directory));
            Assert.Equal(modified, File.GetLastWriteTimeUtc(path!));
            File.WriteAllBytes(path!, expected.AsSpan(0, 300).ToArray());
            Assert.Equal(path, ChannelArtwork.GetPosterPath("movies", "Movies", directory));
            Assert.Equal(expected, File.ReadAllBytes(path!));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("movies")]
    [InlineData("tv")]
    [InlineData("movies-de")]
    [InlineData("downloads")]
    [InlineData("foryou")]
    [InlineData("genres")]
    [InlineData("find")]
    public void MainCategory_HasBundledLandscapeArtwork(string category)
    {
        var path = ChannelArtwork.GetPosterPath(category, category);
        Assert.NotNull(path);
        var png = File.ReadAllBytes(path);
        Assert.Equal(600, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)));
        Assert.Equal(360, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
    }

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
