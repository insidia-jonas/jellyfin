using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.TunerHosts;

/// <summary>
/// Loads last-good M3U snapshots at server start so the first Fire TV / web open is not a cold playlist download.
/// </summary>
public sealed class M3uListingWarmupHost : IHostedService
{
    private readonly ITunerHostManager _tunerHostManager;
    private readonly ILogger<M3uListingWarmupHost> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="M3uListingWarmupHost"/> class.
    /// </summary>
    /// <param name="tunerHostManager">The tuner host manager.</param>
    /// <param name="logger">The logger.</param>
    public M3uListingWarmupHost(ITunerHostManager tunerHostManager, ILogger<M3uListingWarmupHost> logger)
    {
        _tunerHostManager = tunerHostManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var host in _tunerHostManager.TunerHosts)
        {
            if (host is not BaseTunerHost tunerHost)
            {
                continue;
            }

            try
            {
                await tunerHost.WarmAllAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Live TV listing warmup failed for {Host}", host.Name);
            }
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
