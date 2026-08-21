using System;
using System.Linq;
using Jellyfin.Plugin.TreasureMaps.Recommendations;
using Xunit;

namespace Jellyfin.Plugin.TreasureMaps.Tests;

public class AiRecommenderTests
{
    [Fact]
    public void ParseRecommendations_PlainJsonArray()
    {
        var text = "[{\"title\":\"Dune\",\"year\":2021,\"type\":\"movie\",\"reason\":\"Epic sci-fi.\"}]";

        var result = AiRecommender.ParseRecommendations(text);

        var rec = Assert.Single(result);
        Assert.Equal("Dune", rec.Title);
        Assert.Equal(2021, rec.Year);
        Assert.Equal("movie", rec.Type);
        Assert.Equal("Epic sci-fi.", rec.Reason);
    }

    [Fact]
    public void ParseRecommendations_ToleratesMarkdownFencesAndProse()
    {
        var text = "Here are my picks:\n```json\n[\n {\"title\":\"Severance\",\"type\":\"tv\",\"reason\":\"Mind-bending.\"},\n {\"title\":\"Heat\",\"year\":1995,\"type\":\"movie\"}\n]\n```\nEnjoy!";

        var result = AiRecommender.ParseRecommendations(text);

        Assert.Equal(2, result.Count);
        Assert.Equal("Severance", result[0].Title);
        Assert.Equal("tv", result[0].Type);
        Assert.Equal(1995, result[1].Year);
    }

    [Fact]
    public void ParseRecommendations_GarbageYieldsEmpty()
    {
        Assert.Empty(AiRecommender.ParseRecommendations("Sorry, I cannot help with that."));
        Assert.Empty(AiRecommender.ParseRecommendations("[not json"));
        Assert.Empty(AiRecommender.ParseRecommendations(null));
    }

    [Fact]
    public void BuildPrompt_ContainsHistoryAndJsonShape()
    {
        var prompt = AiRecommender.BuildPrompt(
            new[] { "Dune (2021) [movie]" },
            new[] { "Severance" },
            12);

        Assert.Contains("Dune (2021) [movie]", prompt, StringComparison.Ordinal);
        Assert.Contains("Severance", prompt, StringComparison.Ordinal);
        Assert.Contains("exactly 12", prompt, StringComparison.Ordinal);
        Assert.Contains("JSON array", prompt, StringComparison.Ordinal);
    }
}
