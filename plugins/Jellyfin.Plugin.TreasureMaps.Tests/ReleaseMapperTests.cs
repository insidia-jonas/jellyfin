using System;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.TreasureMaps.Api;
using Jellyfin.Plugin.TreasureMaps.Channels;
using Xunit;

namespace Jellyfin.Plugin.TreasureMaps.Tests;

public class ReleaseMapperTests
{
    private const string MovieResponseJson = """
    {
      "items": [
        {
          "guid": "guid-dune",
          "title": "Dune.2021.2160p.UHD.BluRay",
          "size": 12884901888,
          "grabs": 42,
          "ids": { "imdb": "tt1160419", "tmdb": "438631" },
          "images": { "cover": "https://img/cover.jpg", "backdrop": "https://img/back.jpg" },
          "video": { "codec": "HEVC", "resolution": "2160p" },
          "movie": {
            "title": "Dune",
            "tagline": "Beyond fear, destiny awaits.",
            "plot": "Paul Atreides leads a rebellion.",
            "rating": "8.1",
            "genres": "Science Fiction, Action",
            "year": "2021"
          },
          "links": { "details": "https://tm/r/guid-dune", "download": "https://tm/dl/guid-dune" }
        }
      ],
      "pagination": { "limit": 60, "offset": 0, "total": 1 }
    }
    """;

    private static readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Deserialize_MovieResponse_ParsesFields()
    {
        var response = JsonSerializer.Deserialize<ReleaseListResponse>(MovieResponseJson, _options);

        Assert.NotNull(response);
        var release = Assert.Single(response!.Items);
        Assert.Equal("guid-dune", release.Guid);
        Assert.Equal("Dune", release.Movie?.Title);
        Assert.Equal("2021", release.Movie?.Year);
        Assert.Equal("tt1160419", release.Ids?.Imdb);
        Assert.Equal("2160p", release.Video?.Resolution);
        Assert.Equal(12884901888, release.Size);
        Assert.Equal(1, response.Pagination?.Total);
    }

    [Fact]
    public void ToChannelItem_MapsRichMetadata()
    {
        var response = JsonSerializer.Deserialize<ReleaseListResponse>(MovieResponseJson, _options)!;
        var release = response.Items.First();

        var item = ReleaseMapper.ToChannelItem(release, minRating: 0);

        Assert.NotNull(item);
        Assert.Equal("Dune", item!.Name);
        Assert.Equal(2021, item.ProductionYear);
        Assert.Equal(8.1f, item.CommunityRating);
        Assert.Equal("https://img/cover.jpg", item.ImageUrl);
        Assert.Contains("Science Fiction", item.Genres);
        Assert.Contains("Action", item.Genres);
        Assert.Equal("tt1160419", item.ProviderIds["Imdb"]);
        Assert.Equal("438631", item.ProviderIds["Tmdb"]);
        Assert.Contains(item.Tags, t => t.Contains("2160p", StringComparison.Ordinal));
        Assert.Contains(item.Tags, t => t.Contains("GB", StringComparison.Ordinal));
        Assert.Contains("Paul Atreides", item.Overview, StringComparison.Ordinal);
    }

    [Fact]
    public void ToChannelItem_AppliesLanguagePreferences()
    {
        var response = JsonSerializer.Deserialize<ReleaseListResponse>(MovieResponseJson, _options)!;
        var release = response.Items.First();
        release.AudioLanguages = new[] { "English" };
        var prefs = new Languages.LanguagePreferences("de", new[] { "en" }, false);

        var item = ReleaseMapper.ToChannelItem(release, minRating: 0, prefs, null, out var rank);

        Assert.NotNull(item);
        Assert.Equal(1, rank); // English is the first secondary language
        Assert.Contains("EN", item!.Tags);
    }

    [Fact]
    public void ToChannelItem_FiltersOutUnacceptedLanguage()
    {
        var response = JsonSerializer.Deserialize<ReleaseListResponse>(MovieResponseJson, _options)!;
        var release = response.Items.First();
        release.AudioLanguages = new[] { "Spanish" };
        var prefs = new Languages.LanguagePreferences("de", new[] { "en" }, FilterOut: true);

        var item = ReleaseMapper.ToChannelItem(release, minRating: 0, prefs, null, out _);

        Assert.Null(item);
    }

    [Fact]
    public void ToChannelItem_BelowMinRating_IsFilteredOut()
    {
        var response = JsonSerializer.Deserialize<ReleaseListResponse>(MovieResponseJson, _options)!;
        var release = response.Items.First();

        var item = ReleaseMapper.ToChannelItem(release, minRating: 9.0);

        Assert.Null(item);
    }

    [Fact]
    public void ToChannelItem_MapsTvRelease()
    {
        const string tvJson = """
        {
          "guid": "guid-reacher",
          "title": "Reacher.S01.2160p",
          "size": 21474836480,
          "ids": { "imdb": "tt9288030" },
          "images": { "cover": "https://img/reacher.jpg" },
          "video": { "codec": "HEVC", "resolution": "2160p" },
          "tv": { "title": "Reacher", "imdb": "tt9288030", "first_aired": "2022-02-04" }
        }
        """;
        var release = JsonSerializer.Deserialize<Release>(tvJson, _options)!;

        var item = ReleaseMapper.ToChannelItem(release, minRating: 0);

        Assert.NotNull(item);
        Assert.Equal("Reacher", item!.Name);
        Assert.Equal(2022, item.ProductionYear);
        Assert.Equal("tt9288030", item.ProviderIds["Imdb"]);
        Assert.Equal("https://img/reacher.jpg", item.ImageUrl);
    }

    [Theory]
    [InlineData(0, "unknown size")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1610612736, "1.5 GB")]
    public void FormatSize_ReturnsHumanReadable(long bytes, string expected)
    {
        Assert.Equal(expected, ReleaseMapper.FormatSize(bytes));
    }
}
