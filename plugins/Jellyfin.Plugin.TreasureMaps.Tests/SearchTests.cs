using System;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.TreasureMaps.Search;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using Xunit;

namespace Jellyfin.Plugin.TreasureMaps.Tests;

public class SearchTests
{
    [Theory]
    [InlineData("Matrix", "Matrix", 95f)]
    [InlineData("The Matrix", "The Mat", 75f)]
    [InlineData("The Matrix Reloaded", "Matrix", 70f)]
    [InlineData("The Bear", "bear", 93f)]
    [InlineData("The Bear King of the Kitchen", "the bear", 75f)]
    [InlineData("Inception", "cept", 55f)]
    [InlineData("Inception", "xyz", 0f)]
    public void ScoreTitle_RanksMatches(string title, string query, float expected)
    {
        Assert.Equal(expected, TreasureMapsSearch.ScoreTitle(title, query));
    }

    [Fact]
    public void IsLiveTitleQuery_RequiresTwoCharacters()
    {
        Assert.False(TreasureMapsSearch.IsLiveTitleQuery(Query("m")));
        Assert.True(TreasureMapsSearch.IsLiveTitleQuery(Query("ma")));
    }

    [Fact]
    public void IsLiveTitleQuery_SkipsScopedLibrarySearch()
    {
        var query = Query("matrix");
        query = new SearchProviderQuery
        {
            SearchTerm = query.SearchTerm,
            ParentId = Guid.NewGuid()
        };

        Assert.False(TreasureMapsSearch.IsLiveTitleQuery(query));
    }

    [Fact]
    public void IsLiveTitleQuery_SkipsAudioOnly()
    {
        var query = new SearchProviderQuery
        {
            SearchTerm = "matrix",
            MediaTypes = [MediaType.Audio]
        };

        Assert.False(TreasureMapsSearch.IsLiveTitleQuery(query));
    }

    [Fact]
    public void IsLiveTitleQuery_AcceptsMovieOrSeriesOrBoxSet()
    {
        Assert.True(TreasureMapsSearch.IsLiveTitleQuery(new SearchProviderQuery
        {
            SearchTerm = "matrix",
            IncludeItemTypes = [BaseItemKind.Movie]
        }));
        Assert.True(TreasureMapsSearch.IsLiveTitleQuery(new SearchProviderQuery
        {
            SearchTerm = "matrix",
            IncludeItemTypes = [BaseItemKind.Series, BaseItemKind.Movie]
        }));
        Assert.False(TreasureMapsSearch.IsLiveTitleQuery(new SearchProviderQuery
        {
            SearchTerm = "matrix",
            IncludeItemTypes = [BaseItemKind.Person]
        }));
    }

    [Fact]
    public void CacheTtlForQuery_IsLiveForTypedTerms()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), TreasureMapsApiClient.CacheTtlForQuery(null));
        Assert.Equal(TimeSpan.FromMinutes(5), TreasureMapsApiClient.CacheTtlForQuery("*"));
        Assert.Equal(TimeSpan.FromMinutes(5), TreasureMapsApiClient.CacheTtlForQuery("A"));
        Assert.Equal(TimeSpan.FromSeconds(20), TreasureMapsApiClient.CacheTtlForQuery("ma"));
        Assert.Equal(TimeSpan.FromSeconds(20), TreasureMapsApiClient.CacheTtlForQuery("matrix"));
    }

    [Fact]
    public void BestDisplayTitle_PrefersSceneNameWhenItMatchesBetter()
    {
        Assert.Equal("The Bear", TreasureMapsSearch.BestDisplayTitle("The Bear King of the Kitchen", "The Bear", "the bear"));
        Assert.Equal("The Bear", TreasureMapsSearch.BestDisplayTitle("The Bear King of the Kitchen", "The Bear", "bear"));
    }

    [Fact]
    public void ChannelTitleCards_AddsBoxSetWhenMoviesOrSeriesRequested()
    {
        Assert.Contains(BaseItemKind.BoxSet, ChannelTitleCards.IncludeIn([BaseItemKind.Movie, BaseItemKind.Series]));
        Assert.DoesNotContain(BaseItemKind.BoxSet, ChannelTitleCards.IncludeIn([BaseItemKind.Person]));
        Assert.Empty(ChannelTitleCards.IncludeIn([]));
    }

    private static SearchProviderQuery Query(string term)
        => new() { SearchTerm = term };
}
