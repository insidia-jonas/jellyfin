using System;
using System.IO;
using Jellyfin.LiveTv.Web;
using Xunit;

namespace Jellyfin.LiveTv.Tests.Channels;

public class LiveTvWebOverviewTests
{
    private static string ScriptPath()
    {
        var dir = AppContext.BaseDirectory;
        var current = new DirectoryInfo(dir);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "src", "Jellyfin.LiveTv", "Web", "livetv-overview.js");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException("livetv-overview.js");
    }

    [Fact]
    public void Script_ReplacesPosterGridWithListRows()
    {
        var js = File.ReadAllText(ScriptPath());
        Assert.Contains("jf-livetv-overview", js, StringComparison.Ordinal);
        Assert.Contains("jf-livetv-row", js, StringComparison.Ordinal);
        Assert.Contains("jf-livetv-bar", js, StringComparison.Ordinal);
        Assert.Contains("jf-livetv-next", js, StringComparison.Ordinal);
        Assert.Contains("progressOf", js, StringComparison.Ordinal);
        Assert.Contains("Jetzt:", js, StringComparison.Ordinal);
        Assert.Contains("Danach:", js, StringComparison.Ordinal);
        Assert.Contains("TreasureMaps", js, StringComparison.Ordinal);
        Assert.Contains("layout-tv", js, StringComparison.Ordinal);
        Assert.Contains("tabIndex = 0", js, StringComparison.Ordinal);
        Assert.Contains("Keine Sender", js, StringComparison.Ordinal);
        Assert.Contains("Live TV konnte nicht geladen werden", js, StringComparison.Ordinal);
        Assert.Contains("FireTvLive", js, StringComparison.Ordinal);
        Assert.Contains("#/list?parentId=", js, StringComparison.Ordinal);
        Assert.Contains("getUrl('LiveTv/Channels'", js, StringComparison.Ordinal);
        Assert.Contains("client.getItems", js, StringComparison.Ordinal);
        Assert.Contains("never the IPTV playlist", js, StringComparison.Ordinal);
        Assert.DoesNotContain("TreasureMaps/Search", js, StringComparison.Ordinal);
        Assert.DoesNotContain(".m3u", js, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("requestAnimationFrame", js, StringComparison.Ordinal);
        Assert.Contains("ROW_CHUNK", js, StringComparison.Ordinal);
        Assert.Contains("render('progress')", js, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyScriptTag_InsertsThenUpgradesVersion()
    {
        var html = "<html><body>hi</body></html>";
        var first = LiveTvWebScriptInjector.ApplyScriptTag(html);
        Assert.NotNull(first);
        Assert.Contains("plugin=\"LiveTvOverview\"", first, StringComparison.Ordinal);
        Assert.Contains("livetv-overview.js?v=" + LiveTvWebScriptInjector.ScriptVersion, first, StringComparison.Ordinal);

        var stale = first!.Replace("livetv-overview.js?v=" + LiveTvWebScriptInjector.ScriptVersion, "livetv-overview.js?v=0", StringComparison.Ordinal);
        var upgraded = LiveTvWebScriptInjector.ApplyScriptTag(stale);
        Assert.NotNull(upgraded);
        Assert.Contains("livetv-overview.js?v=" + LiveTvWebScriptInjector.ScriptVersion, upgraded, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(upgraded!, "plugin=\"LiveTvOverview\""));
    }

    [Fact]
    public void ApplyScriptTag_ReturnsNullWithoutBody()
    {
        Assert.Null(LiveTvWebScriptInjector.ApplyScriptTag("<html>nope</html>"));
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
