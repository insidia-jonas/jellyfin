using System;
using Jellyfin.LiveTv.TunerHosts;
using MediaBrowser.Model.LiveTv;
using Xunit;

namespace Jellyfin.LiveTv.Tests.TunerHosts;

public class TunerHostManagerTests
{
    [Fact]
    public void EnsureImportedXmlTv_AddsProviderOnce()
    {
        var config = new LiveTvOptions();
        var tuner = new TunerHostInfo
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = "m3u",
            EpgUrl = "https://epg.example.com/guide.xml",
            UserAgent = "VLC/3.0.20"
        };

        TunerHostManager.EnsureImportedXmlTv(config, tuner);
        TunerHostManager.EnsureImportedXmlTv(config, tuner);

        Assert.Single(config.ListingProviders);
        var listing = config.ListingProviders[0];
        Assert.Equal("xmltv", listing.Type);
        Assert.Equal(tuner.EpgUrl, listing.Path);
        Assert.Equal(tuner.UserAgent, listing.UserAgent);
        Assert.False(listing.EnableAllTuners);
        Assert.Equal(tuner.Id, Assert.Single(listing.EnabledTuners));
    }

    [Fact]
    public void EnsureImportedXmlTv_IgnoresMissingEpgUrl()
    {
        var config = new LiveTvOptions();
        var tuner = new TunerHostInfo
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = "m3u"
        };

        TunerHostManager.EnsureImportedXmlTv(config, tuner);

        Assert.Empty(config.ListingProviders);
    }
}
