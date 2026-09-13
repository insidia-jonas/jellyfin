using System;
using System.Collections.Generic;
using Jellyfin.Plugin.TreasureMaps.Api;
using Jellyfin.Plugin.TreasureMaps.Channels;
using Xunit;

namespace Jellyfin.Plugin.TreasureMaps.Tests;

public class CategoryBrowseTests
{
    [Fact]
    public void CountUniqueKeys_CollapsesQualitiesOfSameTitle()
    {
        var releases = new[]
        {
            Movie("g1", "Dune", "tt1"),
            Movie("g2", "Dune", "tt1"),
            Movie("g3", "Dune", "tt1"),
            Movie("g4", "Heat", "tt2"),
            Movie("g5", "Heat", "tt2")
        };

        Assert.Equal(2, CategoryBrowse.CountUniqueKeys(releases));
    }

    [Fact]
    public void OrderNewest_PutsLatestPostedFirst()
    {
        var older = new ReleaseGroup
        {
            Key = "old",
            Title = "Alpha",
            Posted = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };
        var newer = new ReleaseGroup
        {
            Key = "new",
            Title = "Zulu",
            Posted = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)
        };
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["old"] = 0,
            ["new"] = 1
        };

        var ordered = CategoryBrowse.OrderNewest(new[] { older, newer }, ranks);

        Assert.Equal("new", ordered[0].Key);
        Assert.Equal("old", ordered[1].Key);
    }

    [Fact]
    public void Slice_ReturnsPageAndTotal()
    {
        var items = new[] { 1, 2, 3, 4, 5, 6, 7 };
        var (page1, total) = CategoryBrowse.Slice(items, 1, 3);
        var (page3, _) = CategoryBrowse.Slice(items, 3, 3);

        Assert.Equal(3, total);
        Assert.Equal(new[] { 1, 2, 3 }, page1);
        Assert.Equal(new[] { 7 }, page3);
    }

    [Fact]
    public void PageFolderId_Roundtrips()
    {
        var id = CategoryBrowse.PageFolderId("genre:Action", 3);
        Assert.True(CategoryBrowse.TryParsePageFolder(id, out var scope, out var page));
        Assert.Equal("genre:Action", scope);
        Assert.Equal(3, page);
        Assert.Equal("Page 2 (51–100)", CategoryBrowse.PageLabel(2, 50, 200));
        Assert.False(CategoryBrowse.TryParsePageFolder("movies", out _, out _));
    }

    private static Release Movie(string guid, string title, string imdb)
        => new()
        {
            Guid = guid,
            Title = title + ".2021.1080p",
            Ids = new ReleaseIds { Imdb = imdb },
            Movie = new ReleaseMovie { Title = title, Year = "2021" }
        };
}
