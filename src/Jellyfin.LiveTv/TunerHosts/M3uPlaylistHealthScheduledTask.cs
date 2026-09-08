using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.LiveTv.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.TunerHosts;

/// <summary>
/// Tests configured M3U ingest URLs every 15 minutes and switches to the most stable one.
/// </summary>
public class M3uPlaylistHealthScheduledTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly IConfigurationManager _config;
    private readonly ITunerHostManager _tunerHostManager;
    private readonly M3uPlaylistHealthChecker _healthChecker;
    private readonly ILogger<M3uPlaylistHealthScheduledTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="M3uPlaylistHealthScheduledTask"/> class.
    /// </summary>
    /// <param name="config">The configuration manager.</param>
    /// <param name="tunerHostManager">The tuner host manager.</param>
    /// <param name="healthChecker">The playlist health checker.</param>
    /// <param name="logger">The logger.</param>
    public M3uPlaylistHealthScheduledTask(
        IConfigurationManager config,
        ITunerHostManager tunerHostManager,
        M3uPlaylistHealthChecker healthChecker,
        ILogger<M3uPlaylistHealthScheduledTask> logger)
    {
        _config = config;
        _tunerHostManager = tunerHostManager;
        _healthChecker = healthChecker;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "M3U playlist health";

    /// <inheritdoc />
    public string Key => "M3uPlaylistHealth";

    /// <inheritdoc />
    public string Description => "Tests M3U ingest servers and switches to the URL with the most reliable, hang-free delivery.";

    /// <inheritdoc />
    public string Category => "Live TV";

    /// <inheritdoc />
    public bool IsHidden => !_config.GetLiveTvConfiguration().TunerHosts.Any(static t => string.Equals(t.Type, "m3u", StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = _config.GetLiveTvConfiguration();
        var tuners = config.TunerHosts
            .Where(static t => string.Equals(t.Type, "m3u", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (tuners.Count == 0)
        {
            progress.Report(100);
            return;
        }

        var changed = false;
        for (var i = 0; i < tuners.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            changed |= await UpdateTunerAsync(tuners[i], cancellationToken).ConfigureAwait(false);
            progress.Report(((i + 1) * 100d) / tuners.Count);
        }

        if (changed)
        {
            _config.SaveConfiguration("livetv", config);
            foreach (var host in _tunerHostManager.TunerHosts.OfType<BaseTunerHost>())
            {
                if (string.Equals(host.Type, "m3u", StringComparison.OrdinalIgnoreCase))
                {
                    host.ClearChannelCache();
                }
            }
        }
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromMinutes(15).Ticks
            }
        ];
    }

    private async Task<bool> UpdateTunerAsync(TunerHostInfo tuner, CancellationToken cancellationToken)
    {
        var candidates = M3uUrlFailover.GetHealthCandidates(tuner);
        if (candidates.Count <= 1)
        {
            return false;
        }

        M3uPlaylistHealthResult? best = null;
        M3uPlaylistHealthResult? current = null;
        var currentUrl = M3uUrlFailover.GetPrimaryUrl(tuner);

        foreach (var url in candidates)
        {
            var result = await _healthChecker.ProbeAsync(url, tuner, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "M3U health {Url}: success={Success} score={Score:0.###} elapsed={Elapsed}ms bytes={Bytes}",
                result.Url,
                result.Success,
                result.Score,
                result.ElapsedMs,
                result.BytesRead);

            if (string.Equals(url, currentUrl, StringComparison.OrdinalIgnoreCase))
            {
                current = result;
            }

            if (best is null || result.Score > best.Score)
            {
                best = result;
            }
        }

        if (best is null || !M3uUrlFailover.ShouldSwitchActiveUrl(currentUrl, current, best))
        {
            if (best is null || !best.Success)
            {
                _logger.LogWarning("No healthy M3U ingest URL found for tuner {TunerId}", tuner.Id);
            }

            return false;
        }

        _logger.LogInformation("Switching M3U ingest for tuner {TunerId} to {Url}", tuner.Id, best.Url);
        tuner.ActiveUrl = best.Url;
        return true;
    }
}
