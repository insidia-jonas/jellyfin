using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.LiveTv.Health;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.LiveTv.Tests.TunerHosts;

public class IptvWatchdogTests
{
    private const string Primary = "https://primary.example";
    private const string Backup = "https://backup.example";

    [Fact]
    public void TransportDataNeverVerifiesFallbackAndAccountErrorsStopAllSources()
    {
        var (store, tuner, clock) = Create();
        store.Record(tuner, Backup, "a", "play", true, false, 100, 1024);
        Assert.Null(store.GetVerifiedAlternate(tuner, "a", Primary));
        store.ObservePlayback(tuner, Backup, "a", "play");
        store.ObserveClient("a", false);
        Assert.Equal(Backup, store.GetVerifiedAlternate(tuner, "a", Primary));
        store.Record(tuner, Primary, "b", "denied", false, false, reason: "ProviderBusy");
        Assert.False(store.CanProbe(tuner, Backup));
        Assert.Null(store.GetVerifiedAlternate(tuner, "a", Primary));
        var status = Assert.Single(store.GetStatus().Tuners);
        Assert.Equal("ProviderBusy", status.AccountReason);
        Assert.Null(status.RecommendedSourceId);
        Assert.Null(status.Sources.Single(s => s.Active).SuccessRate);
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(Backup, store.GetVerifiedAlternate(tuner, "a", Primary));
    }

    [Fact]
    public void LateClientProgressCannotReviveFailedSourceButFinalFailureStillCounts()
    {
        var (store, tuner, _) = Create();
        store.Record(tuner, Backup, "a", "play", true, false, bytes: 1234);
        store.ObservePlayback(tuner, Backup, "a", "play");
        store.Record(tuner, Backup, "a", "play", false, false, reason: "Interrupted");
        store.EndPlayback("a", "play");
        store.ObserveClient("a", false);
        Assert.Null(store.GetVerifiedAlternate(tuner, "a", Primary));
        store.ObserveClient("a", true);
        var source = Assert.Single(store.GetStatus().Tuners).Sources.Single(s => !s.Active);
        Assert.Equal("ClientPlaybackFailed", source.Reason);
        Assert.Equal(1, source.Interruptions);
        Assert.Equal(1234, source.BytesReceived);
    }

    [Fact]
    public void SourceFailureDoesNotBlockOtherOriginsAndFailureRevokesVerification()
    {
        var (store, tuner, clock) = Create();
        store.Record(tuner, Backup, "a", "probe", true, true, 700);
        store.Record(tuner, Primary, "a", "network", false, false, reason: "ProviderNetwork");
        Assert.False(store.CanProbe(tuner, Primary));
        Assert.True(store.CanProbe(tuner, Backup));
        Assert.Equal(Backup, store.GetVerifiedAlternate(tuner, "a", Primary));
        store.Record(tuner, Backup, "a", "broken", false, false, reason: "NoMediaData");
        Assert.Null(store.GetVerifiedAlternate(tuner, "a", Primary));
        clock.Advance(TimeSpan.FromMinutes(1));
        store.Record(tuner, Backup, "a", "probe2", true, true, 700);
        Assert.Equal(Backup, store.GetVerifiedAlternate(tuner, "a", Primary));
        clock.Advance(TimeSpan.FromHours(7));
        Assert.Null(store.GetVerifiedAlternate(tuner, "a", Primary));
        Assert.All(Assert.Single(store.GetStatus().Tuners).Sources, s => Assert.Equal(0, s.Samples));
    }

    [Fact]
    public void RepeatedProgressAndImmediateRetriesAreNotIndependentEvidence()
    {
        var (store, tuner, clock) = Create();
        for (var i = 0; i < 30; i++)
        {
            store.Record(tuner, Backup, "a", "play", true, true, 1200, i * 1024);
            clock.Advance(TimeSpan.FromSeconds(5));
        }

        store.Record(tuner, Backup, "a", "retry", false, false, reason: "Interrupted", interrupted: true);
        var source = Assert.Single(store.GetStatus().Tuners).Sources.Single(s => !s.Active);
        Assert.Equal(1, source.Samples);
        Assert.Equal(1, source.Interruptions);
        Assert.Equal(29 * 1024, source.BytesReceived);
        Assert.Null(store.GetVerifiedAlternate(tuner, "a", Primary));
    }

    [Fact]
    public void RecommendationRequiresRepeatedEvidenceAcrossTheSameThreeChannels()
    {
        var (store, tuner, clock) = Create();
        foreach (var channel in new[] { "a", "b", "c" })
        {
            store.Record(tuner, Primary, channel, "bad1", false, false, reason: "NoMediaData");
            store.Record(tuner, Backup, channel, "probe", true, true, 1000);
        }

        Assert.Null(Assert.Single(store.GetStatus().Tuners).RecommendedSourceId);
        clock.Advance(TimeSpan.FromMinutes(16));
        foreach (var channel in new[] { "a", "b", "c" })
        {
            store.Record(tuner, Primary, channel, "bad2", false, false, reason: "NoMediaData");
        }

        var result = Assert.Single(store.GetStatus().Tuners);
        Assert.Equal(result.Sources.Single(s => !s.Active).Id, result.RecommendedSourceId);
        Assert.Equal("RepeatedFailures", result.RecommendationReason);
        Assert.Equal(Primary, tuner.ActiveUrl);
        tuner.Url += "&new-token=changed";
        result = Assert.Single(store.GetStatus().Tuners);
        Assert.Null(result.RecommendedSourceId);
        Assert.All(result.Sources, s => Assert.Equal(0, s.Samples));
        Assert.Null(store.GetVerifiedAlternate(tuner, "a", Primary));
    }

    [Fact]
    public void DifferentChannelSetsAndTransportLatencyDoNotProduceSpeedRecommendations()
    {
        var (store, tuner, clock) = Create();
        for (var round = 0; round < 2; round++)
        {
            foreach (var channel in new[] { "a", "b", "c" })
            {
                store.Record(tuner, Primary, channel, round.ToString(CultureInfo.InvariantCulture), true, false, 8000);
                store.Record(tuner, Backup, channel + "other", round.ToString(CultureInfo.InvariantCulture), true, true, 700);
            }

            clock.Advance(TimeSpan.FromMinutes(16));
        }

        var result = Assert.Single(store.GetStatus().Tuners);
        Assert.Null(result.RecommendedSourceId);
        Assert.Null(result.Sources.Single(s => s.Active).MedianStartMilliseconds);
    }

    [Fact]
    public void DecoderLatencyCanRecommendFasterSourceAfterRepeatedComparableSamples()
    {
        var (store, tuner, clock) = Create();
        for (var round = 0; round < 2; round++)
        {
            foreach (var channel in new[] { "a", "b", "c" })
            {
                store.Record(tuner, Primary, channel, round.ToString(CultureInfo.InvariantCulture), true, true, 6000);
                store.Record(tuner, Backup, channel, round.ToString(CultureInfo.InvariantCulture), true, true, 1000);
            }

            clock.Advance(TimeSpan.FromMinutes(16));
        }

        Assert.Equal("SlowStart", Assert.Single(store.GetStatus().Tuners).RecommendationReason);
    }

    [Fact]
    public void EvidenceSurvivesRestartWithoutSavingProviderUrlsOrAccessTokens()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "history.json");
            var (_, tuner, clock) = Create();
            var options = new LiveTvOptions { TunerHosts = [tuner] };
            var store = new IptvWatchdog(path, clock, () => options, NullLogger<IptvWatchdog>.Instance);
            store.Record(tuner, Backup, "a", "probe", true, true, 800);
            store.Save();
            var json = File.ReadAllText(path);
            Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("https://", json, StringComparison.OrdinalIgnoreCase);
            var restored = new IptvWatchdog(path, clock, () => options, NullLogger<IptvWatchdog>.Instance);
            Assert.Equal(Backup, restored.GetVerifiedAlternate(tuner, "a", Primary));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void SharedDeliveryPoolSuppressesIndependentServerRecommendation()
    {
        var (store, tuner, clock) = Create();
        for (var round = 0; round < 2; round++)
        {
            foreach (var channel in new[] { "a", "b", "c" })
            {
                store.Record(tuner, Primary, channel, round.ToString(CultureInfo.InvariantCulture), true, true, 6000, destinationHost: "same-cdn.example");
                store.Record(tuner, Backup, channel, round.ToString(CultureInfo.InvariantCulture), true, true, 1000, destinationHost: "same-cdn.example");
            }

            clock.Advance(TimeSpan.FromMinutes(16));
        }

        var status = Assert.Single(store.GetStatus().Tuners);
        Assert.Null(status.RecommendedSourceId);
        Assert.Equal(["same-cdn.example"], status.SharedDestinations);
        // A verified entry can still help an entry-server failure; it is not
        // presented as an independent way around a shared CDN outage.
        Assert.Equal(Backup, store.GetVerifiedAlternate(tuner, "a", Primary));
    }

    private static (IptvWatchdog Store, TunerHostInfo Tuner, Clock Clock) Create()
    {
        var clock = new Clock();
        var tuner = new TunerHostInfo { Id = "tuner", Type = "m3u", Url = "https://listing.example/secret.m3u?token=secret", ActiveUrl = Primary, AlternateUrls = [Primary, Backup] };
        var options = new LiveTvOptions { TunerHosts = [tuner] };
        return (new IptvWatchdog(null, clock, () => options, NullLogger<IptvWatchdog>.Instance), tuner, clock);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan amount) => _now += amount;
    }
}
