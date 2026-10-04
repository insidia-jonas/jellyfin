using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.LiveTv.Configuration;
using Jellyfin.LiveTv.TunerHosts;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.Health;

/// <summary>Source-scoped evidence. Neither URL reachability nor transport packets prove a backup decodes.</summary>
public sealed class IptvWatchdog : IIptvWatchdog
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Observation> _history = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (TunerHostInfo Tuner, string Source, string Attempt, DateTime Until, bool Ended)> _playbacks = new(StringComparer.Ordinal);
    private readonly string? _path;
    private readonly TimeProvider _clock;
    private readonly Func<LiveTvOptions> _options;
    private readonly ILogger<IptvWatchdog> _logger;
    private string _state = "Waiting";
    private DateTime? _lastCheck;

    /// <summary>Initializes a new instance of the <see cref="IptvWatchdog"/> class.</summary>
    /// <param name="paths">Application paths.</param>
    /// <param name="configuration">Tuner configuration.</param>
    /// <param name="logger">Logger.</param>
    public IptvWatchdog(IApplicationPaths paths, IConfigurationManager configuration, ILogger<IptvWatchdog> logger)
        : this(Path.Combine(paths.DataPath, "livetv", "iptv-watchdog.json"), TimeProvider.System, configuration.GetLiveTvConfiguration, logger)
    {
    }

    internal IptvWatchdog(string? path, TimeProvider clock, Func<LiveTvOptions> options, ILogger<IptvWatchdog> logger)
    {
        _path = path;
        _clock = clock;
        _options = options;
        _logger = logger;
        try
        {
            if (path is not null && File.Exists(path))
            {
                foreach (var pair in JsonSerializer.Deserialize<Dictionary<string, Observation>>(File.ReadAllText(path)) ?? [])
                {
                    if (_history.Count < 10000 && pair.Value?.Samples is { Count: > 0 })
                    {
                        pair.Value.Samples = pair.Value.Samples.OrderByDescending(s => s.Utc).Take(8).OrderBy(s => s.Utc).ToList();
                        _history[pair.Key] = pair.Value;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not read IPTV observations ({ErrorType})", ex.GetType().Name);
        }
    }

    internal static string AccessKey(TunerHostInfo tuner) => Hash(tuner.Id + "\n" + tuner.Url + "\n" + tuner.UserAgent + "\n" + tuner.Referrer);

    internal static string SourceId(TunerHostInfo tuner, string source) => Hash(AccessKey(tuner) + "\n" + Origin(source));

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Origin(string source) => Uri.TryCreate(source, UriKind.Absolute, out var uri)
        ? uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped) : string.Empty;

    internal static string[] Sources(TunerHostInfo tuner) => M3uUrlFailover.GetHealthCandidates(tuner)
        .Where(M3uUrlFailover.IsIngestEndpoint).DistinctBy(Origin).Take(16).ToArray();

    internal void SetState(string state, bool completed = false)
    {
        lock (_sync)
        {
            _state = state;
            if (completed)
            {
                _lastCheck = _clock.GetUtcNow().UtcDateTime;
            }
        }
    }

    internal void ObservePlayback(TunerHostInfo tuner, string source, string channel, string attempt)
    {
        lock (_sync)
        {
            foreach (var id in _playbacks.Where(p => p.Value.Until < _clock.GetUtcNow().UtcDateTime).Select(p => p.Key).ToArray())
            {
                _playbacks.Remove(id);
            }

            _playbacks[channel] = (tuner, source, attempt, _clock.GetUtcNow().UtcDateTime.AddMinutes(2), false);
        }
    }

    internal void EndPlayback(string channel, string attempt)
    {
        lock (_sync)
        {
            if (_playbacks.TryGetValue(channel, out var playback) && playback.Attempt == attempt)
            {
                // Late progress must not revive a failed source. Retain a short
                // window for the client's final failure report after stream close.
                _playbacks[channel] = playback with { Ended = true, Until = _clock.GetUtcNow().UtcDateTime.AddSeconds(30) };
            }
        }
    }

    internal void ObserveClient(string channel, bool failed)
    {
        lock (_sync)
        {
            if (_playbacks.TryGetValue(channel, out var playback) && playback.Until >= _clock.GetUtcNow().UtcDateTime && (failed || !playback.Ended))
            {
                Record(playback.Tuner, playback.Source, channel, playback.Attempt, !failed, !failed, reason: failed ? "ClientPlaybackFailed" : null, interrupted: failed);
                if (failed)
                {
                    _playbacks.Remove(channel);
                }
                else
                {
                    _playbacks[channel] = playback with { Until = _clock.GetUtcNow().UtcDateTime.AddMinutes(2) };
                }
            }
        }
    }

    internal DateTime LastChecked(TunerHostInfo tuner, string source, string channel)
    {
        lock (_sync)
        {
            return _history.GetValueOrDefault(SourceId(tuner, source) + "|" + channel)?.Samples.Max(s => s.Utc) ?? DateTime.MinValue;
        }
    }

    internal bool CanProbe(TunerHostInfo tuner, string source)
    {
        lock (_sync)
        {
            if (AccountReason(tuner) is not null)
            {
                return false;
            }

            var cutoff = _clock.GetUtcNow().UtcDateTime.AddMinutes(-10);
            return !Observations(tuner, source).Any(o => o.Samples.Any(s => s.Utc > cutoff && !s.Success && s.Reason?.StartsWith("Provider", StringComparison.Ordinal) == true));
        }
    }

    internal bool IsVerified(TunerHostInfo tuner, string source, string channel)
    {
        lock (_sync)
        {
            var item = _history.GetValueOrDefault(SourceId(tuner, source) + "|" + channel);
            return AccountReason(tuner) is null && item is not null && Verified(item);
        }
    }

    internal string? GetVerifiedAlternate(TunerHostInfo tuner, string channel, string current)
    {
        lock (_sync)
        {
            return Sources(tuner).Where(s => Origin(s) != Origin(current) && CanProbe(tuner, s) && IsVerified(tuner, s, channel))
                .OrderBy(s => Summarize(tuner, s).MedianStartMilliseconds ?? long.MaxValue).FirstOrDefault();
        }
    }

    internal void Record(TunerHostInfo tuner, string source, string channel, string attempt, bool success, bool decoded, long? startMilliseconds = null, long bytes = 0, string? reason = null, bool interrupted = false, string? destinationHost = null)
    {
        if (!Sources(tuner).Any(s => Origin(s) == Origin(source)))
        {
            return;
        }

        lock (_sync)
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            var key = SourceId(tuner, source) + "|" + channel;
            if (!_history.TryGetValue(key, out var item))
            {
                if (_history.Count >= 10000)
                {
                    _history.Remove(_history.MinBy(p => p.Value.Samples.Max(s => s.Utc)).Key);
                }

                item = new Observation { Access = AccessKey(tuner), Source = SourceId(tuner, source), Channel = channel };
                _history[key] = item;
            }

            var sample = item.Samples.LastOrDefault();
            // Repeated progress and rapid retries remain one observation.
            if (sample is null || (sample.Attempt != attempt && now - sample.Utc >= TimeSpan.FromSeconds(30)))
            {
                sample = new Sample { Attempt = attempt };
                item.Samples.Add(sample);
                if (item.Samples.Count > 8)
                {
                    item.Samples.RemoveAt(0);
                }
            }

            sample.Utc = now;
            sample.Success = success;
            sample.Decoded = success && (sample.Decoded || decoded);
            // Transport latency is not decoder startup latency. Client progress proves
            // playback, but its reporting interval is not a first-frame measurement.
            if (decoded)
            {
                sample.StartMilliseconds ??= startMilliseconds;
            }
            sample.Bytes = Math.Max(sample.Bytes, bytes);
            sample.Reason = reason;
            if (!string.IsNullOrEmpty(destinationHost) && Uri.CheckHostName(destinationHost) != UriHostNameType.Unknown)
            {
                sample.DestinationHost = destinationHost.ToLowerInvariant();
            }
            if (interrupted)
            {
                sample.Interruptions++;
            }
        }
    }

    /// <inheritdoc />
    public IptvWatchdogStatus GetStatus()
    {
        lock (_sync)
        {
            var options = _options();
            return new IptvWatchdogStatus
            {
                IdleChecksEnabled = options.EnableChannelHealthProbes,
                SweepEnabled = options.EnableChannelHealthSweep,
                State = options.EnableChannelHealthProbes ? _state : _state == "Checking" ? "Stopping" : "Disabled",
                LastCheckUtc = _lastCheck,
                Tuners = options.TunerHosts.Where(t => string.Equals(t.Type, "m3u", StringComparison.OrdinalIgnoreCase)).Select(TunerStatus).ToArray()
            };
        }
    }

    private IptvTunerHealth TunerStatus(TunerHostInfo tuner)
    {
        var sources = Sources(tuner);
        var result = new IptvTunerHealth { Id = tuner.Id, AccountReason = AccountReason(tuner), Sources = sources.Select(s => Summarize(tuner, s)).ToArray() };
        result.SharedDestinations = result.Sources.SelectMany(s => s.DestinationHosts.Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(h => h, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        var primary = sources.FirstOrDefault(s => Origin(s) == Origin(M3uUrlFailover.GetPrimaryUrl(tuner)));
        if (primary is null || result.AccountReason is not null)
        {
            return result;
        }

        foreach (var alternate in sources.Where(s => s != primary))
        {
            // Compare the same channels: a server tested on easy channels must not win
            // against a primary observed on a different, more demanding programme set.
            var comparable = Observations(tuner, primary).Select(o => o.Channel)
                .Intersect(Observations(tuner, alternate).Where(Verified).Select(o => o.Channel)).ToHashSet(StringComparer.Ordinal);
            var current = Samples(tuner, primary, comparable);
            var other = Samples(tuner, alternate, comparable);
            if (comparable.Count < 3 || current.Length < 6 || other.Length < 3)
            {
                continue;
            }

            // Different entry names can converge on the same CDN pool. Do not
            // recommend them as independent server alternatives on this evidence.
            if (current.Where(s => s.DestinationHost is not null).Select(s => s.DestinationHost)
                .Intersect(other.Where(s => s.DestinationHost is not null).Select(s => s.DestinationHost), StringComparer.OrdinalIgnoreCase).Any())
            {
                continue;
            }

            var currentRate = current.Count(s => s.Success && s.Interruptions == 0) / (double)current.Length;
            var otherRate = other.Count(s => s.Success && s.Interruptions == 0) / (double)other.Length;
            var currentStart = Median(current.Where(s => s.Decoded).Select(s => s.StartMilliseconds));
            var otherStart = Median(other.Where(s => s.Decoded).Select(s => s.StartMilliseconds));
            var failures = currentRate <= .7 && otherRate >= .9;
            var slow = current.Count(s => s.Decoded && s.StartMilliseconds >= 4000) >= 3
                && currentStart >= 4000 && otherStart < currentStart * .7 && otherRate >= .9;
            if (CanProbe(tuner, alternate) && (failures || slow))
            {
                result.RecommendedSourceId = SourceId(tuner, alternate);
                result.RecommendationReason = failures ? "RepeatedFailures" : "SlowStart";
                break;
            }
        }

        return result;
    }

    private IptvSourceHealth Summarize(TunerHostInfo tuner, string source)
    {
        var items = Observations(tuner, source).ToArray();
        var samples = Samples(tuner, source);
        var uri = new Uri(source);
        return new IptvSourceHealth
        {
            Id = SourceId(tuner, source),
            Host = uri.Host,
            Scheme = uri.Scheme,
            Active = Origin(source) == Origin(M3uUrlFailover.GetPrimaryUrl(tuner)),
            LastCheckedUtc = samples.Length > 0 ? samples.Max(s => s.Utc) : null,
            Samples = samples.Length,
            Channels = items.Count(o => o.Samples.Any(Fresh)),
            VerifiedChannels = items.Count(Verified),
            SuccessRate = samples.Length > 0 ? samples.Count(s => s.Success && s.Interruptions == 0) / (double)samples.Length : null,
            MedianStartMilliseconds = Median(samples.Where(s => s.Decoded).Select(s => s.StartMilliseconds)),
            Interruptions = samples.Sum(s => s.Interruptions),
            BytesReceived = samples.Sum(s => s.Bytes),
            Reason = samples.OrderByDescending(s => s.Utc).FirstOrDefault()?.Reason ?? (samples.Length < 3 ? "InsufficientEvidence" : null),
            DestinationHosts = samples.Where(s => s.DestinationHost is not null).Select(s => s.DestinationHost!).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToArray()
        };
    }

    private IEnumerable<Observation> Observations(TunerHostInfo tuner, string source)
    {
        var id = SourceId(tuner, source);
        return _history.Values.Where(o => o.Source == id);
    }

    private Sample[] Samples(TunerHostInfo tuner, string source, HashSet<string>? channels = null) => Observations(tuner, source)
        .Where(o => channels is null || channels.Contains(o.Channel)).SelectMany(o => o.Samples).Where(Fresh)
        .Where(s => s.Reason is not ("ProviderAuthentication" or "ProviderBusy")).ToArray();

    private bool Fresh(Sample sample) => sample.Utc <= _clock.GetUtcNow().UtcDateTime && sample.Utc >= _clock.GetUtcNow().UtcDateTime.AddHours(-6);

    private bool Verified(Observation item)
    {
        var latest = item.Samples.OrderByDescending(s => s.Utc).FirstOrDefault();
        return latest is not null && Fresh(latest) && latest.Success && latest.Decoded && latest.Interruptions == 0;
    }

    private string? AccountReason(TunerHostInfo tuner)
    {
        var access = AccessKey(tuner);
        var since = _clock.GetUtcNow().UtcDateTime.AddMinutes(-10);
        return _history.Values.Where(o => o.Access == access).SelectMany(o => o.Samples)
            .Where(s => s.Utc > since && s.Reason is "ProviderAuthentication" or "ProviderBusy")
            .OrderByDescending(s => s.Utc).FirstOrDefault()?.Reason;
    }

    private static long? Median(IEnumerable<long?> values)
    {
        var sorted = values.Where(v => v.HasValue).Select(v => v!.Value).Order().ToArray();
        return sorted.Length == 0 ? null : sorted[sorted.Length / 2];
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
                foreach (var key in _history.Where(p => !p.Value.Samples.Any(Fresh)).Select(p => p.Key).ToArray())
                {
                    _history.Remove(key);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_history));
                File.Move(_path + ".tmp", _path, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Could not persist IPTV observations ({ErrorType})", ex.GetType().Name);
            }
        }
    }

    internal sealed class Observation
    {
        public string Access { get; set; } = string.Empty;

        public string Source { get; set; } = string.Empty;

        public string Channel { get; set; } = string.Empty;

        public List<Sample> Samples { get; set; } = [];
    }

    internal sealed class Sample
    {
        public string Attempt { get; set; } = string.Empty;

        public DateTime Utc { get; set; }

        public bool Success { get; set; }

        public bool Decoded { get; set; }

        public long? StartMilliseconds { get; set; }

        public long Bytes { get; set; }

        public int Interruptions { get; set; }

        public string? Reason { get; set; }

        public string? DestinationHost { get; set; }
    }
}
