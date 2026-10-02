using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.LiveTv.Health;
using Jellyfin.LiveTv.IO;
using Jellyfin.LiveTv.TunerHosts;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.LiveTv.Tests.TunerHosts;

public class SharedHttpStreamRecoveryTests
{
    [Fact]
    public async Task MissingPrimaryUsesConfiguredAlternateWithoutChangingOtherChannels()
    {
        using var fixture = new Fixture(HttpStatusCode.NotFound);
        await fixture.Stream.Open(TestContext.Current.CancellationToken);
        using var reader = fixture.Stream.GetStream();
        await WaitUntil(() => fixture.Health.GetHealth("channel").Status == "Healthy");
        Assert.Equal(["primary.example", "primary.example", "secondary.example"], fixture.Handler.Hosts);
        Assert.Equal("http://primary.example", fixture.Tuner.ActiveUrl);
        Assert.True(fixture.Health.GetHealth("channel").BytesReceived > 0);
        Assert.Equal(1, fixture.Handler.MaximumActiveConnections);
        await reader.DisposeAsync();
        await fixture.Stream.Close();
        Assert.Equal(0, fixture.Handler.ActiveConnections);
        Assert.Equal("Healthy", fixture.Health.GetHealth("channel").Status);
    }

    [Fact]
    public async Task AuthenticationFailureStopsImmediatelyAndDoesNotMarkChannelDead()
    {
        using var fixture = new Fixture(HttpStatusCode.Forbidden);
        await fixture.Stream.Open(TestContext.Current.CancellationToken);
        using var reader = fixture.Stream.GetStream();
        await WaitUntil(() => !fixture.Stream.EnableStreamSharing);
        Assert.Single(fixture.Handler.Hosts);
        Assert.Equal("Unknown", fixture.Health.GetHealth("channel").Status);
        Assert.Equal("ProviderAuthentication", fixture.Health.GetHealth("channel").Reason);
        Assert.False(fixture.Health.CanProbe("tuner"));
        await reader.DisposeAsync();
        await fixture.Stream.Close();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory();
        private readonly HttpClient _client;

        internal Fixture(HttpStatusCode primaryStatus)
        {
            Handler = new ProviderHandler(primaryStatus);
            _client = new HttpClient(Handler);
            var http = new Mock<IHttpClientFactory>();
            http.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(_client);
            var configuration = new Mock<IConfigurationManager>();
            configuration.Setup(c => c.GetConfiguration("encoding")).Returns(new EncodingOptions { TranscodingTempPath = _directory.FullName });
            configuration.SetupGet(c => c.CommonApplicationPaths).Returns(Mock.Of<IApplicationPaths>());
            var host = new Mock<IServerApplicationHost>();
            host.Setup(h => h.GetApiUrlForLocalAccess(null, true)).Returns("http://127.0.0.1:8096");
            Tuner = new TunerHostInfo { Id = "tuner", Url = "http://listing.example/list.m3u", ActiveUrl = "http://primary.example", AlternateUrls = ["http://primary.example", "http://secondary.example"] };
            Health = new ChannelHealthStore(null, TimeProvider.System, NullLogger<ChannelHealthStore>.Instance);
            Stream = new SharedHttpStream(new MediaSourceInfo { Path = "http://primary.example/live.ts", Protocol = MediaProtocol.Http }, Tuner, "stream", Mock.Of<IFileSystem>(), http.Object, NullLogger.Instance, configuration.Object, host.Object, new StreamHelper(), Health, "channel");
        }

        internal ProviderHandler Handler { get; }

        internal ChannelHealthStore Health { get; }

        internal TunerHostInfo Tuner { get; }

        internal SharedHttpStream Stream { get; }

        public void Dispose()
        {
            Stream.Dispose();
            _client.Dispose();
            _directory.Delete(true);
        }
    }

    private sealed class ProviderHandler(HttpStatusCode primaryStatus) : HttpMessageHandler
    {
        internal List<string> Hosts { get; } = [];

        internal int ActiveConnections { get; private set; }

        internal int MaximumActiveConnections { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Hosts.Add(request.RequestUri!.Host);
            if (request.RequestUri.Host == "primary.example")
            {
                return Task.FromResult(new HttpResponseMessage(primaryStatus));
            }

            ActiveConnections++;
            MaximumActiveConnections = Math.Max(MaximumActiveConnections, ActiveConnections);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MediaStream(() => ActiveConnections--)) });
        }
    }

    private sealed class MediaStream(Action closed) : Stream
    {
        private int _reads;
        private int _disposed;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_reads++ != 0)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            var data = new byte[188 * 5];
            for (var i = 0; i < 5; i++)
            {
                data[i * 188] = 0x47;
                data[(i * 188) + 1] = 0x01;
                data[(i * 188) + 3] = 0x10;
            }

            data.CopyTo(buffer);
            return data.Length;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                closed();
            }

            base.Dispose(disposing);
        }
    }
}
