using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.LiveTv.TunerHosts;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;

namespace Jellyfin.LiveTv.Tests.TunerHosts;

public class M3uParserConditionalGetTests
{
    private const string PlaylistUrl = "https://cdn.example/playlist.m3u";
    private const string IngestUrl = "http://nl01.example";
    private const string Body = """
        #EXTM3U
        #EXTINF:-1 tvg-id="cnn.us" group-title="News",CNN
        http://ingest.example/live/cnn.ts
        """;

    [Fact]
    public async Task FetchPlaylist_SendsConditionalHeaders_AndParsesBody()
    {
        HttpRequestMessage? seen = null;
        var parser = CreateParser(
            HttpStatusCode.OK,
            Body,
            request => seen = request,
            etag: "\"v1\"",
            lastModified: new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));

        var result = await parser.FetchPlaylist(
            Tuner(PlaylistUrl),
            "m3u_",
            "\"old\"",
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        Assert.False(result.NotModified);
        Assert.Equal("CNN", Assert.Single(result.Playlist!.Channels).Name);
        Assert.Equal(PlaylistUrl, seen!.RequestUri!.AbsoluteUri);
        Assert.Contains(seen.Headers.IfNoneMatch, tag => tag.Tag == "\"old\"" || tag.Tag == "old");
        Assert.NotNull(seen.Headers.IfModifiedSince);
        Assert.Equal("http://ingest.example/live/cnn.ts", result.Playlist.Channels[0].Path);
        Assert.Contains("v1", result.ETag, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchPlaylist_NotModified_DoesNotRequireBody()
    {
        var parser = CreateParser(HttpStatusCode.NotModified, body: string.Empty);

        var result = await parser.FetchPlaylist(
            Tuner(PlaylistUrl),
            "m3u_",
            "\"v1\"",
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.True(result.NotModified);
        Assert.Null(result.Playlist);
    }

    [Fact]
    public async Task FetchPlaylist_IngestHost_DoesNotSendHttp()
    {
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var parser = new M3uParser(NullLogger.Instance, factory.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            parser.FetchPlaylist(Tuner(IngestUrl), "m3u_", null, null, CancellationToken.None));

        factory.Verify(f => f.CreateClient(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task FetchPlaylist_OnlyContactsPlaylistUrl()
    {
        HttpRequestMessage? seen = null;
        var parser = CreateParser(HttpStatusCode.OK, Body, request => seen = request);

        await parser.FetchPlaylist(Tuner(PlaylistUrl), "m3u_", null, null, CancellationToken.None);

        Assert.Equal(PlaylistUrl, seen!.RequestUri!.AbsoluteUri);
        Assert.DoesNotContain("ingest.example", seen.RequestUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".ts", seen.RequestUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
    }

    private static TunerHostInfo Tuner(string url) => new()
    {
        Id = "tuner1",
        Type = "m3u",
        Url = url
    };

    private static M3uParser CreateParser(
        HttpStatusCode status,
        string body,
        Action<HttpRequestMessage>? onRequest = null,
        string? etag = null,
        DateTimeOffset? lastModified = null)
    {
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((request, _) =>
            {
                onRequest?.Invoke(request);
                var response = new HttpResponseMessage(status)
                {
                    Content = new StringContent(body)
                };
                if (!string.IsNullOrEmpty(etag))
                {
                    response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
                }

                if (lastModified.HasValue)
                {
                    response.Content.Headers.LastModified = lastModified;
                }

                return Task.FromResult(response);
            });

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler.Object));
        return new M3uParser(NullLogger.Instance, factory.Object);
    }
}
