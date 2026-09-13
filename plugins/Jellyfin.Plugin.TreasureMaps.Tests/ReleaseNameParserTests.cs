using Jellyfin.Plugin.TreasureMaps.ReleaseNaming;
using Xunit;

namespace Jellyfin.Plugin.TreasureMaps.Tests;

public class ReleaseNameParserTests
{
    [Fact]
    public void Parse_UserExample_ExtractsAllAttributes()
    {
        var p = ReleaseNameParser.Parse("Pinocchio Unstrung 2026 GERMAN DL 1080P BLURAY AVC-UNDERTAKERS");

        Assert.Equal("1080p", p.Resolution);
        Assert.Equal("BluRay", p.Source);
        Assert.Equal("AVC", p.Codec);
        Assert.True(p.DualLanguage);
        Assert.Contains("German", p.Languages);
        Assert.Equal("UNDERTAKERS", p.Group);
        Assert.Null(p.AudioSource); // BluRay has no MIC/LINE
    }

    [Fact]
    public void Parse_Cam_WithMicAudio_IsLowerQualityThanLine()
    {
        var mic = ReleaseNameParser.Parse("Some.Movie.2026.HDCAM.MIC.x264-GRP");
        var line = ReleaseNameParser.Parse("Some.Movie.2026.HDCAM.LINE.x264-GRP");

        Assert.Equal("CAM", mic.Source);
        Assert.Equal("MIC", mic.AudioSource);
        Assert.Equal("LINE", line.AudioSource);
        // Line audio must rank strictly higher than microphone audio.
        Assert.True(line.QualityScore > mic.QualityScore);
    }

    [Fact]
    public void Parse_Telesync_Detected()
    {
        var p = ReleaseNameParser.Parse("Another.Movie.2026.HDTS.LINE.GERMAN-XYZ");
        Assert.Equal("TELESYNC", p.Source);
        Assert.Equal("LINE", p.AudioSource);
        Assert.Contains("German", p.Languages);
    }

    [Theory]
    [InlineData("Film.2026.2160p.WEB-DL.DDP5.1.HDR.HEVC-ABC", "2160p", "WEB-DL", "HEVC", "HDR")]
    [InlineData("Film.2026.720p.BluRay.x265-DEF", "720p", "BluRay", "HEVC", null)]
    public void Parse_Various(string name, string res, string source, string codec, string? hdr)
    {
        var p = ReleaseNameParser.Parse(name);
        Assert.Equal(res, p.Resolution);
        Assert.Equal(source, p.Source);
        Assert.Equal(codec, p.Codec);
        Assert.Equal(hdr, p.Hdr);
    }

    [Fact]
    public void Parse_Empty_ReturnsEmpty()
    {
        var p = ReleaseNameParser.Parse(null);
        Assert.Null(p.Resolution);
        Assert.Null(p.Source);
        Assert.Empty(p.Languages);
    }
}
