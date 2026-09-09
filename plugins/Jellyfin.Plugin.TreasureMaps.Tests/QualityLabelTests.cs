using Jellyfin.Plugin.TreasureMaps.Api;
using Jellyfin.Plugin.TreasureMaps.Channels;
using Xunit;

namespace Jellyfin.Plugin.TreasureMaps.Tests;

public class QualityLabelTests
{
    [Fact]
    public void BuildQualityLabel_BuildsBadgesFromSceneName()
    {
        var release = new Release
        {
            Guid = "g1",
            Title = "Pinocchio.Unstrung.2026.GERMAN.DL.1080p.BLURAY.AVC-UNDERTAKERS",
            Size = 10520000000
        };

        var label = ReleaseMapper.BuildQualityLabel(release);

        Assert.Equal("1080p \u00b7 BluRay \u00b7 AVC \u00b7 German \u00b7 DL \u00b7 9.8 GB \u00b7 [UNDERTAKERS]", label);
    }

    [Fact]
    public void BuildQualityLabel_IncludesHdrAndAudioSource()
    {
        var release = new Release
        {
            Guid = "g2",
            Title = "Some.Movie.2026.2160p.WEBDL.HEVC.DV.LINE-GRP"
        };

        var label = ReleaseMapper.BuildQualityLabel(release);

        Assert.Equal("2160p \u00b7 WEB-DL \u00b7 LINE \u00b7 HEVC \u00b7 DV \u00b7 [GRP]", label);
    }

    [Fact]
    public void BuildQualityLabel_FallsBackToSceneName_WhenNothingParsed()
    {
        var release = new Release { Guid = "g3", Title = "Mystery Item Without Tokens" };

        var label = ReleaseMapper.BuildQualityLabel(release);

        Assert.Equal("Mystery Item Without Tokens", label);
    }
}
