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

public class GrabRecordListTests
{
    [Fact]
    public void ListRecent_DedupesByTitle_NewestFirst()
    {
        ResetTempGrabStore();
        var service = new GrabService(null!, null!, new Microsoft.Extensions.Logging.Abstractions.NullLogger<GrabService>());
        service.RegisterGrab(new[] { "nzo-old" }, "Silo", null, "Silo", "720p", "g1", "tv");
        service.RegisterGrab(new[] { "nzo-new" }, "Silo", "https://img/silo.jpg", "Silo", "1080p", "g2", "tv");
        service.RegisterGrab(new[] { "nzo-oak" }, "The End of Oak Street", null, "The End of Oak Street", "1080p", "g3", "movie");

        var recent = service.ListRecent(10);
        Assert.Equal(2, recent.Count);
        Assert.Contains(recent, r => r.Title == "The End of Oak Street");
        var silo = Assert.Single(recent, r => r.Title == "Silo");
        Assert.Equal("nzo-new", silo.NzoId);
    }

    [Fact]
    public void Forget_RemovesEveryRowForTheTitle()
    {
        ResetTempGrabStore();
        var service = new GrabService(null!, null!, new Microsoft.Extensions.Logging.Abstractions.NullLogger<GrabService>());
        service.RegisterGrab(new[] { "nzo-a" }, "The End of Oak Street", null, "The End of Oak Street", "1080p", "g1", "movie");
        service.RegisterGrab(new[] { "nzo-b" }, "The End of Oak Street", null, "The End of Oak Street", "720p", "g2", "movie");
        service.RegisterGrab(new[] { "nzo-keep" }, "Silo", null, "Silo", "1080p", "g3", "tv");

        Assert.Equal(2, service.ListNzoIds(null, "The End of Oak Street").Count);
        Assert.Equal(2, service.Forget(null, "The End of Oak Street"));
        Assert.False(service.IsTracked("nzo-a", "The End of Oak Street"));
        Assert.False(service.IsTracked("nzo-b", "The End of Oak Street"));
        Assert.True(service.IsTracked("nzo-keep", "Silo"));
        Assert.Single(service.ListRecent(10));
    }

    private static void ResetTempGrabStore()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "treasuremaps-grabs.json");
        if (System.IO.File.Exists(path))
        {
            System.IO.File.Delete(path);
        }
    }
}

public class DownloadListTests
{
    [Fact]
    public void DedupeByTitle_KeepsOneRow_PrefersActive()
    {
        var rows = new[]
        {
            new { Title = "The End of Oak Street", Status = "Completed", Id = "old" },
            new { Title = "The End of Oak Street", Status = "Downloading", Id = "live" },
            new { Title = "The End of Oak Street", Status = "Completed", Id = "dup" },
            new { Title = "Silo", Status = "Completed", Id = "silo" }
        };

        var unique = DownloadList.DedupeByTitle(rows, r => r.Title, r => r.Status);
        Assert.Equal(2, unique.Count);
        Assert.Equal("live", unique[0].Id);
        Assert.Equal("silo", unique[1].Id);
    }
}
