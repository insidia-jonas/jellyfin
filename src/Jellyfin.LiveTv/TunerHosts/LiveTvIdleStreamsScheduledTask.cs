using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.TunerHosts;

/// <summary>
/// Tears down tuner live streams that no client is reading, so IPTV slots are released.
/// </summary>
public class LiveTvIdleStreamsScheduledTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ILogger<LiveTvIdleStreamsScheduledTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveTvIdleStreamsScheduledTask"/> class.
    /// </summary>
    /// <param name="mediaSourceManager">The media source manager.</param>
    /// <param name="logger">The logger.</param>
    public LiveTvIdleStreamsScheduledTask(
        IMediaSourceManager mediaSourceManager,
        ILogger<LiveTvIdleStreamsScheduledTask> logger)
    {
        _mediaSourceManager = mediaSourceManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Live TV idle streams";

    /// <inheritdoc />
    public string Key => "LiveTvIdleStreams";

    /// <inheritdoc />
    public string Description => "Closes Live TV streams that were opened (AutoOpen) but are no longer being watched.";

    /// <inheritdoc />
    public string Category => "Live TV";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => false;

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var closed = await _mediaSourceManager.CloseIdleLiveStreams().ConfigureAwait(false);
        if (closed > 0)
        {
            _logger.LogInformation("Closed {Count} idle Live TV stream(s)", closed);
        }

        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromMinutes(1).Ticks
            }
        ];
    }
}
