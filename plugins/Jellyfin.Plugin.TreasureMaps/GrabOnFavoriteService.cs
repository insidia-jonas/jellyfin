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
    private readonly TreasureMapsApiClient _client;
    private readonly SabnzbdClient _sabnzbd;
    private readonly ILogger<GrabOnFavoriteService> _logger;
    private readonly ConcurrentDictionary<string, byte> _handled = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="GrabOnFavoriteService"/> class.
    /// </summary>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="client">The Treasure-Maps API client.</param>
    /// <param name="sabnzbd">The SABnzbd client.</param>
    /// <param name="logger">The logger.</param>
    public GrabOnFavoriteService(IUserDataManager userDataManager, TreasureMapsApiClient client, SabnzbdClient sabnzbd, ILogger<GrabOnFavoriteService> logger)
    {
        _userDataManager = userDataManager;
        _client = client;
        _sabnzbd = sabnzbd;
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

            if (!_handled.TryAdd(guid, 0))
            {
                return; // already grabbed this session
            }

            var isTv = string.Equals(item.GetProviderId("TreasureMapsKind"), "tv", StringComparison.OrdinalIgnoreCase);
            _ = GrabAsync(guid!, item.Name ?? guid!, isTv);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Grab-on-favorite handler failed");
        }
    }

    private async Task GrabAsync(string guid, string name, bool isTv)
    {
        try
        {
            if (!SabnzbdClient.IsConfigured)
            {
                _logger.LogWarning("Favorited '{Name}' but SABnzbd is not configured — cannot grab.", name);
                return;
            }

            var payload = await _client.DownloadNzbAsync(guid, CancellationToken.None).ConfigureAwait(false);
            var config = Plugin.Instance!.Configuration;
            var preferred = isTv ? config.SabnzbdTvCategory : config.SabnzbdMovieCategory;
            var category = string.IsNullOrWhiteSpace(preferred) ? config.SabnzbdCategory : preferred;

            var ids = await _sabnzbd.AddNzbAsync(payload, Sanitize(name), category, CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("Grab-on-favorite: sent '{Name}' to SABnzbd category '{Category}' ({Ids})", name, category, string.Join(",", ids));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Grab-on-favorite failed for {Guid}", guid);
            _handled.TryRemove(guid, out _);
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
