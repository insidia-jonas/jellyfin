using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TreasureMaps;

/// <summary>
/// Scans the Movies / TV Shows libraries after SABnzbd finishes a job, so grabbed titles appear
/// under those menu entries without a manual "Scan library". Also runs one delayed scan on
/// startup (files that landed while the server was down).
/// </summary>
public sealed class LibraryRefreshService : IHostedService, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly SabnzbdClient _sabnzbd;
    private readonly ILogger<LibraryRefreshService> _logger;
    private readonly HashSet<string> _seenCompleted = new(StringComparer.Ordinal);
    private Timer? _timer;
    private int _busy;
    private bool _seeded;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryRefreshService"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="sabnzbd">The SABnzbd client.</param>
    /// <param name="logger">The logger.</param>
    public LibraryRefreshService(ILibraryManager libraryManager, SabnzbdClient sabnzbd, ILogger<LibraryRefreshService> logger)
    {
        _libraryManager = libraryManager;
        _sabnzbd = sabnzbd;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(static s => ((LibraryRefreshService)s!).Tick(), this, TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(90));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => _timer?.Dispose();

    /// <summary>
    /// Scans every media library so newly completed downloads are picked up.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the scan finishes.</returns>
    public async Task ScanAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Scanning media libraries for completed Treasure-Maps downloads");
        await _libraryManager.ValidateMediaLibrary(new Progress<double>(), cancellationToken).ConfigureAwait(false);
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            if (!_seeded)
            {
                _seeded = true;
                await SeedAndStartupScanAsync().ConfigureAwait(false);
                return;
            }

            if (!SabnzbdClient.IsConfigured)
            {
                return;
            }

            var (_, items) = await _sabnzbd.GetDownloadStatusAsync(CancellationToken.None).ConfigureAwait(false);
            var newlyCompleted = false;
            foreach (var item in items.Where(IsCompleted))
            {
                var id = item.Id ?? item.Name ?? string.Empty;
                if (id.Length > 0 && _seenCompleted.Add(id))
                {
                    newlyCompleted = true;
                    _logger.LogInformation("SABnzbd finished '{Name}' — library scan queued", item.Name);
                }
            }

            if (newlyCompleted)
            {
                await ScanAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Library refresh tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private async Task SeedAndStartupScanAsync()
    {
        try
        {
            if (SabnzbdClient.IsConfigured)
            {
                var (_, items) = await _sabnzbd.GetDownloadStatusAsync(CancellationToken.None).ConfigureAwait(false);
                foreach (var item in items.Where(IsCompleted))
                {
                    var id = item.Id ?? item.Name ?? string.Empty;
                    if (id.Length > 0)
                    {
                        _seenCompleted.Add(id);
                    }
                }
            }

            await ScanAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Startup library scan failed");
        }
    }

    private static bool IsCompleted(SabnzbdClient.SabDownloadStatus item)
    {
        var status = item.Status ?? string.Empty;
        return status.Equals("Completed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Complete", StringComparison.OrdinalIgnoreCase);
    }
}
