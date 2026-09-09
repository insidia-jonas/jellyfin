using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.LiveTv.TunerHosts;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
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

    [Fact]
    public async Task ProbeAsync_IngestHost_DoesNotSendHttp()
    {
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var checker = new M3uPlaylistHealthChecker(factory.Object, NullLogger<M3uPlaylistHealthChecker>.Instance);

        var result = await checker.ProbeAsync(
            "http://nl01.example",
            new TunerHostInfo(),
            CancellationToken.None);

        Assert.False(result.Success);
        factory.Verify(f => f.CreateClient(It.IsAny<string>()), Times.Never);
    }
}
