using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.Channels;

/// <summary>
/// A My Media library tile for Live TV, shown next to Movies, TV Shows, and other channels.
/// </summary>
public class LiveTvLibraryChannel : IChannel, IRequiresMediaInfoCallback, IHasCacheKey
{
    private readonly ITunerHostManager _tunerHostManager;
    private readonly IListingsManager _listingsManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<LiveTvLibraryChannel> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveTvLibraryChannel"/> class.
    /// </summary>
    /// <param name="tunerHostManager">The tuner host manager.</param>
    /// <param name="listingsManager">The listings manager (logos from XMLTV).</param>
    /// <param name="libraryManager">The library manager (imported guide).</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="logger">The logger.</param>
    public LiveTvLibraryChannel(
        ITunerHostManager tunerHostManager,
        IListingsManager listingsManager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILogger<LiveTvLibraryChannel> logger)
    {
        _tunerHostManager = tunerHostManager;
        _listingsManager = listingsManager;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Live TV";

    /// <inheritdoc />
    public string Description => "Live television and IPTV channels.";

    /// <inheritdoc />
    public string DataVersion => "5";

    /// <inheritdoc />
    public string HomePageUrl => string.Empty;

    /// <inheritdoc />
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    /// <inheritdoc />
    public string? GetCacheKey(string? userId)
    {
        // Refresh now/next every 10 minutes so Fire TV tiles do not stay on a finished show.
        var now = DateTime.UtcNow;
        return now.ToString("yyyyMMddHH", System.Globalization.CultureInfo.InvariantCulture)
               + (now.Minute / 10).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public InternalChannelFeatures GetChannelFeatures()
    {
        return new InternalChannelFeatures
        {
            MediaTypes = [ChannelMediaType.Video],
            ContentTypes = [ChannelMediaContentType.TvExtra],
            DefaultSortFields = [ChannelItemSortField.Name],
            AutoRefreshLevels = 3
        };
    }

    /// <inheritdoc />
    public bool IsEnabledFor(string userId)
    {
        if (!Guid.TryParse(userId, out var id) && !Guid.TryParseExact(userId, "N", out id))
        {
            return true;
        }

        var user = _userManager.GetUserById(id);
        return user is null || user.HasPermission(PermissionKind.EnableLiveTvAccess);
    }

    /// <inheritdoc />
    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        var channels = new List<ChannelInfo>();
        foreach (var host in _tunerHostManager.TunerHosts)
        {
            try
            {
                var hostChannels = await host.GetChannels(true, cancellationToken).ConfigureAwait(false);
                channels.AddRange(hostChannels);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error loading Live TV channels from {Host}", host.Name);
            }
        }

        if (channels.Count > 0)
        {
            try
            {
                await _listingsManager.AddProviderMetadata(channels, true, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not attach XMLTV logos to Live TV channel tiles");
            }
        }

        var guide = LoadNowNext();
        var items = LiveTvLibraryChannelItems.Build(channels, query.FolderId, guide);
        return new ChannelItemResult
        {
            Items = items,
            TotalRecordCount = items.Count
        };
    }

    private IReadOnlyDictionary<string, LiveTvNowNext> LoadNowNext()
    {
        try
        {
            var now = DateTime.UtcNow;
            var liveTvChannels = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.LiveTvChannel],
                Recursive = true,
                DtoOptions = new DtoOptions(false)
            });

            var programs = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.LiveTvProgram],
                Recursive = true,
                MinEndDate = now,
                MaxStartDate = now.AddHours(8),
                DtoOptions = new DtoOptions(true)
            }).OfType<LiveTvProgram>();

            return LiveTvNowNextMap.Create(liveTvChannels, programs, now);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not load Live TV now/next for channel tiles");
            return new Dictionary<string, LiveTvNowNext>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <inheritdoc />
    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id) || id.StartsWith(LiveTvLibraryChannelItems.GroupPrefix, StringComparison.Ordinal))
        {
            return [];
        }

        foreach (var host in _tunerHostManager.TunerHosts)
        {
            try
            {
                var sources = await host.GetChannelStreamMediaSources(id, cancellationToken).ConfigureAwait(false);
                if (sources.Count > 0)
                {
                    return LiveTvLibraryChannelPlayback.PrepareMediaSources(sources, id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "No media sources for Live TV channel {Id} from {Host}", id, host.Name);
            }
        }

        return [];
    }

    /// <inheritdoc />
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
    {
        return Task.FromResult(new DynamicImageResponse { HasImage = false });
    }

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedChannelImages()
    {
        return [ImageType.Primary];
    }
}
