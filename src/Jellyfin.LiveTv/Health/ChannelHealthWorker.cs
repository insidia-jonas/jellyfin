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

/// <summary>Checks only favorites and recent channels, one at a time during idle periods.</summary>
public sealed class ChannelHealthWorker : BackgroundService
{
    private readonly ChannelHealthStore _store;
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
    public ChannelHealthWorker(ChannelHealthStore store, ChannelProbeCoordinator coordinator, ChannelMediaProbe probe, ITunerHostManager tuners, IConfigurationManager config, ILibraryManager library, IUserManager users, ISessionManager sessions, TimerManager timers, ILogger<ChannelHealthWorker> logger)
    {
        _store = store;
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
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    _store.Save();
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
            _store.Save();
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
    }

    private void OnStopped(object? sender, PlaybackStopEventArgs args)
    {
        if (args.Failed && args.Item?.ExternalId is { } id && id.StartsWith("m3u", StringComparison.OrdinalIgnoreCase))
        {
            _store.RecordClientFailure(id);
        }
    }

    private async Task CheckOneAsync(CancellationToken stoppingToken)
    {
        if (!_config.GetLiveTvConfiguration().EnableChannelHealthProbes)
        {
            return;
        }

        var now = DateTime.UtcNow;
        // Conservative across all clients and providers: no idle checks during any playback,
        // an active recording, or a recording whose pre-padding begins in the next minute.
        if (_sessions.Sessions.Any(s => s.NowPlayingItem is not null)
            || _timers.GetAll().Any(t => t.Status is RecordingStatus.New or RecordingStatus.InProgress
                && t.StartDate.AddSeconds(-t.PrePaddingSeconds) <= now.AddMinutes(1) && t.EndDate.AddSeconds(t.PostPaddingSeconds) > now))
        {
            return;
        }

        var wanted = new HashSet<string>(_store.RecentChannels(), StringComparer.Ordinal);
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

        var hosts = _config.GetLiveTvConfiguration().TunerHosts;
        var candidates = _tuners.TunerHosts.OfType<M3UTunerHost>()
            .SelectMany(host => host.GetCachedChannels().Select(channel => (Host: host, Channel: channel)))
            .Where(pair => wanted.Contains(pair.Channel.Id) && _store.CanProbe(pair.Channel.TunerHostId)
                && now - _attempted.GetValueOrDefault(pair.Channel.Id) >= TimeSpan.FromMinutes(15)
                && (!_store.GetHealth(pair.Channel.Id).LastCheckedUtc.HasValue || now - _store.GetHealth(pair.Channel.Id).LastCheckedUtc!.Value >= TimeSpan.FromMinutes(15)))
            .OrderBy(pair => _store.GetHealth(pair.Channel.Id).LastCheckedUtc ?? DateTime.MinValue)
            .ThenBy(pair => _attempted.GetValueOrDefault(pair.Channel.Id));
        foreach (var pair in candidates)
        {
            var tuner = hosts.FirstOrDefault(t => t.Id == pair.Channel.TunerHostId);
            if (tuner is null)
            {
                continue;
            }

            // Unknown provider capacity (TunerCount == 0) is treated conservatively too:
            // exactly one probe globally, never alongside a foreground stream.
            using var reservation = _coordinator.TryAcquireProbe();
            if (reservation is null)
            {
                return;
            }

            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, reservation.Cancellation.Token);
            try
            {
                var result = await _probe.ProbeAsync(pair.Host.CreateProbeSource(tuner, pair.Channel), cancelled.Token, pair.Channel.ChannelType == ChannelType.TV).ConfigureAwait(false);
                _attempted[pair.Channel.Id] = now;
                if (_attempted.Count > 5000)
                {
                    _attempted.Remove(_attempted.MinBy(p => p.Value).Key);
                }

                if (result.Success)
                {
                    _store.Success(pair.Channel.Id, tuner.Id);
                }
                else if (result.Failure is not null)
                {
                    _store.Failure(pair.Channel.Id, tuner.Id, result.Failure.Reason, result.Failure.ProviderWide);
                }

                _logger.LogInformation("Idle channel check {ChannelId}: {Result} in {Elapsed}ms", pair.Channel.Id, result.Success ? "media decoded" : result.Failure?.Reason ?? "unsupported", result.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested)
            {
                // Preemption does not count as a failure and does not penalize the channel.
            }

            return;
        }
    }
}
