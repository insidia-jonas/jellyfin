using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.TreasureMaps.Api;
using Jellyfin.Plugin.TreasureMaps.Channels;
using Jellyfin.Plugin.TreasureMaps.Search;
using Xunit;

namespace Jellyfin.Plugin.TreasureMaps.Tests;

public class TargetedSearchTests
{
    [Fact]
    public void ExactSeriesIdentitySuppressesUnrelatedShowsWithTheSameWord()
    {
        var titles = new[] { "The Bear", "Masha and the Bear", "We Baby Bears" };
        var releases = titles.Select((title, i) => new Release { Guid = i.ToString(System.Globalization.CultureInfo.InvariantCulture), Title = title + ".S01E01.1080p", Tv = new ReleaseTv { Title = title, FirstAired = "2022" } });
        Assert.Equal("The Bear", Assert.Single(TreasureMapsSearch.RelevantReleases(releases, "serie: The Bear")).Tv!.Title);
        Assert.Equal(3, TreasureMapsSearch.RelevantReleases(releases, "bea").Count);
    }

    [Theory]
    [InlineData("Little Women", "it", 2019)]
    [InlineData("The Matrix Reloaded", "Matrix 1999", 2003)]
    [InlineData("The Matrix", "Matrix 1999", null)]
    [InlineData("The Matrix Reloaded", "\"Matrix\"", 2003)]
    [InlineData("Mad Max", "mad mad", 1979)]
    public void SpecificQueriesRejectUnrelatedMatches(string title, string query, int? year)
        => Assert.Equal(0, TreasureMapsSearch.ScoreTitle(title, query, year));

    [Theory]
    [InlineData("Amélie", "Amelie")]
    [InlineData("Spider-Man: Homecoming", "spider man home")]
    [InlineData("Die große Reise", "grosse reise")]
    [InlineData("Blade Runner 2049", "Blade Runner 2049")]
    [InlineData("1917", "1917")]
    public void TitlesPreserveNumbersAndNormalizePunctuation(string title, string query)
        => Assert.True(TreasureMapsSearch.ScoreTitle(title, query) > 0);

    [Fact]
    public void StructuredQueryUsesDocumentedIndexerParameters()
    {
        var parameters = new Dictionary<string, string?>();
        TreasureMapsSearch.ApplyParameters(parameters, "serie: The Bear (2022) S02E03", "tv");
        Assert.Equal("The Bear", parameters["q"]);
        Assert.Equal("2022", parameters["year"]);
        Assert.Equal("2", parameters["season"]);
        Assert.Equal("3", parameters["ep"]);
        TreasureMapsSearch.ApplyParameters(parameters, "tt0133093", "movie");
        Assert.Equal("*", parameters["q"]);
        Assert.Equal("0133093", parameters["imdbid"]);
    }

    [Fact]
    public void IdentitySearchRequiresTheRequestedIdentityAndKind()
    {
        Assert.True(TreasureMapsSearch.ScoreIdentity("Matrix", "film: tt0133093", 1999, "movie", "0133093") > 0);
        Assert.Equal(0, TreasureMapsSearch.ScoreIdentity("Matrix", "film: tt0133093", 1999, "tv", "0133093"));
        Assert.Equal(0, TreasureMapsSearch.ScoreIdentity("Matrix", "tt0133093", 1999, "movie", "tt0234215"));
    }

    [Fact]
    public void AlternativeReleaseTitlesAreMatchedBeforeGrouping()
    {
        var release = new Release { Guid = "a", Title = "Sut.Kardesler.1976.German.1080p", Movie = new ReleaseMovie { Title = "The Foster Brothers", Year = "1976" } };
        Assert.True(TreasureMapsSearch.ScoreRelease(release, "Sut Kardesler 1976") > 0);
        var group = ReleaseGrouper.Group(new[] { release }).Single();
        Assert.Equal("Sut Kardesler", TreasureMapsSearch.DisplayTitle(group, "Sut Kardesler"));
    }

    [Fact]
    public void EpisodeQueryRejectsOtherEpisodesAndSeasonPacks()
    {
        var release = new Release { Guid = "a", Title = "The.Bear.S02E03.German.1080p", Tv = new ReleaseTv { Title = "The Bear", FirstAired = "2022-06-23" } };
        Assert.True(TreasureMapsSearch.ScoreRelease(release, "The Bear S02E03") > 0);
        Assert.Equal(0, TreasureMapsSearch.ScoreRelease(release, "The Bear S02E04"));
        release.Title = "The.Bear.S02.German.1080p";
        Assert.Equal(0, TreasureMapsSearch.ScoreRelease(release, "The Bear S02E03"));
        Assert.True(TreasureMapsSearch.ScoreRelease(release, "The Bear S02") > 0);
    }

    [Fact]
    public async Task ExplicitSeriesQueryNeverRequestsMoviePages()
    {
        var kinds = new List<string>();
        await LiveSearchPages.FetchAsync("serie: The Bear", 24, (kind, _, _) =>
        {
            kinds.Add(kind);
            return Task.FromResult<(IReadOnlyList<Release>, bool)>((Array.Empty<Release>(), true));
        }, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "tv" }, kinds);
    }

    [Fact]
    public void SpecificSubtitleSurvivesInformalInstallmentNumber()
    {
        var release = new Release { Guid = "six", Title = "Insidious.Out.of.the.Further.2026.German.1080p", Movie = new ReleaseMovie { Title = "Insidious: Out of the Further", Year = "2026" } };
        Assert.True(TreasureMapsSearch.ScoreRelease(release, "Insidious 6 - Out of the Further") > 90);
        var parameters = new Dictionary<string, string?>();
        TreasureMapsSearch.ApplyParameters(parameters, "Insidious 6 - Out of the Further", "movie");
        Assert.Equal("Insidious Out of the Further", parameters["q"]);
        Assert.Equal(0, TreasureMapsSearch.ScoreRelease(release, "Insidious 2 - The Red Door"));
    }

    [Fact]
    public void FranchiseSearchRetainsSubtitlesAfterAnExactOriginalFilm()
    {
        var releases = new[] { "Insidious", "Insidious: Out of the Further", "Insidious: The Red Door", "Something Insidious" }
            .Select((title, i) => new Release { Guid = i.ToString(System.Globalization.CultureInfo.InvariantCulture), Title = title, Movie = new ReleaseMovie { Title = title } });
        Assert.Equal(3, TreasureMapsSearch.RelevantReleases(releases, "Insidious").Count);
    }

    [Fact]
    public void FullTextMetadataSurvivesEveryRankingStage()
    {
        var release = new Release { Guid = "a", Title = "Unknown.Title.2025", Movie = new ReleaseMovie { Title = "Another Day", Year = "2025", Plot = "A mysterious submarine crosses the ocean.", Actors = ["Jane Doe"] } };
        Assert.True(TreasureMapsSearch.ScoreRelease(release, "mysterious submarine") > 0);
        Assert.True(TreasureMapsSearch.ScoreGroup(ReleaseGrouper.Group([release]).Single(), "Jane Doe") > 0);
        Assert.True(TreasureMapsSearch.ScoreDocument("Another Day", "mysterious submarine", 2025, "movie", null, "Unknown Title", release.Movie.Plot, "Jane Doe") > 0);
    }

    [Fact]
    public async Task EmptyExactProviderSearchRecoversTypoWithoutAcceptingUnrelatedTitles()
    {
        var queries = new List<string>();
        var release = new Release { Guid = "a", Title = "All.Her.Fault.S01E08", Tv = new ReleaseTv { Title = "All Her Fault", FirstAired = "2025" } };
        var found = await LiveSearchPages.FetchAsync("serie: All Her Fualt", 24, (_, _, _) => throw new InvalidOperationException(), TestContext.Current.CancellationToken,
            search: (_, query, _, _) =>
            {
                queries.Add(query);
                return Task.FromResult<(IReadOnlyList<Release>, bool)>((query.Contains("fualt", StringComparison.OrdinalIgnoreCase) ? [] : [release], true));
            });
        Assert.Single(TreasureMapsSearch.RelevantReleases(found, "serie: All Her Fualt"));
        Assert.InRange(queries.Count, 2, 3);
    }
}
