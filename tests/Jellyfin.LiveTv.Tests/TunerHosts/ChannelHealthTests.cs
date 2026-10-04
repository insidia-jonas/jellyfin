using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.LiveTv.Health;
using Jellyfin.LiveTv.TunerHosts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.LiveTv.Tests.TunerHosts;

public class ChannelHealthTests
{
    [Fact]
    public void ChangingAccessExpiresHealthButKeepsRecentChannelPriorityAndAccountCooldown()
    {
        var store = Store(new Clock());
        store.SynchronizeSource("tuner", "old");
        store.BeginPlayback("a", "tuner");
        store.Success("a", "tuner", 6000, decoded: true);
        Assert.Equal("SlowStart", store.GetHealth("a").Reason);
        Assert.Equal("DecodedMedia", store.GetHealth("a").StartupMeasurement);
        store.Failure("a", "tuner", "ProviderBusy", true);
        store.SynchronizeSource("tuner", "old");
        Assert.False(store.CanProbe("tuner"));
        store.SynchronizeSource("tuner", "new");
        Assert.Equal("Unknown", store.GetHealth("a").Status);
        Assert.Null(store.GetHealth("a").LastCheckedUtc);
        Assert.Contains("a", store.RecentChannels());
        Assert.True(store.CanProbe("tuner"));
    }

    [Fact]
    public void FailedClientPlaybackStaysVisibleDespiteTransportDataWithoutCondemningChannel()
    {
        var clock = new Clock();
        var store = Store(clock);
        store.BeginPlayback("channel", "tuner");
        store.Success("channel", "tuner", 100, 100000);
        store.RecordClientFailure("channel");
        store.Success("channel", "tuner", 100, 200000);
        Assert.Equal("Unstable", store.GetHealth("channel").Status);
        Assert.Equal("ClientPlaybackFailed", store.GetHealth("channel").Reason);
        Assert.Equal(1, store.GetHealth("channel").Interruptions);
        clock.Advance(TimeSpan.FromMinutes(1));
        store.RecordClientFailure("channel");
        Assert.Equal("Unstable", store.GetHealth("channel").Status);
        store.Failure("channel", "tuner", "ProviderBusy", true);
        Assert.Equal("Unknown", store.GetHealth("channel").Status);
        Assert.Equal("ProviderBusy", store.GetHealth("channel").Reason);
    }

    [Fact]
    public void RetriesAreNotIndependentFailuresAndHistoryExpires()
    {
        var clock = new Clock();
        var store = Store(clock);
        store.Failure("a", "tuner", "NoMediaData", false);
        store.Failure("a", "tuner", "NoMediaData", false);
        Assert.Equal("Unstable", store.GetHealth("a").Status);
        clock.Advance(TimeSpan.FromMinutes(1));
        store.Failure("a", "tuner", "NoMediaData", false);
        Assert.Equal("Unavailable", store.GetHealth("a").Status);
        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal("Unknown", store.GetHealth("a").Status);
        store.Success("a", "tuner", 123, 4096);
        Assert.Equal("Healthy", store.GetHealth("a").Status);
        Assert.Equal(123, store.GetHealth("a").StartMilliseconds);
        Assert.Equal(4096, store.GetHealth("a").BytesReceived);
    }

    [Theory]
    [InlineData(401, "ProviderAuthentication")]
    [InlineData(403, "ProviderAuthentication")]
    [InlineData(429, "ProviderBusy")]
    [InlineData(453, "ProviderBusy")]
    [InlineData(509, "ProviderBusy")]
    [InlineData(503, "ProviderUnavailable")]
    public void ProviderProblemsDoNotCondemnChannels(int status, string reason)
    {
        var clock = new Clock();
        var store = Store(clock);
        store.Success("a", "tuner");
        store.Success("b", "tuner");
        var failure = ChannelFailure.FromException(new HttpRequestException("secret URL", null, (HttpStatusCode)status));
        Assert.Equal(reason, failure.Reason);
        store.Failure("a", "tuner", failure.Reason, failure.ProviderWide);
        clock.Advance(TimeSpan.FromMinutes(1));
        store.Failure("b", "tuner", failure.Reason, failure.ProviderWide);
        Assert.Equal("Unknown", store.GetHealth("a").Status);
        Assert.Equal("Unknown", store.GetHealth("b").Status);
        Assert.Equal(reason, store.GetHealth("b").Reason);
        Assert.False(store.CanProbe("tuner"));
        Assert.True(store.CanProbe("other"));
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.True(store.CanProbe("tuner"));
        Assert.Equal("Healthy", store.GetHealth("a").Status);
    }

    [Fact]
    public void HistorySurvivesRestartAndKeepsRecentChannels()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "history.json");
            var clock = new Clock();
            var store = new ChannelHealthStore(path, clock, NullLogger<ChannelHealthStore>.Instance);
            store.BeginPlayback("channel", "tuner");
            store.Success("channel", "tuner", 70, 120000);
            store.Save();
            var restored = new ChannelHealthStore(path, clock, NullLogger<ChannelHealthStore>.Instance);
            Assert.Equal("Healthy", restored.GetHealth("channel").Status);
            Assert.Equal(120000, restored.GetHealth("channel").BytesReceived);
            Assert.Contains("channel", restored.RecentChannels());
            clock.Advance(TimeSpan.FromDays(8));
            Assert.Empty(restored.RecentChannels());
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task ForegroundWaitsForProbeExitAndBlocksNewProbes()
    {
        var clock = new Clock();
        var coordinator = new ChannelProbeCoordinator(clock);
        var probe = coordinator.TryAcquireProbe();
        Assert.NotNull(probe);
        Assert.Null(coordinator.TryAcquireProbe());
        var playbackTask = coordinator.AcquirePlayback(TestContext.Current.CancellationToken);
        Assert.True(probe.Cancellation.IsCancellationRequested);
        Assert.False(playbackTask.IsCompleted);
        Assert.Null(coordinator.TryAcquireProbe());
        probe.Dispose(); // Signals real provider teardown.
        using var playback = await playbackTask;
        Assert.Null(coordinator.TryAcquireProbe());
        playback.Dispose();
        Assert.Null(coordinator.TryAcquireProbe());
        clock.Advance(TimeSpan.FromMinutes(3));
        using var next = coordinator.TryAcquireProbe();
        Assert.NotNull(next);
    }

    [Fact]
    public void DisablingChecksCancelsButDoesNotReleaseTheRunningDecoder()
    {
        var coordinator = new ChannelProbeCoordinator(new Clock());
        var probe = coordinator.TryAcquireProbe();
        Assert.NotNull(probe);
        coordinator.SetProbesEnabled(false);
        Assert.True(probe.Cancellation.IsCancellationRequested);
        Assert.False(probe.Finished.Task.IsCompleted);
        Assert.Null(coordinator.TryAcquireProbe());
        probe.Dispose();
        Assert.Null(coordinator.TryAcquireProbe());
        coordinator.SetProbesEnabled(true);
        using var next = coordinator.TryAcquireProbe();
        Assert.NotNull(next);
    }

    [Fact]
    public async Task CancellingWaitingViewerDoesNotLeakReservationOrReleaseRunningProbe()
    {
        var clock = new Clock();
        var coordinator = new ChannelProbeCoordinator(clock);
        using var probe = coordinator.TryAcquireProbe();
        using var cancelled = new CancellationTokenSource();
        var playback = coordinator.AcquirePlayback(cancelled.Token);
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => playback);
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Null(coordinator.TryAcquireProbe());
        probe!.Dispose();
        using var next = coordinator.TryAcquireProbe();
        Assert.NotNull(next);
    }

    [Fact]
    public void RecoveryHasTimeAndAttemptLimitsAndRequiresStableMediaToReset()
    {
        var state = new LiveStreamRecovery();
        var now = DateTime.UtcNow;
        Assert.True(state.Failed(now, TimeSpan.Zero));
        Assert.False(state.ShouldSwitch);
        Assert.True(state.Failed(now.AddSeconds(10), TimeSpan.FromSeconds(1)));
        Assert.True(state.ShouldSwitch);
        Assert.True(state.Failed(now.AddSeconds(20), TimeSpan.FromSeconds(1)));
        Assert.False(state.Failed(now.AddSeconds(30), TimeSpan.FromSeconds(1)));
        Assert.True(state.Failed(now.AddMinutes(5), TimeSpan.FromSeconds(30)));
        Assert.Equal(1, state.Failures);
        Assert.False(state.Failed(now.AddMinutes(6), TimeSpan.Zero));
    }

    [Theory]
    [InlineData("progress=end\nframe=0\nout_time_us=0", false)]
    [InlineData("frame=1\nprogress=end", true)]
    [InlineData("out_time_us=1000000\nprogress=end", true)]
    [InlineData("HTTP 200 OK", false)]
    public void ProbeRequiresDecodedMedia(string output, bool expected)
        => Assert.Equal(expected, ChannelMediaProbe.HasDecodedMedia(output));

    [Fact]
    public void HttpSuccessPageAndNullTransportPacketsAreNotMediaFlow()
    {
        Assert.False(SharedHttpStream.HasTransportPackets(System.Text.Encoding.UTF8.GetBytes(new string('x', 4096))));
        var packets = new byte[188 * 5];
        for (var i = 0; i < 5; i++)
        {
            packets[i * 188] = 0x47;
            packets[(i * 188) + 1] = 0x1f;
            packets[(i * 188) + 2] = 0xff;
            packets[(i * 188) + 3] = 0x10;
        }

        Assert.False(SharedHttpStream.HasTransportPackets(packets));
        packets[1] = 0x01;
        Assert.True(SharedHttpStream.HasTransportPackets(packets));
    }

    private static ChannelHealthStore Store(Clock clock) => new(null, clock, NullLogger<ChannelHealthStore>.Instance);

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }
}
