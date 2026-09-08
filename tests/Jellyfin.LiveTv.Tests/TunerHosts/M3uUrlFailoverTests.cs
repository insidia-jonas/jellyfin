using System;
using Jellyfin.LiveTv.TunerHosts;
using MediaBrowser.Model.LiveTv;
using Xunit;

namespace Jellyfin.LiveTv.Tests.TunerHosts;

public class M3uUrlFailoverTests
{
    [Fact]
    public void SplitAndNormalize_PipeSeparatedUrls_StoresAlternates()
    {
        var info = new TunerHostInfo
        {
            Url = "http://s1.example/pl.m3u|http://s2.example/pl.m3u|http://s3.example/pl.m3u"
        };

        M3uUrlFailover.NormalizeTunerUrls(info);

        Assert.Equal("http://s1.example/pl.m3u", info.Url);
        Assert.Equal(["http://s2.example/pl.m3u", "http://s3.example/pl.m3u"], info.AlternateUrls);
        Assert.Equal("http://s1.example/pl.m3u", info.ActiveUrl);
        Assert.Equal(3, M3uUrlFailover.GetCandidateUrls(info).Count);
    }

    [Fact]
    public void Normalize_MixedPlaylistAndIngestHosts_KeepsListingUrl()
    {
        var info = new TunerHostInfo
        {
            Url = "http://cdn.example/iptv/p/token/list.m3u?p=1|http://nl01.example|http://am01.example"
        };

        M3uUrlFailover.NormalizeTunerUrls(info);

        Assert.Equal("http://cdn.example/iptv/p/token/list.m3u?p=1", info.Url);
        Assert.Equal(["http://nl01.example", "http://am01.example"], info.AlternateUrls);
        Assert.True(M3uUrlFailover.IsIngestEndpoint("http://nl01.example"));
        Assert.False(M3uUrlFailover.IsIngestEndpoint("http://cdn.example/iptv/p/token/list.m3u?p=1"));
        Assert.Equal("http://cdn.example/iptv/p/token/list.m3u?p=1", M3uUrlFailover.GetPlaylistUrl(info));
        Assert.Equal(["http://nl01.example", "http://am01.example"], M3uUrlFailover.GetHealthCandidates(info));
        Assert.Equal("http://nl01.example", info.ActiveUrl);
        Assert.Equal("http://nl01.example", M3uUrlFailover.GetPrimaryUrl(info));
    }

    [Fact]
    public void RewriteStreamUrl_IngestHost_ReplacesOnlyHost()
    {
        var rewritten = M3uUrlFailover.RewriteStreamUrl(
            "http://am01.example/9209/mpegts?token=abc",
            "http://nl01.example");

        Assert.Equal("http://nl01.example/9209/mpegts?token=abc", rewritten);
    }

    [Fact]
    public void RewriteStreamUrl_ReplacesHostAndScheme()
    {
        var rewritten = M3uUrlFailover.RewriteStreamUrl(
            "http://s1.example/iptv/token/12.ts",
            "https://s2.example/iptv/token/playlist.m3u");

        Assert.Equal("https://s2.example/iptv/token/12.ts", rewritten);
    }

    [Fact]
    public void RewriteStreamUrl_DoesNotPointRegionalStreamAtPlaylistCdn()
    {
        var rewritten = M3uUrlFailover.RewriteStreamUrl(
            "http://am01.example/9209/mpegts?token=abc",
            "http://cdn.example/iptv/p/token/Sharavoz.Tv.Kodi.m3u?p=1");

        Assert.Equal("http://am01.example/9209/mpegts?token=abc", rewritten);
    }

    [Fact]
    public void RewriteStreamUrl_ListingOnlyPrimaryUrl_KeepsRegionalHost()
    {
        var info = new TunerHostInfo
        {
            Url = "http://cdn.example/iptv/p/token/list.m3u?p=1"
        };

        M3uUrlFailover.NormalizeTunerUrls(info);

        var rewritten = M3uUrlFailover.RewriteStreamUrl(
            "http://am01.example/9209/mpegts?token=abc",
            M3uUrlFailover.GetPrimaryUrl(info));

        Assert.Equal("http://cdn.example/iptv/p/token/list.m3u?p=1", info.Url);
        Assert.Equal("http://cdn.example/iptv/p/token/list.m3u?p=1", M3uUrlFailover.GetPrimaryUrl(info));
        Assert.Equal("http://am01.example/9209/mpegts?token=abc", rewritten);
    }

    [Fact]
    public void ShouldRewriteStreamHost_IngestAuthorityAlone_IsNotEnough()
    {
        var stream = new Uri("http://am01.example/9209/mpegts?token=abc");
        var listing = new Uri("http://cdn.example/iptv/p/token/list.m3u?p=1");
        var ingest = new Uri("http://nl01.example/");

        Assert.False(M3uUrlFailover.ShouldRewriteStreamHost(stream, listing));
        Assert.True(M3uUrlFailover.ShouldRewriteStreamHost(stream, ingest));
    }

    [Fact]
    public void StableStreamKey_IgnoresHost()
    {
        var a = M3uUrlFailover.StableStreamKey("http://s1.example/iptv/token/12.ts");
        var b = M3uUrlFailover.StableStreamKey("http://s2.example/iptv/token/12.ts");

        Assert.Equal(a, b);
        Assert.Equal("/iptv/token/12.ts", a);
    }

    [Fact]
    public void GetNextUrl_WrapsAround()
    {
        string[] urls = ["http://a", "http://b", "http://c"];

        Assert.Equal("http://b", M3uUrlFailover.GetNextUrl(urls, "http://a"));
        Assert.Equal("http://a", M3uUrlFailover.GetNextUrl(urls, "http://c"));
    }

    [Fact]
    public void GetHangTimeout_UsesDefaultWhenUnset()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), M3uUrlFailover.GetHangTimeout(new TunerHostInfo { HangTimeoutSeconds = 0 }));
        Assert.Equal(TimeSpan.FromSeconds(12), M3uUrlFailover.GetHangTimeout(new TunerHostInfo { HangTimeoutSeconds = 12 }));
    }

    [Fact]
    public void ShouldSwitchActiveUrl_SwitchesWhenCurrentFailed()
    {
        var current = new M3uPlaylistHealthResult { Url = "http://a", Success = false, Score = -1 };
        var best = new M3uPlaylistHealthResult { Url = "http://b", Success = true, Score = 1.5 };

        Assert.True(M3uUrlFailover.ShouldSwitchActiveUrl("http://a", current, best));
    }

    [Fact]
    public void ShouldSwitchActiveUrl_SwitchesWhenOtherScoreIsBetter()
    {
        var current = new M3uPlaylistHealthResult { Url = "http://a", Success = true, Score = 0.4 };
        var best = new M3uPlaylistHealthResult { Url = "http://b", Success = true, Score = 1.2 };

        Assert.True(M3uUrlFailover.ShouldSwitchActiveUrl("http://a", current, best));
    }

    [Fact]
    public void ShouldSwitchActiveUrl_KeepsCurrentWhenItIsBest()
    {
        var current = new M3uPlaylistHealthResult { Url = "http://a", Success = true, Score = 2 };
        Assert.False(M3uUrlFailover.ShouldSwitchActiveUrl("http://a", current, current));
    }

    [Fact]
    public void ShouldSwitchAfterHang_RequiresTwoConfirmedHangs()
    {
        Assert.False(M3uUrlFailover.ShouldSwitchAfterHang(1));
        Assert.True(M3uUrlFailover.ShouldSwitchAfterHang(2));
    }

    [Fact]
    public void IsHls_DetectsPlaylistAndPath()
    {
        Assert.True(M3uUrlFailover.IsHls("http://s/live.m3u8"));
        Assert.True(M3uUrlFailover.IsHls("http://s/stream", "hls"));
        Assert.False(M3uUrlFailover.IsHls("http://s/iptv/token/12"));
    }
}
