using System;
using System.Linq;
using Jellyfin.Plugin.TreasureMaps.Api;
using Jellyfin.Plugin.TreasureMaps.Channels;
using Jellyfin.Plugin.TreasureMaps.Metadata;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Plugin.TreasureMaps.Tests;

public class ReleaseIdentityTests
{
    [Theory]
    [InlineData("Other.2012.1080p.BluRay.REMUX.AVC.FLAC.2.0-DVL", 2012)]
    [InlineData("Blade.Runner.2049.2017.1080p.WEB-DL", 2017)]
    [InlineData("1917.2019.1080p.BluRay", 2019)]
    [InlineData("1917.1080p.BluRay", null)]
    [InlineData("Title.1080p.WEB-DL-GROUP2020", null)]
    public void SceneYearUsesTheReleaseDate(string title, int? expected)
        => Assert.Equal(expected, ReleaseIdentity.SceneYear(title));

    [Fact]
    public void RealProviderMislabelCannotBecomeATitleOrAReleaseEvenWithMatchingIds()
    {
        var bad = new Release { Guid = "bad", Title = "Other.2012.1080p.BluRay.REMUX.AVC.FLAC.2.0-DVL",
            Movie = new() { Title = "Other Mommy", Year = "2026" }, Ids = new() { Imdb = "36586020", Tmdb = "1400837" } };
        Assert.False(ReleaseIdentity.HasConsistentMetadata(bad));
        Assert.False(SeriesBrowse.SameShow(bad, "movie:tmdb:1400837", "Other Mommy"));
        Assert.Empty(ReleaseGrouper.Group(new[] { bad }));
        Assert.Null(ReleaseMapper.ToChannelItem(bad, 0));
    }

    [Fact]
    public void SimilarTitlesAndRemakesCannotBecomeFallbackReleases()
    {
        var wrong = new Release { Guid = "other", Title = "The.Others.2001.1080p", Movie = new() { Title = "The Others", Year = "2001" }, Ids = new() { Imdb = "0230600" } };
        Assert.False(SeriesBrowse.SameShow(wrong, "movie:t:theothermother:2017", "The Other Mother"));
        Assert.False(SeriesBrowse.SameShow(wrong, "movie:imdb:99999", "The Others"));
        Assert.True(SeriesBrowse.SameShow(wrong, "movie:imdb:tt0230600", "The Others"));
        Assert.False(SeriesBrowse.SameShow(wrong, "movie:t:theothers:2026", "The Others"));
    }

    [Fact]
    public void ConflictingImdbCannotMergeThroughASharedTmdbId()
    {
        var releases = new[] {
            new Release { Guid = "one", Title = "One.2020.1080p", Movie = new() { Title = "One", Year = "2020" }, Ids = new() { Imdb = "111", Tmdb = "123" } },
            new Release { Guid = "two", Title = "Two.2020.1080p", Movie = new() { Title = "Two", Year = "2020" }, Ids = new() { Imdb = "222", Tmdb = "123" } }
        };
        Assert.Single(ReleaseGrouper.Group(releases).Single().Releases);
    }

    [Fact]
    public void CatalogDoesNotSupplyAnUnrelatedPosterOrAPlotForAnotherRemake()
    {
        var candidates = new[] { new CatalogHit { Title = "The Others", Year = 2001 }, new CatalogHit { Title = "The Other Mother", Year = 2026 } };
        Assert.Null(MetadataCatalog.PickBest("The Other Mother", 2017, candidates));
    }

    [Fact]
    public void ExistingWronglyTaggedFileIsNotAPlayableCopyOfUpcomingMovie()
    {
        var expected = new Movie { Name = "Other Mommy", ProductionYear = 2026 };
        expected.ProviderIds["Imdb"] = "tt36586020";
        var wrong = new Movie { Name = "Other Mommy", ProductionYear = 2026, Path = "/movies/Other.2012.1080p.BluRay.mkv" };
        wrong.ProviderIds["Imdb"] = "tt36586020";
        Assert.False(IndexerPlaybackController.IdentityMatches(expected, wrong));
        wrong.Path = "/movies/Other.Mommy.2026.1080p.WEB.mkv";
        Assert.True(IndexerPlaybackController.IdentityMatches(expected, wrong));
        wrong.ProviderIds["Imdb"] = "tt99999";
        Assert.False(IndexerPlaybackController.IdentityMatches(expected, wrong));
    }

    [Fact]
    public void LaterTvEpisodesDoNotConflictWithSeriesPremiereYear()
    {
        var episode = new Release { Guid = "episode", Title = "Silo.S03E01.2026.1080p", Tv = new() { Title = "Silo", FirstAired = "2023" } };
        Assert.True(ReleaseIdentity.HasConsistentMetadata(episode));
        Assert.Equal("tv", ReleaseMapper.ToChannelItem(episode, 0)!.ProviderIds["TreasureMapsKind"]);
    }
}
