using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    public static bool HasFfmpeg => ChannelMediaProbeTests.HasFfmpeg;

    [Fact]
    public async Task ManualChoiceNeverSilentlyFailsOverToAnotherHost()
    {
        using var fixture = new Fixture(HttpStatusCode.NotFound, selectedOrigin: "http://primary.example");
        await fixture.Stream.Open(TestContext.Current.CancellationToken);
        await using var reader = fixture.Stream.GetStream();
        await WaitUntil(() => fixture.Handler.Hosts.Count >= 2);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.All(fixture.Handler.Hosts, host => Assert.Equal("primary.example", host));
        Assert.Equal("http://primary.example", fixture.Tuner.ActiveUrl);
        await fixture.Stream.Close();
    }

    [Fact(Skip = "Set JELLYFIN_TEST_FFMPEG to verify decoded fallback media.", SkipUnless = nameof(HasFfmpeg))]
    public async Task ConfiguredAlternateSuppliesDecodableVideoAfterPrimaryFails()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var transport = Path.Combine(directory.FullName, "video.ts");
            await Ffmpeg(["-i", Path.Combine(AppContext.BaseDirectory, "Test Data", "health-video.mp4"), "-c", "copy", "-f", "mpegts", transport]);
            using var fixture = new Fixture(HttpStatusCode.ServiceUnavailable, await File.ReadAllBytesAsync(transport, token));
            await fixture.Stream.Open(token);
            await using var reader = fixture.Stream.GetStream();
            await WaitUntil(() => fixture.Health.GetHealth("channel").Status == "Healthy");
            // Closing flushes the last file buffer; the test file-system mock keeps
            // the captured output available to the existing reader for decoding.
            await fixture.Stream.Close();
            var capture = Path.Combine(directory.FullName, "received.ts");
            await using (var file = File.Create(capture))
            {
                await reader.CopyToAsync(file, token);
            }

            var decoded = await Ffmpeg(["-i", capture, "-t", "1", "-map", "0:v:0", "-progress", "pipe:1", "-f", "null", "-"]);
            Assert.True(ChannelMediaProbe.HasDecodedMedia(decoded, requireVideo: true));
            Assert.Equal(["primary.example", "primary.example", "secondary.example"], fixture.Handler.Hosts);
            Assert.Equal(1, fixture.Handler.MaximumActiveConnections);
            Assert.Equal(0, fixture.Handler.ActiveConnections);
            Assert.Equal("http://primary.example", fixture.Tuner.ActiveUrl);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private static async Task<string> Ffmpeg(string[] arguments)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("JELLYFIN_TEST_FFMPEG")!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var argument in new[] { "-nostdin", "-v", "error", "-threads", "1" })
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            Assert.True(process.ExitCode == 0, await error);
            return await output;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

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
    public async Task AllSourcesFailStopsAfterFourRequests()
    {
        using var fixture = new Fixture(HttpStatusCode.NotFound, alternateStatus: HttpStatusCode.ServiceUnavailable);
        await fixture.Stream.Open(TestContext.Current.CancellationToken);
        using var reader = fixture.Stream.GetStream();
        await WaitUntil(() => !fixture.Stream.EnableStreamSharing);
        Assert.Equal(["primary.example", "primary.example", "secondary.example", "secondary.example"], fixture.Handler.Hosts);
        Assert.Equal("http://primary.example", fixture.Tuner.ActiveUrl);
        await reader.DisposeAsync();
        await fixture.Stream.Close();
        Assert.Equal(0, fixture.Handler.ActiveConnections);
    }

    [Fact]
    public async Task UnverifiedAlternateIsNeverUsedForRecovery()
    {
        using var fixture = new Fixture(HttpStatusCode.NotFound, verifyBackup: false);
        await fixture.Stream.Open(TestContext.Current.CancellationToken);
        using var reader = fixture.Stream.GetStream();
        await WaitUntil(() => !fixture.Stream.EnableStreamSharing);
        Assert.Equal(["primary.example", "primary.example", "primary.example", "primary.example"], fixture.Handler.Hosts);
        await fixture.Stream.Close();
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "ProviderAuthentication")]
    [InlineData(HttpStatusCode.TooManyRequests, "ProviderBusy")]
    public async Task AccountFailureStopsImmediatelyAndDoesNotMarkChannelDead(HttpStatusCode status, string reason)
    {
        using var fixture = new Fixture(status);
        await fixture.Stream.Open(TestContext.Current.CancellationToken);
        using var reader = fixture.Stream.GetStream();
        await WaitUntil(() => !fixture.Stream.EnableStreamSharing);
        Assert.Single(fixture.Handler.Hosts);
        Assert.Equal("Unknown", fixture.Health.GetHealth("channel").Status);
        Assert.Equal(reason, fixture.Health.GetHealth("channel").Reason);
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

        internal Fixture(HttpStatusCode primaryStatus, byte[]? media = null, HttpStatusCode alternateStatus = HttpStatusCode.OK, bool verifyBackup = true, string? selectedOrigin = null)
        {
            Handler = new ProviderHandler(primaryStatus, media, alternateStatus);
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
            var watchdog = new IptvWatchdog(null, TimeProvider.System, () => new LiveTvOptions { TunerHosts = [Tuner] }, NullLogger<IptvWatchdog>.Instance);
            if (verifyBackup)
            {
                watchdog.Record(Tuner, "http://secondary.example", "channel", "probe", true, true, 100);
            }

            Stream = new SharedHttpStream(new MediaSourceInfo { Path = "http://primary.example/live.ts", Protocol = MediaProtocol.Http }, Tuner, "stream", Mock.Of<IFileSystem>(), http.Object, NullLogger.Instance, configuration.Object, host.Object, new StreamHelper(), Health, "channel", watchdog: watchdog, selectedOrigin: selectedOrigin);
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

    private sealed class ProviderHandler(HttpStatusCode primaryStatus, byte[]? media, HttpStatusCode alternateStatus) : HttpMessageHandler
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

            if (alternateStatus != HttpStatusCode.OK)
            {
                return Task.FromResult(new HttpResponseMessage(alternateStatus));
            }

            ActiveConnections++;
            MaximumActiveConnections = Math.Max(MaximumActiveConnections, ActiveConnections);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MediaStream(() => ActiveConnections--, media)) });
        }
    }

    private sealed class MediaStream(Action closed, byte[]? media) : Stream
    {
        private readonly byte[] _data = media ?? TransportPackets();
        private int _offset;
        private int _disposed;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset == _data.Length)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            var count = Math.Min(buffer.Length, _data.Length - _offset);
            _data.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        private static byte[] TransportPackets()
        {
            var data = new byte[188 * 5];
            for (var i = 0; i < 5; i++)
            {
                data[i * 188] = 0x47;
                data[(i * 188) + 1] = 0x01;
                data[(i * 188) + 3] = 0x10;
            }

            return data;
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
