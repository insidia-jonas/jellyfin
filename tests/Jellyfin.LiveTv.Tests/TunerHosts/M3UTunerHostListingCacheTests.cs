using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.LiveTv.Channels;
using Jellyfin.LiveTv.Tests;
using Jellyfin.LiveTv.TunerHosts;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;

namespace Jellyfin.LiveTv.Tests.TunerHosts;

[Collection(LiveTvChannelSetIdentityCollection.Name)]
public sealed class M3UTunerHostListingCacheTests
{
    private readonly string _cachePath = Path.Combine(Path.GetTempPath(), "jf-m3u-cache-" + Guid.NewGuid().ToString("N"));

    public M3UTunerHostListingCacheTests()
    {
        Directory.CreateDirectory(_cachePath);
        LiveTvChannelSetIdentity.Reset();
    }

    [Fact]
    public async Task GetChannels_SecondOpenAndGroupFilter_DoesNotParsePlaylistAgain()
    {
        var playlistUrl = "https://cdn.example/playlist.m3u";
        var requests = new List<Uri>();
        var tuner = new TunerHostInfo
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = "m3u",
            Url = playlistUrl
        };
        var host = CreateHost(tuner, requests, """
            #EXTM3U
            #EXTINF:-1 tvg-id="cnn.us" group-title="News",CNN
            http://ingest.example/cnn.ts
            #EXTINF:-1 tvg-id="film.de" group-title="Movies",Film
            http://ingest.example/film.ts
            """);

        var first = await host.GetChannels(tuner, true, CancellationToken.None);
        var second = await host.GetChannels(tuner, true, CancellationToken.None);
        var grouped = LiveTvLibraryChannelItems.Build(second, LiveTvLibraryChannelItems.EncodeGroupId("News"));

        Assert.Equal(2, first.Count);
        Assert.Equal(first.Count, second.Count);
        Assert.Single(grouped);
        Assert.Equal("CNN", grouped[0].Name);
        Assert.Single(requests);
        Assert.Equal(playlistUrl, requests[0].AbsoluteUri);
        Assert.All(requests, uri =>
        {
            Assert.DoesNotContain("ingest.example", uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".ts", uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task GetChannelItems_ColdRootFetchesPlaylistOnce_GroupDoesNot()
    {
        var playlistUrl = "https://cdn.example/playlist.m3u";
        var requests = new List<Uri>();
        var tuner = new TunerHostInfo
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = "m3u",
            Url = playlistUrl
        };
        var host = CreateHost(tuner, requests, """
            #EXTM3U
            #EXTINF:-1 tvg-id="cnn.us" group-title="News",CNN
            http://ingest.example/cnn.ts
            #EXTINF:-1 tvg-id="film.de" group-title="Movies",Film
            http://ingest.example/film.ts
            """);
        host.EnableBackgroundListingRefresh = true;
        host.FirstLoadWait = TimeSpan.FromSeconds(2);

        var manager = new Mock<ITunerHostManager>();
        manager.Setup(m => m.TunerHosts).Returns([host]);
        var channel = new LiveTvLibraryChannel(
            manager.Object,
            Mock.Of<ILibraryManager>(),
            Mock.Of<IUserManager>(),
            NullLogger<LiveTvLibraryChannel>.Instance);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var root = await channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Contains(root.Items, item => item.Name == "News");
        Assert.Contains(root.Items, item => item.Name == "Movies");
        Assert.False(root.RefreshPending);
        Assert.Equal(playlistUrl, Assert.Single(requests).AbsoluteUri);

        var group = await channel.GetChannelItems(
            new InternalChannelItemQuery { FolderId = LiveTvLibraryChannelItems.EncodeGroupId("News") },
            CancellationToken.None);

        Assert.Equal("CNN", Assert.Single(group.Items).Name);
        Assert.False(group.RefreshPending);
        Assert.Single(requests);
        Assert.All(requests, uri =>
        {
            Assert.DoesNotContain("ingest.example", uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".ts", uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task GetChannels_NeverRequestsIngestOrMediaUrls()
    {
        var playlistUrl = "https://cdn.example/list.m3u8";
        var requests = new List<Uri>();
        var tuner = new TunerHostInfo
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = "m3u",
            Url = playlistUrl
        };
        var host = CreateHost(tuner, requests, """
            #EXTM3U
            #EXTINF:-1,Stream
            http://nl01.example/live/1.ts
            """);

        await host.GetChannels(tuner, false, CancellationToken.None);

        Assert.Equal(playlistUrl, Assert.Single(requests).AbsoluteUri);
    }

    [Fact]
    public async Task GetChannels_ConditionalGetNotModified_KeepsSnapshot()
    {
        var playlistUrl = "https://cdn.example/playlist.m3u";
        var requests = new List<HttpRequestMessage>();
        var tuner = new TunerHostInfo
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = "m3u",
            Url = playlistUrl
        };

        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((request, _) =>
            {
                requests.Add(request);
                if (request.Headers.IfNoneMatch.Count > 0)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified)
                    {
                        Content = new StringContent(string.Empty)
                    });
                }

                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        #EXTM3U
                        #EXTINF:-1,One
                        http://ingest.example/one.ts
                        """)
                };
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag1\"");
                return Task.FromResult(response);
            });

        var host = CreateHost(tuner, handler);
        var first = await host.GetChannels(tuner, false, CancellationToken.None);
        var snapshot = host.PeekListingSnapshot(tuner.Id);
        Assert.NotNull(snapshot);
        snapshot!.FetchedUtc = DateTime.UtcNow.AddHours(-2);

        var second = await host.GetChannels(tuner, false, CancellationToken.None);

        Assert.Equal("One", Assert.Single(first).Name);
        Assert.Equal("One", Assert.Single(second).Name);
        Assert.Equal(2, requests.Count);
        Assert.NotEmpty(requests[1].Headers.IfNoneMatch);
    }

    private M3UTunerHost CreateHost(TunerHostInfo tuner, List<Uri> requestUris, string body)
    {
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((request, _) =>
            {
                requestUris.Add(request.RequestUri!);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body)
                });
            });

        return CreateHost(tuner, handler);
    }

    private M3UTunerHost CreateHost(TunerHostInfo tuner, Mock<HttpMessageHandler> handler)
    {
        var paths = new Mock<IServerApplicationPaths>();
        paths.Setup(p => p.CachePath).Returns(_cachePath);

        var config = new Mock<IServerConfigurationManager>();
        config.Setup(c => c.ApplicationPaths).Returns(paths.Object);
        config.Setup(c => c.GetConfiguration("livetv")).Returns(new LiveTvOptions { TunerHosts = [tuner] });

        var http = new Mock<IHttpClientFactory>();
        http.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler.Object));

        var mediaSourceManager = new Mock<IMediaSourceManager>();
        mediaSourceManager.Setup(m => m.GetPathProtocol(It.IsAny<string>())).Returns(MediaBrowser.Model.MediaInfo.MediaProtocol.Http);

        var host = new M3UTunerHost(
            config.Object,
            mediaSourceManager.Object,
            NullLogger<M3UTunerHost>.Instance,
            Mock.Of<IFileSystem>(),
            http.Object,
            Mock.Of<IServerApplicationHost>(),
            Mock.Of<INetworkManager>(),
            Mock.Of<IStreamHelper>());
        host.EnableBackgroundListingRefresh = false;
        return host;
    }
}
