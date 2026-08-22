using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TreasureMaps;

/// <summary>
/// Injects the plugin's client script into the web client's index.html (the same pattern other
/// UI-enhancing plugins use), so details pages get the release list with download buttons and
/// live SABnzbd status. Idempotent: guarded by a marker attribute.
/// </summary>
public class WebScriptInjector : IHostedService
{
    private const string ScriptMarker = "plugin=\"TreasureMaps\"";
    private const string ScriptTag = "<script plugin=\"TreasureMaps\" defer src=\"/TreasureMaps/ClientScript?v=3\"></script>";

    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<WebScriptInjector> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebScriptInjector"/> class.
    /// </summary>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public WebScriptInjector(IApplicationPaths appPaths, ILogger<WebScriptInjector> logger)
    {
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var indexPath = Path.Combine(_appPaths.WebPath ?? string.Empty, "index.html");
            if (!File.Exists(indexPath))
            {
                _logger.LogWarning("Web client index.html not found at {Path}; client script not injected", indexPath);
                return;
            }

            var html = await File.ReadAllTextAsync(indexPath, cancellationToken).ConfigureAwait(false);
            var updated = ApplyScriptTag(html);
            if (updated is null)
            {
                return;
            }

            if (string.Equals(updated, html, StringComparison.Ordinal))
            {
                return;
            }

            await File.WriteAllTextAsync(indexPath, updated, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Injected the Treasure-Maps client script into {Path}", indexPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to inject the Treasure-Maps client script (web UI enhancements disabled)");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Inserts or replaces the Treasure-Maps script tag. Returns the original HTML when
    /// <c>&lt;/body&gt;</c> is missing, or the already-current HTML when the tag is up to date.
    /// </summary>
    /// <param name="html">The index.html contents.</param>
    /// <returns>The updated HTML, or null when it cannot be patched.</returns>
    public static string? ApplyScriptTag(string html)
    {
        if (html.Contains(ScriptTag, StringComparison.Ordinal))
        {
            return html;
        }

        var existing = System.Text.RegularExpressions.Regex.Match(
            html,
            "<script[^>]*" + ScriptMarker + "[^>]*>\\s*</script>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (existing.Success)
        {
            return html[..existing.Index] + ScriptTag + html[(existing.Index + existing.Length)..];
        }

        var closing = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (closing < 0)
        {
            return null;
        }

        return html[..closing] + ScriptTag + html[closing..];
    }
}

/// <summary>
/// Periodically queues a metadata+image refresh for persons without a primary image, so actor
/// photos (fetched from TMDB by name) appear on details pages. Channel items add people by name
/// only, and nothing else ever refreshes them.
/// </summary>
public class PeopleImageService : IHostedService, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly MediaBrowser.Model.IO.IFileSystem _fileSystem;
    private readonly ILogger<PeopleImageService> _logger;
    private Timer? _timer;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeopleImageService"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="providerManager">The provider manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="logger">The logger.</param>
    public PeopleImageService(ILibraryManager libraryManager, IProviderManager providerManager, MediaBrowser.Model.IO.IFileSystem fileSystem, ILogger<PeopleImageService> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(_ => Run(), null, TimeSpan.FromMinutes(3), TimeSpan.FromHours(12));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases resources.
    /// </summary>
    /// <param name="disposing">Whether managed resources should be disposed.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer?.Dispose();
        }
    }

    private void Run()
    {
        try
        {
            var queued = QueueMissingPeopleImages(_libraryManager, _providerManager, _fileSystem, _logger, 300);
            if (queued > 0)
            {
                _logger.LogInformation("Queued image refreshes for {Count} persons without a photo", queued);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "People image refresh sweep failed");
        }
    }

    /// <summary>
    /// Queues a full metadata+image refresh for persons that have no primary image yet.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="providerManager">The provider manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="maxCount">The maximum number of refreshes to queue.</param>
    /// <returns>The number of queued refreshes.</returns>
    public static int QueueMissingPeopleImages(ILibraryManager libraryManager, IProviderManager providerManager, MediaBrowser.Model.IO.IFileSystem fileSystem, ILogger logger, int maxCount)
    {
        var persons = libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Person },
            Recursive = true
        });

        var queued = 0;
        foreach (var person in persons.Where(p => !p.HasImage(ImageType.Primary)))
        {
            providerManager.QueueRefresh(
                person.Id,
                new MetadataRefreshOptions(new MediaBrowser.Controller.Providers.DirectoryService(fileSystem))
                {
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                    ImageRefreshMode = MetadataRefreshMode.FullRefresh
                },
                MediaBrowser.Controller.Providers.RefreshPriority.Low);

            if (++queued >= maxCount)
            {
                break;
            }
        }

        return queued;
    }
}
