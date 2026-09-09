using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.TreasureMaps.Api;
using Jellyfin.Plugin.TreasureMaps.Channels;
using Jellyfin.Plugin.TreasureMaps.Languages;
using Jellyfin.Plugin.TreasureMaps.Xrel;
using Xunit;

namespace Jellyfin.Plugin.TreasureMaps.Tests;

public class XrelTests
{
    private const string XrelReleaseJson = """
    {
      "id": "f638d1cfec8d",
      "dirname": "Dune.2021.2160p.UHD.BluRay-GROUP",
      "group_name": "GROUP",
      "num_ratings": 128,
      "video_rating": 9.2,
      "audio_rating": 8.7,
      "ext_info": { "type": "movie", "title": "Dune", "rating": 8.4, "num_ratings": 4200 }
    }
    """;

    private static readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ParseRating_ReadsAllFields()
    {
        var rating = XrelClient.ParseRating(XrelReleaseJson);

        Assert.NotNull(rating);
        Assert.Equal(9.2, rating!.Value.VideoRating);
        Assert.Equal(8.7, rating.Value.AudioRating);
        Assert.Equal(128, rating.Value.NumRatings);
        Assert.Equal(8.4, rating.Value.TitleRating);
        Assert.True(rating.Value.HasAny);
    }

    [Fact]
    public void ToChannelItem_AddsXrelTag()
    {
        var release = new Release { Guid = "guid-dune", Title = "Dune.2021" };
        var xrel = new XrelRating(9.2, 8.7, 128, 8.4, "GROUP");

        var item = ReleaseMapper.ToChannelItem(release, 0, default, xrel, out _);

        Assert.NotNull(item);
        Assert.Contains(item!.Tags, t => t.StartsWith("xREL", System.StringComparison.Ordinal) && t.Contains("V9.2", System.StringComparison.Ordinal) && t.Contains("A8.7", System.StringComparison.Ordinal));
    }

    [Fact]
    public void ToChannelItem_UsesXrelTitleRating_WhenIndexerRatingMissing()
    {
        // Release with no movie rating -> xREL title rating fills CommunityRating.
        var release = new Release { Guid = "guid-x", Title = "Something.2024" };
        var xrel = new XrelRating(null, null, 10, 7.5, null);

        var item = ReleaseMapper.ToChannelItem(release, 0, default, xrel, out _);

        Assert.NotNull(item);
        Assert.Equal(7.5f, item!.CommunityRating);
    }
}
