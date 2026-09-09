using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TreasureMaps;

/// <summary>
/// Scans the Movies / TV Shows libraries after SABnzbd finishes a Treasure-Maps job, so grabbed
/// titles appear under those menu entries without a manual "Scan library". Also attaches the
/// real completed-folder path from SABnzbd history when the configured library folder is empty
/// or points somewhere else, and runs one delayed scan on startup.
/// </summary>
public sealed class LibraryRefreshService : IHostedService, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly SabnzbdClient _sabnzbd;
    private readonly GrabService _grabService;
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
    /// <param name="grabService">The grab store (only Treasure-Maps jobs trigger an import).</param>
    /// <param name="logger">The logger.</param>
    public LibraryRefreshService(
        ILibraryManager libraryManager,
        SabnzbdClient sabnzbd,
        GrabService grabService,
        ILogger<LibraryRefreshService> logger)
    {
        _libraryManager = libraryManager;
        _sabnzbd = sabnzbd;
        _grabService = grabService;
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
        await AttachFromSabnzbdAsync(cancellationToken).ConfigureAwait(false);
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
            foreach (var item in items.Where(i => IsCompleted(i) && _grabService.IsTracked(i.Id, i.Name)))
            {
                var id = item.Id ?? item.Name ?? string.Empty;
                if (id.Length > 0 && _seenCompleted.Add(id))
                {
                    newlyCompleted = true;
                    _logger.LogInformation("SABnzbd finished '{Name}' — library import queued", item.Name);
                }
            }

            if (newlyCompleted || LibraryLooksEmpty())
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
                foreach (var item in items.Where(i => IsCompleted(i) && _grabService.IsTracked(i.Id, i.Name)))
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

    private async Task AttachFromSabnzbdAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<SabnzbdClient.SabDownloadStatus> tracked = Array.Empty<SabnzbdClient.SabDownloadStatus>();
        if (SabnzbdClient.IsConfigured)
        {
            try
            {
                var (_, items) = await _sabnzbd.GetDownloadStatusAsync(cancellationToken).ConfigureAwait(false);
                tracked = items.Where(i => IsCompleted(i) && _grabService.IsTracked(i.Id, i.Name)).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read SABnzbd status before library attach");
            }
        }

        await AttachCompletedFoldersAsync(tracked, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Points Movies / TV Shows at the configured SABnzbd folders and at each completed job's
    /// real storage path (history <c>storage</c>), then reports whether a path was added.
    /// </summary>
    private async Task<bool> AttachCompletedFoldersAsync(
        IReadOnlyList<SabnzbdClient.SabDownloadStatus> completed,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        string? completeDir = null;
        if (SabnzbdClient.IsConfigured)
        {
            try
            {
                completeDir = await _sabnzbd.GetCompleteDirAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read SABnzbd complete_dir");
            }
        }

        var moviesPath = LibraryPaths.Resolve(config?.SabnzbdMovieFolder, config?.SabnzbdMovieCategory, completeDir);
        var tvPath = LibraryPaths.Resolve(config?.SabnzbdTvFolder, config?.SabnzbdTvCategory, completeDir);
        var changed = false;

        if (moviesPath is not null)
        {
            changed |= IsPathChange(await LibrarySetup.EnsureAsync(
                _libraryManager, "Movies", CollectionTypeOptions.movies, moviesPath, _logger).ConfigureAwait(false));
        }

        if (tvPath is not null)
        {
            changed |= IsPathChange(await LibrarySetup.EnsureAsync(
                _libraryManager, "TV Shows", CollectionTypeOptions.tvshows, tvPath, _logger).ConfigureAwait(false));
        }

        foreach (var item in completed)
        {
            var rec = _grabService.Lookup(item.Id, item.Name);
            var isTv = string.Equals(rec?.Kind, "tv", StringComparison.OrdinalIgnoreCase);
            var configured = isTv ? tvPath : moviesPath;
            var root = LibraryPaths.LibraryRootFromCompletedPath(item.Storage, configured);
            if (root is null)
            {
                continue;
            }

            var status = await LibrarySetup.EnsureAsync(
                _libraryManager,
                isTv ? "TV Shows" : "Movies",
                isTv ? CollectionTypeOptions.tvshows : CollectionTypeOptions.movies,
                root,
                _logger).ConfigureAwait(false);
            changed |= IsPathChange(status);
        }

        return changed;
    }

    private bool LibraryLooksEmpty()
    {
        return FolderHasNoVideos(CollectionTypeOptions.movies, "Movies")
            || FolderHasNoVideos(CollectionTypeOptions.tvshows, "TV Shows");
    }

    private bool FolderHasNoVideos(CollectionTypeOptions collectionType, string name)
    {
        var folder = LibrarySetup.Find(_libraryManager, name, collectionType);
        if (folder?.Locations is null || folder.Locations.Length == 0)
        {
            return true;
        }

        return folder.Locations.All(p => LibraryPaths.CountVideos(p) <= 0);
    }

    private static bool IsPathChange(string status)
        => status is "created" or "path added";

    private static bool IsCompleted(SabnzbdClient.SabDownloadStatus item)
    {
        var status = item.Status ?? string.Empty;
        return status.Equals("Completed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Complete", StringComparison.OrdinalIgnoreCase);
    }
}
