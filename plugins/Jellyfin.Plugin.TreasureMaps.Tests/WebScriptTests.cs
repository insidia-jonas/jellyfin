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
    public void ApplyScriptTag_InsertsThenUpgradesVersion()
    {
        var html = "<html><body>hi</body></html>";
        var first = WebScriptInjector.ApplyScriptTag(html);
        Assert.NotNull(first);
        Assert.Contains("TreasureMaps/ClientScript?v=2", first, StringComparison.Ordinal);

        var stale = first!.Replace("ClientScript?v=2", "ClientScript", StringComparison.Ordinal);
        var upgraded = WebScriptInjector.ApplyScriptTag(stale);
        Assert.NotNull(upgraded);
        Assert.Contains("ClientScript?v=2", upgraded, StringComparison.Ordinal);
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
