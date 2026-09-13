using System;
using Jellyfin.LiveTv.TunerHosts;
using Xunit;

namespace Jellyfin.LiveTv.Tests.TunerHosts;

public class M3uStreamUrlTests
{
    [Fact]
    public void Split_PipeHeaders_StripsUrlAndMapsNames()
    {
        var (url, headers) = M3uStreamUrl.Split(
            "http://ingest.example/live.ts|user-agent=VLC/3.0.20&referer=https://provider.example&!X-Token=abc");

        Assert.Equal("http://ingest.example/live.ts", url);
        Assert.Equal("VLC/3.0.20", headers["User-Agent"]);
        Assert.Equal("https://provider.example", headers["Referer"]);
        Assert.Equal("abc", headers["X-Token"]);
    }

    [Fact]
    public void Split_AtPrefix_IsRemoved()
    {
        var (url, _) = M3uStreamUrl.Split("@http://page.example/channel.html");
        Assert.Equal("http://page.example/channel.html", url);
    }

    [Fact]
    public void SubstituteLiveNow_UnixSecondsAndFormat()
    {
        var utc = new DateTime(2026, 9, 9, 12, 30, 0, DateTimeKind.Utc);

        Assert.Equal(
            "http://s/live.ts?t=1788957000",
            M3uStreamUrl.SubstituteLiveNow("http://s/live.ts?t={lutc}", utc));
        Assert.Equal(
            "http://s/live.ts?t=20260909123000",
            M3uStreamUrl.SubstituteLiveNow("http://s/live.ts?t={lutc:YmdHMS}", utc));
        Assert.Equal(
            "http://s/live.ts?t=1788957000",
            M3uStreamUrl.SubstituteLiveNow("http://s/live.ts?t=${now}", utc));
    }
}
