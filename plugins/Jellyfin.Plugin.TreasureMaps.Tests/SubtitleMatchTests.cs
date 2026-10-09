using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TreasureMaps.Subtitles;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using Xunit;

namespace Jellyfin.Plugin.TreasureMaps.Tests;

public sealed class SubtitleMatchTests
{
    private static SubtitleSearchRequest Movie() => new() { Name = "Resident Evil", ProductionYear = 2002, ContentType = VideoContentType.Movie, ProviderIds = new() { ["Imdb"] = "tt0120804" } };
    private static OsSubtitleAttributes Candidate() => new() { Language = "de", Feature = new() { Type = "Movie", Title = "Resident Evil", Year = 2002, Imdb = 120804 }, Files = new[] { new OsSubtitleFile { FileId = 42 } } };

    [Fact]
    public void RejectsWrongFilmEvenWhenPopularOrHashFlagged()
    {
        var candidate = Candidate(); candidate.Feature!.Title = "Evil Dead Burn"; candidate.Feature.Imdb = 999; candidate.DownloadCount = 999999; candidate.MoviehashMatch = true;
        Assert.False(SubtitleMatch.Accept(Movie(), candidate, "de", false));
        Assert.False(SubtitleMatch.Accept(Movie(), candidate, "de", true));
    }

    [Fact]
    public void ExactIdentityAllowsLocalizedTitleButNotWrongLanguage()
    {
        var candidate = Candidate(); candidate.Feature!.Title = "Biogefährdung";
        Assert.True(SubtitleMatch.Accept(Movie(), candidate, "de", false));
        Assert.False(SubtitleMatch.Accept(Movie(), candidate, "en", false));
    }

    [Fact]
    public void TitleFallbackRequiresCompleteTitleAndYear()
    {
        var request = Movie(); request.ProviderIds.Clear(); var candidate = Candidate();
        Assert.True(SubtitleMatch.Accept(request, candidate, "de", false));
        candidate.Feature!.Title = "Resident Evil: Apocalypse";
        Assert.False(SubtitleMatch.Accept(request, candidate, "de", false));
        candidate.Feature.Title = "Resident Evil"; candidate.Feature.Year = 2026;
        Assert.False(SubtitleMatch.Accept(request, candidate, "de", false));
        candidate.Feature.Year = null;
        Assert.False(SubtitleMatch.Accept(request, candidate, "de", false));
    }

    [Fact]
    public void HashQueryMustActuallyMatchHashAndRespectKnownIdentity()
    {
        var candidate = Candidate();
        Assert.False(SubtitleMatch.Accept(Movie(), candidate, "de", true));
        candidate.MoviehashMatch = true;
        Assert.True(SubtitleMatch.Accept(Movie(), candidate, "de", true));
        candidate.Feature = null;
        Assert.True(SubtitleMatch.Accept(Movie(), candidate, "de", true));
        Assert.False(SubtitleMatch.Accept(Movie(), candidate, "de", false));
    }

    [Theory]
    [InlineData(1, 2, true)]
    [InlineData(1, 3, false)]
    [InlineData(2, 2, false)]
    [InlineData(null, null, false)]
    public void SeriesIdentityRequiresCorrectEpisode(int? season, int? episode, bool accepted)
    {
        var request = new SubtitleSearchRequest { ContentType = VideoContentType.Episode, SeriesName = "Resident Evil", ParentIndexNumber = 1, IndexNumber = 2, SeriesProviderIds = new() { ["Imdb"] = "tt9660182" } };
        var candidate = Candidate(); candidate.Feature = new() { Type = "Episode", ParentImdb = 9660182, Season = season, Episode = episode };
        Assert.Equal(accepted, SubtitleMatch.Accept(request, candidate, "de", false));
    }

    [Fact]
    public async Task ValidatedPassesRejectProviderNoiseAndStopAfterIdentityMatches()
    {
        var calls = new List<IReadOnlyDictionary<string, string?>>();
        var result = await OpenSubtitlesProvider.SearchValidatedAsync(Movie(), "de", (query, _) =>
        {
            calls.Add(query); var wrong = Candidate(); wrong.Feature!.Imdb = 999;
            return Task.FromResult<OsSearchResponse?>(new() { Data = query.ContainsKey("moviehash")
                ? new[] { new OsSubtitle { Attributes = wrong } }
                : new[] { new OsSubtitle { Attributes = wrong }, new OsSubtitle { Attributes = Candidate() } } });
        }, TestContext.Current.CancellationToken, "1234567890abcdef");
        Assert.Single(result); Assert.Equal(2, calls.Count); Assert.Equal("120804", calls[1]["imdb_id"]);
        Assert.DoesNotContain(calls, c => c.ContainsKey("query"));
    }

    [Fact]
    public async Task EpisodeIdAndParentIdUseDifferentApiParameters()
    {
        var request = new SubtitleSearchRequest { ContentType = VideoContentType.Episode, ParentIndexNumber = 1, IndexNumber = 2, ProviderIds = new() { ["Imdb"] = "tt123" } };
        var calls = new List<IReadOnlyDictionary<string, string?>>();
        Task<OsSearchResponse?> Search(IReadOnlyDictionary<string, string?> p, CancellationToken _) { calls.Add(p); return Task.FromResult<OsSearchResponse?>(new()); }
        await OpenSubtitlesProvider.SearchValidatedAsync(request, "de", Search, TestContext.Current.CancellationToken);
        Assert.False(calls.Single().ContainsKey("season_number"));
        request.SeriesProviderIds["Imdb"] = "tt456"; request.ProviderIds.Clear(); calls.Clear();
        await OpenSubtitlesProvider.SearchValidatedAsync(request, "de", Search, TestContext.Current.CancellationToken);
        Assert.Equal("456", calls.Single()["parent_imdb_id"]); Assert.Equal("2", calls.Single()["episode_number"]);
    }
}
