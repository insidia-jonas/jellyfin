using Xunit;

namespace Jellyfin.Plugin.TreasureMaps.Tests;

public class DownloadTitleTests
{
    [Theory]
    [InlineData("1080p · WEB-DL · AVC · DL · 3.66 GB")]
    [InlineData("⬇ 61% – 1080p · WEB-DL · DL")]
    [InlineData("Start download")]
    [InlineData("Download starten")]
    [InlineData("4k")]
    public void LooksLikeQualityLabel_DetectsBadges(string name)
    {
        Assert.True(DownloadTitle.LooksLikeQualityLabel(name));
    }

    [Theory]
    [InlineData("The Godfather")]
    [InlineData("Silo.S03E08.GERMAN.DL.1080p.WEB.H264")]
    [InlineData("Bury the Devil")]
    public void LooksLikeQualityLabel_AllowsTitlesAndSceneNames(string name)
    {
        Assert.False(DownloadTitle.LooksLikeQualityLabel(name));
    }

    [Fact]
    public void Resolve_PrefersStoredMovieTitle()
    {
        Assert.Equal("The Godfather", DownloadTitle.Resolve("1080p · WEB-DL · AVC · DL · 3.66 GB", "The Godfather"));
    }

    [Fact]
    public void Resolve_CleansSceneName_WhenNoStoredTitle()
    {
        var cleaned = DownloadTitle.Resolve("Silo.S03E08.GERMAN.DL.1080p.WEB.H264-GROUP", null);
        Assert.DoesNotContain("1080p", cleaned, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Silo", cleaned, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_QualityOnly_WithoutStore_IsGeneric()
    {
        Assert.Equal("Download", DownloadTitle.Resolve("1080p · WEB-DL · AVC · DL · 3.66 GB", null));
    }

    [Fact]
    public void NameKey_StripsPunctuation()
    {
        Assert.Equal("thegodfather", GrabService.NameKey("The Godfather"));
        Assert.Equal("1080pwebdl", GrabService.NameKey("1080p · WEB-DL"));
    }
}
