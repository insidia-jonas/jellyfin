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
    public void RewriteStreamUrl_ReplacesHostAndScheme()
    {
        var rewritten = M3uUrlFailover.RewriteStreamUrl(
            "http://s1.example/iptv/token/12.ts",
            "https://s2.example/iptv/token/playlist.m3u");

        Assert.Equal("https://s2.example/iptv/token/12.ts", rewritten);
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
}
