using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.LiveTv.TunerHosts;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.LiveTv.Tests.TunerHosts;

public class M3uParserTests
{
    [Fact]
    public async Task ParsePlaylist_ReadsEpgUserAgentAndChannels()
    {
        var playlist = await ParseFileAsync("Test Data/LiveTv/m3u/iptv-sample.m3u");

        Assert.Equal("https://epg.example.com/guide.xml", playlist.EpgUrl);
        Assert.Equal("VLC/3.0.20", playlist.UserAgent);
        Assert.Equal("https://provider.example", playlist.Referrer);
        Assert.Equal(2, playlist.Channels.Count);

        var cnn = playlist.Channels[0];
        Assert.Equal("CNN HD", cnn.Name);
        Assert.Equal("cnn.us", cnn.TunerChannelId);
        Assert.Equal("cnn.us", cnn.CallSign);
        Assert.Equal("News", cnn.ChannelGroup);
        Assert.Equal("https://logo.example/cnn.png", cnn.ImageUrl);
        Assert.Equal("http://stream.example.com/cnn.ts", cnn.Path);

        var bbc = playlist.Channels[1];
        Assert.Equal("BBC One", bbc.Name);
        Assert.Equal("2", bbc.Number);
        Assert.Equal("bbc.uk", bbc.TunerChannelId);
        Assert.Equal("https://stream.example.com/bbc.m3u8", bbc.Path);
    }

    [Fact]
    public async Task ParsePlaylist_UsesXTvgUrlWhenUrlTvgMissing()
    {
        var playlist = await ParseFileAsync("Test Data/LiveTv/m3u/iptv-xtvg-only.m3u");

        Assert.Equal("https://alt.example.com/xmltv.xml", playlist.EpgUrl);
        Assert.Single(playlist.Channels);
        Assert.Equal("Demo Channel", playlist.Channels[0].Name);
    }

    private static Task<M3uPlaylist> ParseFileAsync(string relativePath)
    {
        var parser = new M3uParser(Mock.Of<ILogger>(), Mock.Of<IHttpClientFactory>());
        var info = new TunerHostInfo
        {
            Id = "tuner1",
            Type = "m3u",
            Url = Path.Combine(relativePath)
        };

        return parser.ParsePlaylist(info, "m3u_", CancellationToken.None);
    }
}
