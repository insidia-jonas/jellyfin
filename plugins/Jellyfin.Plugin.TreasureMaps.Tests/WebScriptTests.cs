using System;
using System.IO;
using Xunit;

namespace Jellyfin.Plugin.TreasureMaps.Tests;

public class WebScriptTests
{
    private static string ScriptPath()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "Jellyfin.Plugin.TreasureMaps", "Web", "treasuremaps.js"));

    [Fact]
    public void ReleaseRows_WrapOnNarrowScreens()
    {
        var js = File.ReadAllText(ScriptPath());
        Assert.Contains("overflow-wrap:anywhere", js, StringComparison.Ordinal);
        Assert.Contains("white-space:normal", js, StringComparison.Ordinal);
        Assert.Contains("@media (max-width:700px)", js, StringComparison.Ordinal);
        Assert.Contains("flex-direction:column", js, StringComparison.Ordinal);
        Assert.Contains("tmRelMeta", js, StringComparison.Ordinal);
        Assert.DoesNotContain("text-overflow:ellipsis", js, StringComparison.Ordinal);
        Assert.Contains(".tmRelName{flex:1 1 12rem", js, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadsList_UsesTitleRowsAndOpenTargets()
    {
        var js = File.ReadAllText(ScriptPath());
        Assert.Contains("#tmDownloads", js, StringComparison.Ordinal);
        Assert.Contains("enhanceDownloadsList", js, StringComparison.Ordinal);
        Assert.Contains("tmDlTitle", js, StringComparison.Ordinal);
        Assert.Contains("tmDlOpen", js, StringComparison.Ordinal);
        Assert.Contains("min-height:7.25rem", js, StringComparison.Ordinal);
        Assert.Contains("params.title = movieTitle", js, StringComparison.Ordinal);
        Assert.Contains("#/details?id=", js, StringComparison.Ordinal);
        Assert.Contains("Download complete|Download failed|Downloading|SABnzbd|Treasure-Maps download", js, StringComparison.Ordinal);
        Assert.DoesNotContain("return looksQuality(item.Name);", js, StringComparison.Ordinal);
    }

    [Fact]
    public void TitlePage_ReplacesPosterGridIncludingAndereInhalte()
    {
        var js = File.ReadAllText(ScriptPath());
        Assert.Contains("#childrenCollapsible", js, StringComparison.Ordinal);
        Assert.Contains("childrenItemsContainer", js, StringComparison.Ordinal);
        Assert.Contains("andere inhalte", js, StringComparison.Ordinal);
        Assert.Contains("hideNativeChildren", js, StringComparison.Ordinal);
        Assert.Contains("pickReleases", js, StringComparison.Ordinal);
        Assert.Contains("tmTitlePage", js, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyScriptTag_InsertsThenUpgradesVersion()
    {
        var html = "<html><body>hi</body></html>";
        var first = WebScriptInjector.ApplyScriptTag(html);
        Assert.NotNull(first);
        Assert.Contains("TreasureMaps/ClientScript?v=4", first, StringComparison.Ordinal);

        var stale = first!.Replace("ClientScript?v=4", "ClientScript?v=3", StringComparison.Ordinal);
        var upgraded = WebScriptInjector.ApplyScriptTag(stale);
        Assert.NotNull(upgraded);
        Assert.Contains("ClientScript?v=4", upgraded, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(upgraded!, "plugin=\"TreasureMaps\""));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }

        return count;
    }
}
