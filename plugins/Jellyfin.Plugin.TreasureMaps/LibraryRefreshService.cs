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
/// or points somewhere else. Completed paths are retried until discovery succeeds.
/// </summary>
public sealed class LibraryRefreshService : BackgroundService
{
    private readonly ILibraryManager _libraryManager;
    private readonly SabnzbdClient _sabnzbd;
    private readonly GrabService _grabService;
    private readonly ILogger<LibraryRefreshService> _logger;
    private readonly HashSet<string> _seenCompleted = new(StringComparer.Ordinal);
    private readonly CompletedDownloadImporter _importer;
    private bool _attached;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryRefreshService"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="sabnzbd">The SABnzbd client.</param>
    /// <param name="grabService">The grab store (only Treasure-Maps jobs trigger an import).</param>
    /// <param name="importer">Discovers completed files without a global scan.</param>
    /// <param name="logger">The logger.</param>
    public LibraryRefreshService(
        ILibraryManager libraryManager,
        SabnzbdClient sabnzbd,
        GrabService grabService,
        CompletedDownloadImporter importer,
        ILogger<LibraryRefreshService> logger)
    {
        _libraryManager = libraryManager;
        _sabnzbd = sabnzbd;
        _grabService = grabService;
        _importer = importer;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            do
            {
                try { await ImportCompletedAsync(stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { _logger.LogDebug(ex, "Completed download import will retry"); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

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

    private async Task ImportCompletedAsync(CancellationToken cancellationToken)
    {
        if (!SabnzbdClient.IsConfigured) { return; }
        var (_, items) = await _sabnzbd.GetDownloadStatusAsync(cancellationToken).ConfigureAwait(false);
        var completed = items.Where(i => IsCompleted(i) && _grabService.IsTracked(i.Id, i.Name)
            && !_seenCompleted.Contains(i.Id ?? i.Name ?? string.Empty)).ToList();
        if (!_attached || completed.Count > 0)
        {
            await AttachCompletedFoldersAsync(completed, cancellationToken).ConfigureAwait(false);
            _attached = true;
        }

        foreach (var item in completed)
        {
            if (await _importer.ImportAsync(item.Storage, cancellationToken).ConfigureAwait(false))
            {
                _seenCompleted.Add(item.Id ?? item.Name ?? string.Empty);
                _logger.LogInformation("Completed Treasure-Maps download '{Name}' discovered; metadata queued", item.Name);
            }
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

    private static bool IsPathChange(string status)
        => status is "created" or "path added";

    private static bool IsCompleted(SabnzbdClient.SabDownloadStatus item)
    {
        var status = item.Status ?? string.Empty;
        return status.Equals("Completed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Complete", StringComparison.OrdinalIgnoreCase);
    }
}
