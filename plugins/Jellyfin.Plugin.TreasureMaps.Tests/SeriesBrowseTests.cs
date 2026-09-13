using System;
using System.Linq;
using Jellyfin.Plugin.TreasureMaps.Api;
using Jellyfin.Plugin.TreasureMaps.Channels;
using Xunit;

namespace Jellyfin.Plugin.TreasureMaps.Tests;

public class SeriesBrowseTests
{
    [Theory]
    [InlineData("Silo.2023.S03E08.GERMAN.DL.1080p.WEB.H264-TSC", 3, 8, false)]
    [InlineData("Reacher.S04E04.German.DL.2160p.WEB-DL", 4, 4, false)]
    [InlineData("Show.S1E2.720p", 1, 2, false)]
    [InlineData("Show.S01E01-E02.GERMAN", 1, 1, false)]
    [InlineData("Show.S01E01E02.GERMAN", 1, 1, false)]
    [InlineData("Show.1x04.HDTV", 1, 4, false)]
    [InlineData("Show.S01.COMPLETE.GERMAN", 1, null, true)]
    [InlineData("Show.Staffel.2.German.DL", 2, null, true)]
    [InlineData("Random.Movie.2024.1080p", null, null, false)]
    public void Parse_ReadsSeasonEpisodeAndPacks(string title, int? season, int? episode, bool pack)
    {
        var slot = SeriesBrowse.Parse(title);
        if (season is null && episode is null && !pack)
        {
            Assert.True(slot.IsUnknown);
            Assert.Equal("other", slot.Key);
            return;
        }

        Assert.False(slot.IsUnknown);
        Assert.Equal(season, slot.Season);
        Assert.Equal(episode, slot.Episode);
        Assert.Equal(pack, slot.IsSeasonPack);
    }

    [Fact]
    public void Parse_MultiEpisode_KeepsRangeInLabel()
    {
        var slot = SeriesBrowse.Parse("Show.S01E01-E03.GERMAN");
        Assert.Equal(1, slot.Season);
        Assert.Equal(1, slot.Episode);
        Assert.Equal(3, slot.EpisodeEnd);
        Assert.Equal("S01E01–E03", slot.Label);
        Assert.Equal("s01e01-e03", slot.Key);
    }

    [Fact]
    public void GroupEpisodes_SplitsQualitiesPerFolge()
    {
        var releases = new[]
        {
            new Release { Guid = "a", Title = "Silo.S03E08.1080p.WEB" },
            new Release { Guid = "b", Title = "Silo.S03E08.2160p.WEB" },
            new Release { Guid = "c", Title = "Silo.S03E07.1080p.WEB" },
            new Release { Guid = "d", Title = "Silo.S02E01.1080p.WEB" }
        };

        var bundles = SeriesBrowse.GroupEpisodes(releases);

        Assert.Equal(3, bundles.Count);
        Assert.Equal("S02E01", bundles[0].Slot.Label);
        Assert.Equal("S03E07", bundles[1].Slot.Label);
        Assert.Equal("S03E08", bundles[2].Slot.Label);
        Assert.Equal(2, bundles[2].Releases.Count);
    }

    [Fact]
    public void LayoutFor_OneEpisode_IsQualities()
    {
        var bundles = SeriesBrowse.GroupEpisodes(
        [
            new Release { Guid = "a", Title = "Silo.S03E08.1080p" },
            new Release { Guid = "b", Title = "Silo.S03E08.2160p" }
        ]);

        Assert.Equal(SeriesCoverLayout.Qualities, SeriesBrowse.LayoutFor(bundles));
    }

    [Fact]
    public void LayoutFor_SeveralEpisodesOneSeason_IsEpisodes()
    {
        var bundles = SeriesBrowse.GroupEpisodes(
        [
            new Release { Guid = "a", Title = "Silo.S03E07.1080p" },
            new Release { Guid = "b", Title = "Silo.S03E08.1080p" }
        ]);

        Assert.Equal(SeriesCoverLayout.Episodes, SeriesBrowse.LayoutFor(bundles));
    }

    [Fact]
    public void LayoutFor_MultipleSeasons_IsSeasons()
    {
        var bundles = SeriesBrowse.GroupEpisodes(
        [
            new Release { Guid = "a", Title = "Silo.S02E01.1080p" },
            new Release { Guid = "b", Title = "Silo.S03E08.1080p" }
        ]);

        Assert.Equal(SeriesCoverLayout.Seasons, SeriesBrowse.LayoutFor(bundles));
        Assert.Equal(new[] { 2, 3 }, SeriesBrowse.SeasonsOf(bundles));
    }

    [Fact]
    public void SearchQueries_StripsTrailingYear()
    {
        var queries = SeriesBrowse.SearchQueries("Silo 2023");
        Assert.Equal(2, queries.Count);
        Assert.Equal("Silo 2023", queries[0]);
        Assert.Equal("Silo", queries[1]);
    }

    [Fact]
    public void SeasonQueries_FillsGapsAndLooksAhead()
    {
        var queries = SeriesBrowse.SeasonQueries("Silo 2023", [3]);
        Assert.Contains("Silo S03", queries);
        Assert.Contains("Silo S3", queries);
        Assert.Contains("Silo S01", queries);
        Assert.Contains("Silo S04", queries);
    }

    [Fact]
    public void SameShow_MatchesByTitleWhenKeysDiffer()
    {
        var release = new Release
        {
            Guid = "x",
            Title = "Silo.2023.S03E08.GERMAN",
            Category = new ReleaseCategory { Name = "TV - DE > HD" }
        };

        Assert.True(SeriesBrowse.SameShow(release, "tv:t:otherkey", "Silo 2023"));
        Assert.False(SeriesBrowse.SameShow(release, "tv:t:otherkey", "Reacher"));
    }

    [Fact]
    public void EpisodeOverview_CountsReleases()
    {
        Assert.StartsWith("1 release", SeriesBrowse.EpisodeOverview(1), StringComparison.Ordinal);
        Assert.StartsWith("3 releases", SeriesBrowse.EpisodeOverview(3), StringComparison.Ordinal);
    }

    [Fact]
    public void SeasonLabel_IncludesEpisodeCount()
    {
        Assert.Equal("Season 3 · 8 episodes", SeriesBrowse.SeasonLabel(3, 8));
        Assert.Equal("Season 1 · 1 episode", SeriesBrowse.SeasonLabel(1, 1));
    }

    [Fact]
    public void ReleasesFor_ReturnsThatFolgeOnly()
    {
        var bundles = SeriesBrowse.GroupEpisodes(
        [
            new Release { Guid = "a", Title = "Silo.S03E08.1080p" },
            new Release { Guid = "b", Title = "Silo.S03E07.1080p" }
        ]);

        var eight = SeriesBrowse.ReleasesFor(bundles, "s03e08");
        Assert.Equal("a", Assert.Single(eight).Guid);
    }

    [Fact]
    public void SortKey_PutsPackBeforeEpisodesAndS01E02BeforeS01E10()
    {
        var pack = SeriesBrowse.Parse("Show.S01.COMPLETE");
        var e2 = SeriesBrowse.Parse("Show.S01E02");
        var e10 = SeriesBrowse.Parse("Show.S01E10");
        var ordered = new[] { e10, pack, e2 }.OrderBy(s => s.SortKey).Select(s => s.Key).ToArray();
        Assert.Equal(new[] { "s01pack", "s01e02", "s01e10" }, ordered);
    }
}
