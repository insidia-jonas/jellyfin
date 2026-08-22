using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TreasureMaps;

/// <summary>
/// Lets the user trigger a grab from the <b>normal</b> Jellyfin UI: marking a Treasure-Maps channel
/// item as a favorite (the heart) downloads its NZB into SABnzbd with the matching category.
/// </summary>
public sealed class GrabOnFavoriteService : IHostedService
{
    private readonly IUserDataManager _userDataManager;
    private readonly GrabService _grabService;
    private readonly ILogger<GrabOnFavoriteService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="GrabOnFavoriteService"/> class.
    /// </summary>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="grabService">The shared grab service.</param>
    /// <param name="logger">The logger.</param>
    public GrabOnFavoriteService(IUserDataManager userDataManager, GrabService grabService, ILogger<GrabOnFavoriteService> logger)
    {
        _userDataManager = userDataManager;
        _grabService = grabService;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved += OnUserDataSaved;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        return Task.CompletedTask;
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        try
        {
            if (e.UserData?.IsFavorite != true)
            {
                return;
            }

            var config = Plugin.Instance?.Configuration;
            if (config is null || !config.GrabOnFavorite)
            {
                return;
            }

            var item = e.Item;
            if (item is null)
            {
                return;
            }

            var guid = item.GetProviderId("TreasureMaps");
            _logger.LogDebug("Favorite event: item='{Name}' reason={Reason} tmGuid='{Guid}'", item.Name, e.SaveReason, guid);
            if (string.IsNullOrEmpty(guid))
            {
                return;
            }

            var isTv = string.Equals(item.GetProviderId("TreasureMapsKind"), "tv", StringComparison.OrdinalIgnoreCase);
            var cover = item.PrimaryImagePath;
            var storedTitle = item.GetProviderId("TreasureMapsTitle");
            if (string.IsNullOrWhiteSpace(storedTitle))
            {
                storedTitle = item.OriginalTitle;
            }

            if (DownloadTitle.LooksLikeQualityLabel(storedTitle))
            {
                storedTitle = null;
            }

            if (string.IsNullOrWhiteSpace(storedTitle) && !DownloadTitle.LooksLikeQualityLabel(item.Name))
            {
                storedTitle = item.Name;
            }

            var quality = item.GetProviderId("TreasureMapsQuality");
            if (string.IsNullOrWhiteSpace(quality) && DownloadTitle.LooksLikeQualityLabel(item.Name))
            {
                quality = item.Name;
            }

            var jobName = string.IsNullOrWhiteSpace(storedTitle) ? (item.Name ?? guid!) : storedTitle;
            _ = RunGrabAsync(guid!, jobName, isTv, cover, storedTitle, quality);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Grab-on-favorite handler failed");
        }
    }

    private async Task RunGrabAsync(string guid, string name, bool isTv, string? cover, string? title, string? quality)
    {
        try
        {
            await _grabService.GrabAsync(guid, name, isTv, cover, CancellationToken.None, title, quality).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Grab-on-favorite failed for {Guid}", guid);
        }
    }
}
