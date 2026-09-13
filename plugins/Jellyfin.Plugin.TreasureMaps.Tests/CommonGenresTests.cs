using Jellyfin.Plugin.TreasureMaps.Channels;
using Xunit;

namespace Jellyfin.Plugin.TreasureMaps.Tests;

public class CommonGenresTests
{
    [Fact]
    public void FilterAvailable_KeepsOnlyCommonGenres_ExcludingAdultTags()
    {
        var available = new[]
        {
            "Action", "Adult/porn", "Erotica", "Hentai", "BDSM", "Drama",
            "Pornography", "Thriller", "Abduction", "Accounting"
        };

        var result = CommonGenres.FilterAvailable(available);

        Assert.Equal(new[] { "Action", "Drama", "Thriller" }, result);
    }

    [Fact]
    public void FilterAvailable_FallsBackToWhitelist_WhenNothingMatches()
    {
        var result = CommonGenres.FilterAvailable(new string?[] { "Zzz-unknown", null });

        Assert.Equal(CommonGenres.Names.Length, result.Count);
        Assert.DoesNotContain("Zzz-unknown", result);
    }
}
