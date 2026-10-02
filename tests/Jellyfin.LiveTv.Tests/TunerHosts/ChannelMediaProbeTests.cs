using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.LiveTv.Health;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Moq;
using Xunit;

namespace Jellyfin.LiveTv.Tests.TunerHosts;

public class ChannelMediaProbeTests
{
    public static bool HasFfmpeg => File.Exists(Environment.GetEnvironmentVariable("JELLYFIN_TEST_FFMPEG"));

    [Theory]
    [InlineData("Error opening input: Connection timed out", "ProviderNetwork", true)]
    [InlineData("Connection refused", "ProviderNetwork", true)]
    [InlineData("Network is unreachable", "ProviderNetwork", true)]
    [InlineData("No route to host", "ProviderNetwork", true)]
    [InlineData("Failed to resolve hostname", "ProviderNetwork", true)]
    [InlineData("Temporary failure in name resolution", "ProviderNetwork", true)]
    [InlineData("Server returned 403 Forbidden", "ProviderAuthentication", true)]
    [InlineData("HTTP error 429 Too Many Requests", "ProviderBusy", true)]
    [InlineData("Invalid data found when processing input", "InvalidMedia", false)]
    public void DecoderErrorsDistinguishProviderProblemsFromInvalidMedia(string output, string reason, bool providerWide)
    {
        var failure = ChannelMediaProbe.FailureFromOutput(output, false);
        Assert.Equal(reason, failure.Reason);
        Assert.Equal(providerWide, failure.ProviderWide);
    }

    [Fact(Skip = "Set JELLYFIN_TEST_FFMPEG to run the real decoder integration test.", SkipUnless = nameof(HasFfmpeg))]
    public async Task DecoderChecksMediaRatherThanHttpSuccess()
    {
        var video = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Test Data", "health-video.mp4"), TestContext.Current.CancellationToken);
        await using var server = new Server(video);
        var probe = CreateProbe();
        var valid = await probe.ProbeAsync(Source(server.Url + "video"), TestContext.Current.CancellationToken);
        Assert.True(valid.Success);
        var html = await probe.ProbeAsync(Source(server.Url + "html"), TestContext.Current.CancellationToken);
        Assert.False(html.Success);
        var denied = await probe.ProbeAsync(Source(server.Url + "denied"), TestContext.Current.CancellationToken);
        Assert.False(denied.Success);
        Assert.Equal("ProviderAuthentication", denied.Failure?.Reason);
        Assert.True(denied.Failure?.ProviderWide);

        using var refused = new TcpListener(IPAddress.Loopback, 0);
        refused.Start();
        var endpoint = (IPEndPoint)refused.LocalEndpoint;
        refused.Stop();
        var unavailable = await probe.ProbeAsync(Source("http://" + endpoint + "/"), TestContext.Current.CancellationToken);
        Assert.False(unavailable.Success);
        Assert.Equal("ProviderNetwork", unavailable.Failure?.Reason);
        Assert.True(unavailable.Failure?.ProviderWide);
    }

    [Fact(Skip = "Set JELLYFIN_TEST_FFMPEG to run the real decoder integration test.", SkipUnless = nameof(HasFfmpeg))]
    public async Task ViewerPreemptsRealDecoderBeforeAcquiringProviderReservation()
    {
        await using var server = new Server([]);
        var coordinator = new ChannelProbeCoordinator();
        var reservation = coordinator.TryAcquireProbe()!;
        async Task RunProbe()
        {
            using (reservation)
            {
                await CreateProbe().ProbeAsync(Source(server.Url + "slow"), reservation.Cancellation.Token);
            }
        }

        var running = RunProbe();
        await server.Connected.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        using var foreground = await coordinator.AcquirePlayback(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(running.IsCompleted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Null(coordinator.TryAcquireProbe());
    }

    private static ChannelMediaProbe CreateProbe()
    {
        var encoder = new Mock<IMediaEncoder>();
        encoder.SetupGet(e => e.EncoderPath).Returns(Environment.GetEnvironmentVariable("JELLYFIN_TEST_FFMPEG")!);
        return new ChannelMediaProbe(encoder.Object);
    }

    private static MediaSourceInfo Source(string url) => new() { Path = url, Protocol = MediaProtocol.Http };

    private sealed class Server : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        internal Server(byte[] video)
        {
            using var port = new TcpListener(IPAddress.Loopback, 0);
            port.Start();
            var number = ((IPEndPoint)port.LocalEndpoint).Port;
            port.Stop();
            Url = "http://127.0.0.1:" + number + "/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _loop = Run(video);
        }

        internal string Url { get; }

        internal TaskCompletionSource Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try
            {
                await _loop;
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or ObjectDisposedException)
            {
            }

            _listener.Close();
            _stop.Dispose();
        }

        private async Task Run(byte[] video)
        {
            while (!_stop.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync();
                Connected.TrySetResult();
                using var response = context.Response;
                var route = context.Request.Url!.AbsolutePath;
                if (route == "/slow")
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, _stop.Token);
                }

                response.StatusCode = route == "/denied" ? 403 : 200;
                var body = route == "/video" ? video : "<html>Provider unavailable</html>"u8.ToArray();
                response.ContentType = route == "/video" ? "video/mp4" : "text/html";
                response.ContentLength64 = body.Length;
                await response.OutputStream.WriteAsync(body, _stop.Token);
            }
        }
    }
}
