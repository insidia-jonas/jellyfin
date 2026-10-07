using System.Linq;
using Jellyfin.Extensions;
using Xunit;

namespace Jellyfin.Extensions.Tests;

public class MediaSearchTests
{
    [Theory]
    [InlineData("Insidious: Out of the Further", "Insidious 6 - Out of the Further")]
    [InlineData("All Her Fault", "All her fualt")]
    [InlineData("All Her Fault", "Fault All Her")]
    [InlineData("Amélie", "Amelie")]
    [InlineData("Die große Reise", "grosse reise")]
    public void TitleAliasesAndTyposAreUseful(string title, string query)
        => Assert.True(MediaSearch.ScoreTitle(title, MediaSearch.Parse(query)) > 0);

    [Theory]
    [InlineData("Insidious", "Insidious 6")]
    [InlineData("Insidious: The Red Door", "Insidious 6 - Out of the Further")]
    [InlineData("Rocky IV", "Rocky VI")]
    [InlineData("Little Women", "it")]
    [InlineData("Mad Max", "mad mad")]
    [InlineData("All Her Fault", "\"All Her Fualt\"")]
    public void SpecificQueriesDoNotInventMatches(string title, string query)
        => Assert.Equal(0, MediaSearch.ScoreTitle(title, MediaSearch.Parse(query)));

    [Fact]
    public void TitleMatchesOutrankTyposAndFullText()
    {
        var query = MediaSearch.Parse("Marissa Carrie");
        Assert.Equal(30, MediaSearch.ScoreDocument(query, ["All Her Fault"], "Marissa erfährt von Carries Vergangenheit."));
        Assert.Equal(0, MediaSearch.ScoreDocument(MediaSearch.Parse("Marissa space"), ["All Her Fault"], "Marissa erfährt von Carrie."));
        Assert.True(MediaSearch.ScoreTitle("Marissa Carrie", query) > MediaSearch.ScoreTitle("Marissa Carries", query));
        Assert.True(MediaSearch.ScoreTitle("All Her Fault", MediaSearch.Parse("All Her Fault")) > MediaSearch.ScoreTitle("All Her Fault", MediaSearch.Parse("All Her Fualt")));
    }

    [Theory]
    [InlineData("The Bear Staffel 2 Folge 3 (2022)")]
    [InlineData("serie: The Bear (2022) S02E03")]
    [InlineData("The Bear 2x03 2022")]
    public void GermanAndCompactFiltersAgree(string text)
    {
        var query = MediaSearch.Parse(text);
        Assert.Equal("The Bear", query.Text);
        Assert.Equal("tv", query.Kind);
        Assert.Equal(2022, query.Year);
        Assert.Equal(2, query.Season);
        Assert.Equal(3, query.Episode);
    }

    [Fact]
    public void FallbacksAreBoundedAndKeepExplicitConstraints()
    {
        var fallbacks = MediaSearch.FallbackQueries(MediaSearch.Parse("serie: All Her Fualt 2025 S01E08"));
        Assert.InRange(fallbacks.Count, 1, 2);
        Assert.All(fallbacks, text =>
        {
            var parsed = MediaSearch.Parse(text);
            Assert.Equal("tv", parsed.Kind);
            Assert.Equal(2025, parsed.Year);
            Assert.Equal(1, parsed.Season);
            Assert.Equal(8, parsed.Episode);
        });
        Assert.Empty(MediaSearch.FallbackQueries(MediaSearch.Parse("\"Insidious 6\"")));
        Assert.Empty(MediaSearch.FallbackQueries(MediaSearch.Parse("tt1234567")));
        Assert.Empty(MediaSearch.FallbackQueries(MediaSearch.Parse("It")));
    }
}
