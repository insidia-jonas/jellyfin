using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.LiveTv.Configuration;
using Jellyfin.LiveTv.Timers;
using Jellyfin.LiveTv.TunerHosts;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.Health;

/// <summary>Rotates priority channels, backup sources and the channel catalog during idle periods.</summary>
public sealed class ChannelHealthWorker : BackgroundService
{
    private readonly ChannelHealthStore _store;
    private readonly IptvWatchdog _watchdog;
    private readonly ChannelProbeCoordinator _coordinator;
    private readonly ChannelMediaProbe _probe;
    private readonly ITunerHostManager _tuners;
    private readonly IConfigurationManager _config;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly ISessionManager _sessions;
    private readonly TimerManager _timers;
    private readonly ILogger<ChannelHealthWorker> _logger;
    private readonly Dictionary<string, DateTime> _attempted = new(StringComparer.Ordinal);
    private int _rotation;

    /// <summary>Initializes a new instance of the <see cref="ChannelHealthWorker"/> class.</summary>
    /// <param name="store">Observation history.</param>
    /// <param name="coordinator">Provider reservation coordinator.</param>
    /// <param name="probe">Media decoder.</param>
    /// <param name="tuners">Configured tuners.</param>
    /// <param name="config">Server configuration.</param>
    /// <param name="library">Library manager.</param>
    /// <param name="users">User manager.</param>
    /// <param name="sessions">Playback sessions.</param>
    /// <param name="timers">Recording timers.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="watchdog">Source observations and verification.</param>
    public ChannelHealthWorker(ChannelHealthStore store, ChannelProbeCoordinator coordinator, ChannelMediaProbe probe, ITunerHostManager tuners, IConfigurationManager config, ILibraryManager library, IUserManager users, ISessionManager sessions, TimerManager timers, ILogger<ChannelHealthWorker> logger, IptvWatchdog watchdog)
    {
        _store = store;
        _watchdog = watchdog;
        _coordinator = coordinator;
        _probe = probe;
        _tuners = tuners;
        _config = config;
        _library = library;
        _users = users;
        _sessions = sessions;
        _timers = timers;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Also observe progress for HLS/radio streams handled by ffmpeg or a native player.
        _sessions.PlaybackProgress += OnProgress;
        _sessions.PlaybackStopped += OnStopped;
        _config.NamedConfigurationUpdated += OnConfigurationUpdated;
        _coordinator.SetProbesEnabled(_config.GetLiveTvConfiguration().EnableChannelHealthProbes);
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    _store.Save();
                    _watchdog.Save();
                    await CheckOneAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Decoder stderr and provider paths may contain credentials; never log them.
                    _logger.LogWarning("Idle channel check failed ({ErrorType})", ex.GetType().Name);
                }
            }
        }
        finally
        {
            _sessions.PlaybackProgress -= OnProgress;
            _sessions.PlaybackStopped -= OnStopped;
            _config.NamedConfigurationUpdated -= OnConfigurationUpdated;
            _store.Save();
            _watchdog.Save();
        }
    }

    private void OnConfigurationUpdated(object? sender, ConfigurationUpdateEventArgs args)
    {
        if (string.Equals(args.Key, "livetv", StringComparison.OrdinalIgnoreCase))
        {
            _coordinator.SetProbesEnabled(_config.GetLiveTvConfiguration().EnableChannelHealthProbes);
        }
    }

    private void OnProgress(object? sender, PlaybackProgressEventArgs args)
    {
        if (args.IsAutomated || args.IsPaused || !args.PlaybackPositionTicks.HasValue || args.PlaybackPositionTicks.Value < TimeSpan.FromSeconds(2).Ticks || args.Item is null)
        {
            return;
        }

        var id = args.Item.ExternalId;
        if (string.IsNullOrEmpty(id) || !id.StartsWith("m3u", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _store.ConfirmClientProgress(id);
        _watchdog.ObserveClient(id, false);
    }

    private void OnStopped(object? sender, PlaybackStopEventArgs args)
    {
        if (args.Failed && args.Item?.ExternalId is { } id && id.StartsWith("m3u", StringComparison.OrdinalIgnoreCase))
        {
            _store.RecordClientFailure(id);
            _watchdog.ObserveClient(id, true);
        }
    }

    private async Task CheckOneAsync(CancellationToken stoppingToken)
    {
        var options = _config.GetLiveTvConfiguration();
        foreach (var host in options.TunerHosts)
        {
            _store.SynchronizeSource(host.Id, IptvWatchdog.SourceId(host, M3uUrlFailover.GetPrimaryUrl(host)));
        }

        if (!options.EnableChannelHealthProbes)
        {
            _watchdog.SetState("Disabled");
            return;
        }

        var now = DateTime.UtcNow;
        // Conservative across all clients and providers: no idle checks during any playback,
        // an active recording, or a recording whose pre-padding begins in the next minute.
        if (_sessions.Sessions.Any(s => s.NowPlayingItem is not null)
            || _timers.GetAll().Any(t => t.Status is RecordingStatus.New or RecordingStatus.InProgress
                && t.StartDate.AddSeconds(-t.PrePaddingSeconds) <= now.AddMinutes(1) && t.EndDate.AddSeconds(t.PostPaddingSeconds) > now))
        {
            _watchdog.SetState("PlaybackOrRecording");
            return;
        }

        var wanted = new HashSet<string>(_store.RecentChannels(), StringComparer.Ordinal);
        wanted.UnionWith(options.ChannelHealthPriorityIds ?? []);
        foreach (var user in _users.GetUsers().Where(u => u.HasPermission(PermissionKind.EnableLiveTvAccess)))
        {
            foreach (var item in _library.GetItemList(new InternalItemsQuery(user) { IsFavorite = true, IsFolder = false, Limit = 500, DtoOptions = new DtoOptions(false) }))
            {
                if (item.ExternalId?.StartsWith("m3u", StringComparison.OrdinalIgnoreCase) == true)
                {
                    wanted.Add(item.ExternalId);
                }
            }
        }

        var hosts = options.TunerHosts;
        var channels = _tuners.TunerHosts.OfType<M3UTunerHost>()
            .SelectMany(host => host.GetCachedChannels().Select(channel => (Host: host, Channel: channel)))
            .ToArray();
        var explicitPriority = new HashSet<string>(options.ChannelHealthPriorityIds ?? [], StringComparer.Ordinal);
        var representatives = channels.GroupBy(p => p.Channel.TunerHostId).SelectMany(group => group
                .OrderByDescending(p => explicitPriority.Contains(p.Channel.Id))
                .ThenByDescending(p => wanted.Contains(p.Channel.Id))
                .ThenByDescending(p => _store.GetHealth(p.Channel.Id).LastPlayedUtc)
                .ThenBy(p => p.Channel.Id, StringComparer.Ordinal).Take(8))
            .Select(p => p.Channel.Id).ToHashSet(StringComparer.Ordinal);
        var candidates = new List<(M3UTunerHost Host, ChannelInfo Channel, TunerHostInfo Tuner, string Source, int Lane, DateTime Checked)>();
        foreach (var pair in channels)
        {
            var tuner = hosts.FirstOrDefault(t => t.Id == pair.Channel.TunerHostId);
            if (tuner is null)
            {
                continue;
            }

            var primary = M3uUrlFailover.GetPrimaryUrl(tuner);
            var last = _store.GetHealth(pair.Channel.Id).LastCheckedUtc ?? DateTime.MinValue;
            var priority = wanted.Contains(pair.Channel.Id);
            if ((priority || options.EnableChannelHealthSweep) && _store.CanProbe(tuner.Id) && _watchdog.CanProbe(tuner, primary)
                && now - last >= TimeSpan.FromMinutes(priority ? 15 : 30))
            {
                candidates.Add((pair.Host, pair.Channel, tuner, primary, priority ? 0 : 2, last));
            }

            // Bound source comparisons to eight representative channels per tuner.
            // Recently used/favorite channels take precedence over catalog controls.
            if (representatives.Contains(pair.Channel.Id))
            {
                foreach (var source in IptvWatchdog.Sources(tuner).Where(s => s != primary))
                {
                    var checkedAt = _watchdog.LastChecked(tuner, source, pair.Channel.Id);
                    if (_watchdog.CanProbe(tuner, source) && now - checkedAt >= TimeSpan.FromHours(2))
                    {
                        candidates.Add((pair.Host, pair.Channel, tuner, source, 1, checkedAt));
                    }
                }
            }
        }

        var lane = _rotation++ % 3;
        _watchdog.SetState("Waiting");
        foreach (var candidate in candidates.Where(c => now - _attempted.GetValueOrDefault(c.Channel.Id + "|" + c.Source) >= TimeSpan.FromMinutes(15))
                     .OrderBy(c => (c.Lane - lane + 3) % 3).ThenBy(c => c.Checked))
        {
            var pair = (candidate.Host, candidate.Channel);
            var tuner = candidate.Tuner;

            // Unknown provider capacity (TunerCount == 0) is treated conservatively too:
            // exactly one probe globally, never alongside a foreground stream.
            using var reservation = _coordinator.TryAcquireProbe();
            if (reservation is null)
            {
                _watchdog.SetState("PlaybackCooldown");
                return;
            }

            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, reservation.Cancellation.Token);
            try
            {
                _watchdog.SetState("Checking");
                var source = pair.Host.CreateProbeSource(tuner, pair.Channel);
                source.Path = M3uUrlFailover.RewriteStreamUrl(source.Path, candidate.Source);
                var result = await _probe.ProbeAsync(source, cancelled.Token, pair.Channel.ChannelType == ChannelType.TV).ConfigureAwait(false);
                _attempted[pair.Channel.Id + "|" + candidate.Source] = now;
                if (_attempted.Count > 5000)
                {
                    _attempted.Remove(_attempted.MinBy(p => p.Value).Key);
                }

                _watchdog.Record(tuner, candidate.Source, pair.Channel.Id, Guid.NewGuid().ToString("N"), result.Success, result.Success,
                    result.FirstMediaMilliseconds, reason: result.Failure?.Reason, destinationHost: result.DestinationHost);
                // A backup's result describes the backup, not the user's current channel.
                if (candidate.Source == M3uUrlFailover.GetPrimaryUrl(tuner) && result.Success)
                {
                    _store.Success(pair.Channel.Id, tuner.Id, result.FirstMediaMilliseconds, decoded: true);
                }
                else if (result.Failure is not null && (candidate.Source == M3uUrlFailover.GetPrimaryUrl(tuner) || result.Failure.StopRetries))
                {
                    _store.Failure(pair.Channel.Id, tuner.Id, result.Failure.Reason, result.Failure.ProviderWide);
                }

                _logger.LogInformation("Idle channel check {ChannelId}: {Result} in {Elapsed}ms", pair.Channel.Id, result.Success ? "media decoded" : result.Failure?.Reason ?? "unsupported", result.ElapsedMilliseconds);
                _watchdog.SetState("Waiting", completed: true);
            }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested)
            {
                // Preemption does not count as a failure and does not penalize the channel.
                _watchdog.SetState("PlaybackCooldown");
            }

            return;
        }
    }
}
