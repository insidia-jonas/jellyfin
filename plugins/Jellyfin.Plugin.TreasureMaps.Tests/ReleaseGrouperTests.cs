using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.TreasureMaps.Api;
using Jellyfin.Plugin.TreasureMaps.Channels;
using Xunit;

namespace Jellyfin.Plugin.TreasureMaps.Tests;

public class ReleaseGrouperTests
{
    private static readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);

    private const string TwoReleasesSameMovieJson = """
    {
      "items": [
        {
          "guid": "g1",
          "title": "Dune.2021.1080p.BluRay.x264-GROUP",
          "ids": { "imdb": "tt1160419", "tmdb": "438631" },
          "images": { "cover": "https://img/dune.jpg" },
          "video": { "resolution": "1080p" },
          "movie": { "title": "Dune", "year": "2021", "genres": "Science Fiction", "rating": "8.1" }
        },
        {
          "guid": "g2",
          "title": "Dune.2021.2160p.UHD.BluRay.x265-OTHER",
          "ids": { "imdb": "tt1160419", "tmdb": "438631" },
          "video": { "resolution": "2160p" },
          "movie": { "title": "Dune", "year": "2021" }
        },
        {
          "guid": "g3",
          "title": "Sicario.2015.1080p.BluRay-XYZ",
          "ids": { "imdb": "tt3397884", "tmdb": "273481" },
          "images": { "cover": "https://img/sicario.jpg" },
          "movie": { "title": "Sicario", "year": "2015" }
        }
      ]
    }
    """;

    [Fact]
    public void Group_MergesReleasesOfSameMovie_ByExternalId()
    {
        var response = JsonSerializer.Deserialize<ReleaseListResponse>(TwoReleasesSameMovieJson, _options)!;

        var groups = ReleaseGrouper.Group(response.Items);

        Assert.Equal(2, groups.Count);
        var dune = groups.First(g => g.Title == "Dune");
        Assert.Equal(2, dune.Releases.Count);
        Assert.Equal("https://img/dune.jpg", dune.Cover); // filled from the release that has it
        Assert.Equal(2021, dune.Year);
        Assert.Equal("movie", dune.Kind);
    }

    [Fact]
    public void KindOf_DetectsTv_FromEpisodeMarker()
    {
        var release = new Release { Guid = "x", Title = "Reacher.S04E04.German.DL.2160p.WEB-DL" };
        Assert.Equal("tv", ReleaseGrouper.KindOf(release));
    }

    [Fact]
    public void ShowNameFromScene_StripsEpisode()
    {
        Assert.Equal("Reacher", ReleaseGrouper.ShowNameFromScene("Reacher.S04E04.German.DL.2160p.WEB-DL"));
        Assert.Equal("Greys Anatomy Die jungen Aerzte", ReleaseGrouper.ShowNameFromScene("Greys.Anatomy.Die.jungen.Aerzte.S22E13.GERMAN"));
    }

    [Fact]
    public void CleanSceneTitle_StripsYearAndTech()
    {
        Assert.Equal("Toy Story 5", ReleaseGrouper.CleanSceneTitle("Toy.Story.5.2026.German.DL.1080p.WEBRiP.x265-P73"));
    }

    [Fact]
    public void Group_TvEpisodes_MergeIntoOneShow_ByName()
    {
        var response = JsonSerializer.Deserialize<ReleaseListResponse>("""
        {
          "items": [
            { "guid": "a", "title": "Silo.2023.S03E08.GERMAN.DL.1080p.WEB.H264-TSC", "category": { "id": 5040, "name": "TV - DE > HD" } },
            { "guid": "b", "title": "Silo.2023.S03E07.GERMAN.DL.1080p.WEB.H264-TSC", "category": { "id": 5040, "name": "TV - DE > HD" } }
          ]
        }
        """, _options)!;

        var groups = ReleaseGrouper.Group(response.Items);

        var group = Assert.Single(groups);
        Assert.Equal("Silo 2023", group.Title);
        Assert.Equal("tv", group.Kind);
        Assert.Equal(2, group.Releases.Count);
    }
}
