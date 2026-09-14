using Jellyfin.LiveTv.TunerHosts;
using Xunit;

namespace Jellyfin.LiveTv.Tests.TunerHosts;

public class SharedHttpStreamIngestTests
{
    [Fact]
    public void Capture_KeepsIptvUrl()
    {
        Assert.Equal("http://localhost:8778/live.ts", SharedHttpStreamIngest.Capture("http://localhost:8778/live.ts"));
    }

    [Fact]
    public void Capture_RejectsPublishedLiveStreamFilesPath()
    {
        Assert.Null(SharedHttpStreamIngest.Capture("http://172.30.0.2:8096/LiveTv/LiveStreamFiles/abc/stream.ts"));
    }

    [Fact]
    public void ForFirstRead_UsesCapturedUrlAfterPathRewrite()
    {
        var ingest = "http://localhost:8778/live.ts";
        var rewritten = "http://172.30.0.2:8096/LiveTv/LiveStreamFiles/abc/stream.ts";

        Assert.Equal(ingest, SharedHttpStreamIngest.ForFirstRead(ingest, rewritten));
    }

    [Fact]
    public void ForFirstRead_DoesNotLoopOnLiveStreamFiles()
    {
        var published = "http://127.0.0.1:8096/LiveTv/LiveStreamFiles/abc/stream.ts";

        Assert.Null(SharedHttpStreamIngest.ForFirstRead(published, published));
        Assert.Null(SharedHttpStreamIngest.ForFirstRead(null, published));
    }

    [Fact]
    public void ForFirstRead_FallsBackToCurrentPathWhenCaptureMissing()
    {
        Assert.Equal(
            "http://ingest.example/live/arte.ts",
            SharedHttpStreamIngest.ForFirstRead(null, "http://ingest.example/live/arte.ts"));
    }
}
