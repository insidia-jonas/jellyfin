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
        Assert.Contains("resolvePlaybackManager", js, StringComparison.Ordinal);
        Assert.Contains("playableItem", js, StringComparison.Ordinal);
        Assert.Contains("webpackChunk", js, StringComparison.Ordinal);
        Assert.Contains("playbackmanager.js", js, StringComparison.Ordinal);
        Assert.Contains("Type = 'TvChannel'", js, StringComparison.Ordinal);
        Assert.Contains("NativePlayer.loadPlayer", js, StringComparison.Ordinal);
        Assert.Contains("#/list?parentId=", js, StringComparison.Ordinal);
        Assert.Contains("&ltvgroup=1", js, StringComparison.Ordinal);
        Assert.Contains("split('?')[0]", js, StringComparison.Ordinal);
        Assert.Contains("getUrl('LiveTv/Channels'", js, StringComparison.Ordinal);
        Assert.Contains("client.getItems", js, StringComparison.Ordinal);
        Assert.Contains("never the IPTV playlist", js, StringComparison.Ordinal);
        Assert.DoesNotContain("TreasureMaps/Search", js, StringComparison.Ordinal);
        Assert.DoesNotContain(".m3u", js, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("requestAnimationFrame", js, StringComparison.Ordinal);
        Assert.Contains("ROW_CHUNK", js, StringComparison.Ordinal);
        Assert.Contains("render('progress')", js, StringComparison.Ordinal);
        Assert.Contains("hideLibrarySpinner", js, StringComparison.Ordinal);
        Assert.Contains("html.jf-livetv-list-on .loading", js, StringComparison.Ordinal);
        Assert.Contains("indexOf('g:') === 0", js, StringComparison.Ordinal);
        Assert.Contains("isLiveTvGroupHash", js, StringComparison.Ordinal);
        Assert.Contains("loadGen", js, StringComparison.Ordinal);
        Assert.Contains("emptyRetry", js, StringComparison.Ordinal);
        Assert.Contains("EMPTY_RETRY_MS", js, StringComparison.Ordinal);
        Assert.Contains("scheduleEmptyRetry", js, StringComparison.Ordinal);
        Assert.Contains("window.loading.hide", js, StringComparison.Ordinal);
        Assert.Contains("visibleHost", js, StringComparison.Ordinal);
        Assert.Contains("loginPage", js, StringComparison.Ordinal);
        Assert.Contains("overlayOnVisiblePage", js, StringComparison.Ordinal);
        Assert.Contains("host.contains(box)", js, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_NeverNavigatesToTheDetailsPage()
    {
        // Clicking a sender used to end on #/details whenever playback could not be
        // reached, which renders as the gray poster placeholder a Live TV item has
        // instead of artwork. Failures belong in the list, not on a dead-end page.
        foreach (var line in File.ReadAllLines(ScriptPath()))
        {
            var code = line.Trim();
            if (code.StartsWith("/*", StringComparison.Ordinal)
                || code.StartsWith('*')
                || code.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.DoesNotContain("#/details", code, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("itemdetails", code, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Script_ForcesADecodableTranscodeForLiveSources()
    {
        // The progressive URL the server hands out carries no codec, so for a live
        // source it has not probed it falls back to a stream copy. An mpeg2video mux
        // copied into mp4 gives a <video> element whose videoWidth stays 0 forever.
        var js = File.ReadAllText(ScriptPath());
        Assert.Contains("forceProgressiveTranscode", js, StringComparison.Ordinal);
        Assert.Contains("videocodec: 'h264'", js, StringComparison.Ordinal);
        Assert.Contains("audiocodec: 'aac'", js, StringComparison.Ordinal);
        Assert.Contains("FORCED_STREAM_PARAMS", js, StringComparison.Ordinal);
        Assert.Contains("path += '.mp4'", js, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_TreatsPicturelessPlaybackAsFailure()
    {
        // Reporting success on readyState alone is how a run claimed "play works"
        // with videoWidth 0: an mpeg2 stream copy decodes audio and never paints.
        var js = File.ReadAllText(ScriptPath());
        Assert.Contains("elementState", js, StringComparison.Ordinal);
        Assert.Contains("element.videoWidth > 0 && element.readyState >= 2", js, StringComparison.Ordinal);
        Assert.Contains("if (how === 'video')", js, StringComparison.Ordinal);
        Assert.Contains("hasVideoStream", js, StringComparison.Ordinal);
        Assert.Contains("showError", js, StringComparison.Ordinal);
        Assert.Contains("produced no video", js, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_RecoversTheListAfterPlaybackEnds()
    {
        // jellyfin-web keeps .videoOsdBottom mounted with display:none once anything
        // has played, and leaves .videoPlayerContainer on screen when a load never
        // produced a frame. Both used to leave the senders hidden behind a blank page.
        var js = File.ReadAllText(ScriptPath());
        Assert.Contains("onScreen", js, StringComparison.Ordinal);
        Assert.Contains("getBoundingClientRect", js, StringComparison.Ordinal);
        Assert.Contains("hideOrphanPlayerView", js, StringComparison.Ordinal);
        Assert.Contains("managerIdle", js, StringComparison.Ordinal);
        Assert.Contains("leavePlayerView", js, StringComparison.Ordinal);
        Assert.Contains("popstate", js, StringComparison.Ordinal);
        Assert.Contains("modeTimer = window.setInterval(applyListMode", js, StringComparison.Ordinal);
        Assert.Contains("paintError", js, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_ReleasesTheTunerBeforeSwitchingSenders()
    {
        // An M3U host with TunerCount 1 - the normal IPTV subscription - rejects the
        // next channel while the previous live stream is still open.
        var js = File.ReadAllText(ScriptPath());
        Assert.Contains("releaseTuner", js, StringComparison.Ordinal);
        Assert.Contains("LiveStreams/Close", js, StringComparison.Ordinal);
        Assert.Contains("Videos/ActiveEncodings", js, StringComparison.Ordinal);
        Assert.Contains("return releaseTuner().then(", js, StringComparison.Ordinal);
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
