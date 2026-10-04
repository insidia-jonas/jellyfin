using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.Health;

/// <summary>Bounded, persisted observations. Provider-wide failures never condemn a channel.</summary>
public sealed class ChannelHealthStore : ILiveTvChannelHealth
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Observation> _channels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Reason, DateTime Until)> _providerProblems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sourceScopes = new(StringComparer.Ordinal);
    private readonly string? _path;
    private readonly TimeProvider _clock;
    private readonly ILogger<ChannelHealthStore> _logger;

    /// <summary>Initializes a new instance of the <see cref="ChannelHealthStore"/> class.</summary>
    /// <param name="paths">Application paths.</param>
    /// <param name="logger">Logger.</param>
    public ChannelHealthStore(IApplicationPaths paths, ILogger<ChannelHealthStore> logger)
        : this(Path.Combine(paths.DataPath, "livetv", "channel-health.json"), TimeProvider.System, logger)
    {
    }

    internal ChannelHealthStore(string? path, TimeProvider clock, ILogger<ChannelHealthStore> logger)
    {
        _path = path;
        _clock = clock;
        _logger = logger;
        try
        {
            if (path is not null && File.Exists(path))
            {
                foreach (var pair in JsonSerializer.Deserialize<Dictionary<string, Observation>>(File.ReadAllText(path)) ?? [])
                {
                    if (_channels.Count < 5000 && pair.Value is not null)
                    {
                        _channels[pair.Key] = pair.Value;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not read channel health history ({ErrorType})", ex.GetType().Name);
        }
    }

    /// <inheritdoc />
    public ChannelHealth GetHealth(string channelId)
    {
        lock (_sync)
        {
            if (!_channels.TryGetValue(channelId, out var item))
            {
                return new ChannelHealth();
            }

            var now = _clock.GetUtcNow().UtcDateTime;
            var stale = !item.Health.LastCheckedUtc.HasValue || now - item.Health.LastCheckedUtc.Value > TimeSpan.FromMinutes(30);
            var provider = _providerProblems.GetValueOrDefault(item.TunerId);
            var blocked = provider.Until > now;
            return new ChannelHealth
            {
                Status = blocked || stale ? "Unknown" : item.Health.Status,
                Reason = blocked ? provider.Reason : stale ? "Stale" : item.Health.Reason,
                LastCheckedUtc = item.Health.LastCheckedUtc,
                LastSuccessUtc = item.Health.LastSuccessUtc,
                LastPlayedUtc = item.Health.LastPlayedUtc,
                StartMilliseconds = item.Health.StartMilliseconds,
                StartupMeasurement = item.Health.StartupMeasurement,
                BytesReceived = item.Health.BytesReceived,
                Interruptions = item.Health.Interruptions
            };
        }
    }

    internal void BeginPlayback(string channelId, string tunerId)
    {
        lock (_sync)
        {
            var item = GetOrAdd(channelId, tunerId);
            item.Health.LastPlayedUtc = _clock.GetUtcNow().UtcDateTime;
            item.Health.BytesReceived = 0;
            item.Health.StartMilliseconds = null;
            item.Health.StartupMeasurement = null;
            item.Health.Interruptions = 0;
        }
    }

    internal void Success(string channelId, string tunerId, long? startMilliseconds = null, long? bytes = null, bool decoded = false)
    {
        lock (_sync)
        {
            var item = GetOrAdd(channelId, tunerId);
            var now = _clock.GetUtcNow().UtcDateTime;
            var unstable = item.LastInterruptionUtc.HasValue && now - item.LastInterruptionUtc.Value < TimeSpan.FromMinutes(5);
            item.Health.Status = unstable ? "Unstable" : "Healthy";
            item.Health.Reason = unstable ? item.Health.Reason == "ClientPlaybackFailed" ? "ClientPlaybackFailed" : "Interrupted" : null;
            item.Health.LastCheckedUtc = now;
            item.Health.LastSuccessUtc = now;
            if (decoded)
            {
                item.Health.StartMilliseconds = startMilliseconds;
            }
            else
            {
                item.Health.StartMilliseconds ??= startMilliseconds;
            }
            if (startMilliseconds.HasValue)
            {
                item.Health.StartupMeasurement = decoded ? "DecodedMedia" : "MediaData";
            }

            if (!unstable && item.Health.StartMilliseconds >= 4000)
            {
                item.Health.Status = "Unstable";
                item.Health.Reason = "SlowStart";
            }
            if (bytes.HasValue)
            {
                item.Health.BytesReceived = bytes.Value;
            }

            item.Failures = 0;
            item.LastFailureUtc = null;
            _providerProblems.Remove(tunerId);
        }
    }

    internal void Failure(string channelId, string tunerId, string reason, bool providerWide, bool interrupted = false)
    {
        lock (_sync)
        {
            var item = GetOrAdd(channelId, tunerId);
            var now = _clock.GetUtcNow().UtcDateTime;
            if (providerWide)
            {
                _providerProblems[tunerId] = (reason, now.AddMinutes(10));
                return;
            }

            item.Health.LastCheckedUtc = now;

            // Retries of the same tune do not constitute independent evidence.
            if (!item.LastFailureUtc.HasValue || now - item.LastFailureUtc.Value >= TimeSpan.FromSeconds(30))
            {
                item.Failures = item.LastFailureUtc.HasValue && now - item.LastFailureUtc.Value < TimeSpan.FromMinutes(30) ? item.Failures + 1 : 1;
                item.LastFailureUtc = now;
            }

            if (interrupted)
            {
                item.Health.Interruptions++;
                item.LastInterruptionUtc = now;
            }

            item.Health.Status = item.Failures >= 2 ? "Unavailable" : "Unstable";
            item.Health.Reason = reason;
        }
    }

    internal bool CanProbe(string tunerId)
    {
        lock (_sync)
        {
            return _providerProblems.GetValueOrDefault(tunerId).Until <= _clock.GetUtcNow().UtcDateTime;
        }
    }

    internal void SynchronizeSource(string tunerId, string fingerprint)
    {
        lock (_sync)
        {
            if (_sourceScopes.GetValueOrDefault(tunerId) == fingerprint)
            {
                return;
            }

            _sourceScopes[tunerId] = fingerprint;
            foreach (var item in _channels.Values.Where(o => o.TunerId == tunerId && o.SourceFingerprint != fingerprint))
            {
                item.Health = new ChannelHealth { LastPlayedUtc = item.Health.LastPlayedUtc };
                item.Failures = 0;
                item.LastFailureUtc = null;
                item.LastInterruptionUtc = null;
                item.SourceFingerprint = fingerprint;
            }

            _providerProblems.Remove(tunerId);
        }
    }

    internal string[] RecentChannels()
    {
        lock (_sync)
        {
            var since = _clock.GetUtcNow().UtcDateTime.AddDays(-7);
            return _channels.Where(p => p.Value.Health.LastPlayedUtc >= since).Select(p => p.Key).ToArray();
        }
    }

    internal void ConfirmClientProgress(string channelId)
    {
        lock (_sync)
        {
            if (_channels.TryGetValue(channelId, out var item))
            {
                Success(channelId, item.TunerId);
            }
        }
    }

    internal void RecordClientFailure(string channelId)
    {
        lock (_sync)
        {
            if (_channels.TryGetValue(channelId, out var item))
            {
                // A decoder/client failure is not proof that the provider channel is dead.
                // Keep it visible even while valid TS packets continue reaching the server.
                var now = _clock.GetUtcNow().UtcDateTime;
                item.LastInterruptionUtc = now;
                item.Health.LastCheckedUtc = now;
                item.Health.Interruptions++;
                item.Health.Status = item.Failures >= 2 ? "Unavailable" : "Unstable";
                item.Health.Reason = "ClientPlaybackFailed";
            }
        }
    }

    internal void Save()
    {
        if (_path is null)
        {
            return;
        }

        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_channels));
                File.Move(_path + ".tmp", _path, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Could not persist channel health history ({ErrorType})", ex.GetType().Name);
            }
        }
    }

    private Observation GetOrAdd(string channelId, string tunerId)
    {
        if (!_channels.TryGetValue(channelId, out var item))
        {
            if (_channels.Count >= 5000)
            {
                _channels.Remove(_channels.MinBy(p => p.Value.Health.LastPlayedUtc ?? p.Value.Health.LastCheckedUtc).Key);
            }

            item = new Observation();
            _channels[channelId] = item;
        }

        item.TunerId = tunerId;
        item.SourceFingerprint = _sourceScopes.GetValueOrDefault(tunerId);
        return item;
    }

    internal sealed class Observation
    {
        public string TunerId { get; set; } = string.Empty;

        public string? SourceFingerprint { get; set; }

        public ChannelHealth Health { get; set; } = new();

        public int Failures { get; set; }

        public DateTime? LastFailureUtc { get; set; }

        public DateTime? LastInterruptionUtc { get; set; }
    }
}
