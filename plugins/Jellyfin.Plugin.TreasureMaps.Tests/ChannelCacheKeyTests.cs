using System;
using Jellyfin.Plugin.TreasureMaps.Listing;
using Xunit;

namespace Jellyfin.Plugin.TreasureMaps.Tests;

public class ChannelCacheKeyTests
{
    [Fact]
    public void Build_IsStableInsideBrowseEpoch()
    {
        var epoch = TreasureMapsListingCache.BrowseFreshTtl;
        var start = new DateTimeOffset(epoch.Ticks * 4000, TimeSpan.Zero);
        var same = start.Add(epoch).AddTicks(-1);

        var first = TreasureMapsChannelCacheKey.Build("user", "38|de", 0, start);
        var second = TreasureMapsChannelCacheKey.Build("user", "38|de", 0, same);

        Assert.Equal(first, second);
        Assert.Contains("38|de", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ChangesWhenGenerationChanges_NotWhenClockMoves()
    {
        var epoch = TreasureMapsListingCache.BrowseFreshTtl;
        var start = new DateTimeOffset(epoch.Ticks * 4000, TimeSpan.Zero);
        var next = start.Add(epoch).AddHours(3);

        var before = TreasureMapsChannelCacheKey.Build("user", "38", 0, start);
        var afterEpoch = TreasureMapsChannelCacheKey.Build("user", "38", 0, next);
        var afterInvalidate = TreasureMapsChannelCacheKey.Build("user", "38", 1, start);

        Assert.Equal(before, afterEpoch);
        Assert.NotEqual(before, afterInvalidate);
    }

    [Fact]
    public void Build_DoesNotUseTwoMinuteBucket()
    {
        var epoch = TreasureMapsListingCache.BrowseFreshTtl;
        var t0 = new DateTimeOffset(epoch.Ticks * 4000, TimeSpan.Zero);
        var t1 = t0.AddMinutes(2);
        var a = TreasureMapsChannelCacheKey.Build(null, "38", 0, t0);
        var b = TreasureMapsChannelCacheKey.Build(null, "38", 0, t1);
        var twoMinuteBucketChanged = (t0.UtcTicks / TimeSpan.FromMinutes(2).Ticks)
                                     != (t1.UtcTicks / TimeSpan.FromMinutes(2).Ticks);

        Assert.True(twoMinuteBucketChanged);
        Assert.Equal(a, b);
    }
}
