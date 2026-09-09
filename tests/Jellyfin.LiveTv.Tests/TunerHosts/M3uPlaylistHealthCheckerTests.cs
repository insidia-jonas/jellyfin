using Jellyfin.LiveTv.TunerHosts;
using Xunit;

namespace Jellyfin.LiveTv.Tests.TunerHosts;

public class M3uPlaylistHealthCheckerTests
{
    [Fact]
    public void Score_FailedProbe_IsMinusOne()
    {
        Assert.Equal(-1, M3uPlaylistHealthChecker.Score(false, 10));
    }

    [Fact]
    public void Score_FasterHost_Wins()
    {
        var fast = M3uPlaylistHealthChecker.Score(true, 50);
        var slow = M3uPlaylistHealthChecker.Score(true, 500);
        Assert.True(fast > slow);
        Assert.True(fast > 0);
    }
}
