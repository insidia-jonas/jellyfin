using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Plugin.TreasureMaps.ReleaseNaming;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Branding;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TreasureMaps.Api;

/// <summary>
/// API controller for the Treasure-Maps plugin (connection test and NZB grab).
/// </summary>
[ApiController]
[Authorize]
[Route("TreasureMaps")]
[Produces(MediaTypeNames.Application.Json)]
[ApiExplorerSettings(IgnoreApi = true)]
public class TreasureMapsController : ControllerBase
{
    private readonly TreasureMapsApiClient _client;
    private readonly SabnzbdClient _sabnzbd;
    private readonly Subtitles.OpenSubtitlesClient _openSubtitles;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserViewManager _userViewManager;
    private readonly IServerConfigurationManager _configurationManager;
    private readonly ILogger<TreasureMapsController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TreasureMapsController"/> class.
    /// </summary>
    /// <param name="client">The Treasure-Maps API client.</param>
    /// <param name="sabnzbd">The SABnzbd client.</param>
    /// <param name="openSubtitles">The OpenSubtitles client.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="userViewManager">The user view manager.</param>
    /// <param name="configurationManager">The server configuration manager.</param>
    /// <param name="logger">The logger.</param>
    public TreasureMapsController(
        TreasureMapsApiClient client,
        SabnzbdClient sabnzbd,
        Subtitles.OpenSubtitlesClient openSubtitles,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserViewManager userViewManager,
        IServerConfigurationManager configurationManager,
        ILogger<TreasureMapsController> logger)
    {
        _client = client;
        _sabnzbd = sabnzbd;
        _openSubtitles = openSubtitles;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userViewManager = userViewManager;
        _configurationManager = configurationManager;
        _logger = logger;
    }

    /// <summary>
    /// Validates the OpenSubtitles configuration (API key + optional login).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The connection status.</returns>
    [HttpGet("OpenSubtitles/Test")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> TestOpenSubtitles(CancellationToken cancellationToken)
    {
        if (!Subtitles.OpenSubtitlesClient.IsEnabled)
        {
            return Ok(new { ok = false, message = "Enable OpenSubtitles and set an API key first." });
        }

        try
        {
            var token = await _openSubtitles.GetTokenAsync(cancellationToken).ConfigureAwait(false);
            var probe = await _openSubtitles.SearchAsync(
                new Dictionary<string, string?> { ["languages"] = "en", ["query"] = "matrix", ["type"] = "movie" },
                cancellationToken).ConfigureAwait(false);
            var count = probe?.Data?.Count ?? 0;
            return Ok(new { ok = true, loggedIn = !string.IsNullOrEmpty(token), sampleResults = count });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenSubtitles connection test failed");
            return Ok(new { ok = false, message = ex.Message });
        }
    }

    /// <summary>
    /// Validates the configured Base URL and API key by calling the provider's user endpoint.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The connection status.</returns>
    [HttpGet("Test")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Test(CancellationToken cancellationToken)
    {
        if (!TreasureMapsApiClient.IsConfigured)
        {
            return Ok(new { ok = false, message = "Base URL and API key must be configured first." });
        }

        try
        {
            var user = await _client.GetUserAsync(cancellationToken).ConfigureAwait(false);
            return Ok(new { ok = true, username = user?.Username, grabs = user?.Grabs });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Treasure-Maps connection test failed");
            return Ok(new { ok = false, message = ex.Message });
        }
    }

    /// <summary>
    /// Validates the configured SABnzbd connection.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The SABnzbd connection status.</returns>
    [HttpGet("Sabnzbd/Test")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> TestSabnzbd(CancellationToken cancellationToken)
    {
        if (!SabnzbdClient.IsConfigured)
        {
            return Ok(new { ok = false, message = "SABnzbd URL and API key must be configured first." });
        }

        try
        {
            var version = await _sabnzbd.GetVersionAsync(cancellationToken).ConfigureAwait(false);
            return Ok(new { ok = true, version });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SABnzbd connection test failed");
            return Ok(new { ok = false, message = ex.Message });
        }
    }

    /// <summary>
    /// Creates/updates the movie and TV categories (with their folders) in SABnzbd, so the plugin
    /// manages the SABnzbd configuration for the user.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result of the setup.</returns>
    [HttpPost("Sabnzbd/Setup")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> SetupSabnzbd(CancellationToken cancellationToken)
    {
        if (!SabnzbdClient.IsConfigured)
        {
            return Ok(new { ok = false, message = "Configure the SABnzbd URL and API key first." });
        }

        var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
        var applied = new List<object>();
        try
        {
            if (!string.IsNullOrWhiteSpace(config.SabnzbdMovieCategory))
            {
                await _sabnzbd.SetCategoryAsync(config.SabnzbdMovieCategory, config.SabnzbdMovieFolder, cancellationToken).ConfigureAwait(false);
                applied.Add(new { category = config.SabnzbdMovieCategory, dir = config.SabnzbdMovieFolder });
            }

            if (!string.IsNullOrWhiteSpace(config.SabnzbdTvCategory))
            {
                await _sabnzbd.SetCategoryAsync(config.SabnzbdTvCategory, config.SabnzbdTvFolder, cancellationToken).ConfigureAwait(false);
                applied.Add(new { category = config.SabnzbdTvCategory, dir = config.SabnzbdTvFolder });
            }

            var categories = await _sabnzbd.GetCategoryNamesAsync(cancellationToken).ConfigureAwait(false);
            return Ok(new { ok = true, applied, categories });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SABnzbd setup failed");
            return Ok(new { ok = false, message = ex.Message });
        }
    }

    /// <summary>
    /// Creates (or completes) the Jellyfin media libraries for the download folders, so grabbed
    /// movies and series show up in the top menu under "Movies" / "TV Shows" once SABnzbd has
    /// finished downloading them.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result of the setup.</returns>
    [HttpPost("Libraries/Setup")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> SetupLibraries(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

        // Category folders may be relative to SABnzbd's completed-downloads directory.
        var completeDir = SabnzbdClient.IsConfigured
            ? await _sabnzbd.GetCompleteDirAsync(cancellationToken).ConfigureAwait(false)
            : null;

        var moviesPath = ResolveDownloadFolder(config.SabnzbdMovieFolder, config.SabnzbdMovieCategory, completeDir);
        var tvPath = ResolveDownloadFolder(config.SabnzbdTvFolder, config.SabnzbdTvCategory, completeDir);
        if (moviesPath is null && tvPath is null)
        {
            return Ok(new { ok = false, message = "Configure the SABnzbd movie/TV folders (absolute paths) or connect SABnzbd first." });
        }

        var results = new List<object>();
        try
        {
            if (moviesPath is not null)
            {
                var status = await EnsureLibraryAsync("Movies", CollectionTypeOptions.movies, moviesPath).ConfigureAwait(false);
                results.Add(new { library = "Movies", path = moviesPath, status });
            }

            if (tvPath is not null)
            {
                var status = await EnsureLibraryAsync("TV Shows", CollectionTypeOptions.tvshows, tvPath).ConfigureAwait(false);
                results.Add(new { library = "TV Shows", path = tvPath, status });
            }

            return Ok(new { ok = true, libraries = results });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Library setup failed");
            return Ok(new { ok = false, message = ex.Message, libraries = results });
        }
    }

    private static string? ResolveDownloadFolder(string? folder, string? category, string? completeDir)
    {
        var value = !string.IsNullOrWhiteSpace(folder) ? folder : category;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Path.IsPathRooted(value))
        {
            return value;
        }

        return string.IsNullOrWhiteSpace(completeDir) ? null : Path.Combine(completeDir, value);
    }

    private async Task<string> EnsureLibraryAsync(string name, CollectionTypeOptions collectionType, string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create library folder {Path}", path);
        }

        var existing = _libraryManager.GetVirtualFolders()
            .FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            var options = new LibraryOptions
            {
                PathInfos = new[] { new MediaPathInfo(path) },
                EnableRealtimeMonitor = true
            };
            await _libraryManager.AddVirtualFolder(name, collectionType, options, true).ConfigureAwait(false);
            _logger.LogInformation("Created Jellyfin library '{Name}' -> {Path}", name, path);
            return "created";
        }

        if (existing.Locations?.Any(l => string.Equals(l, path, StringComparison.OrdinalIgnoreCase)) == true)
        {
            return "already configured";
        }

        _libraryManager.AddMediaPath(name, new MediaPathInfo(path));
        _logger.LogInformation("Added {Path} to existing Jellyfin library '{Name}'", path, name);
        return "path added";
    }

    /// <summary>
    /// Moves the Treasure-Maps channel to the end of the top menu (after Movies, TV Shows, ...)
    /// for every user, by updating each user's ordered-views preference.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The users that were updated.</returns>
    [HttpPost("Menu/MoveChannelLast")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> MoveChannelLast(CancellationToken cancellationToken)
    {
        var channelName = Plugin.Instance?.Name ?? "Treasure-Maps";
        var updated = new List<string>();
        try
        {
            foreach (var user in _userManager.GetUsers().ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var views = _userViewManager.GetUserViews(new UserViewQuery { User = user, IncludeExternalContent = true });
                var channelViews = views
                    .Where(v => v is Channel && string.Equals(v.Name, channelName, StringComparison.OrdinalIgnoreCase))
                    .Select(v => v.Id)
                    .ToList();
                if (channelViews.Count == 0)
                {
                    continue;
                }

                var ordered = views.Select(v => v.Id)
                    .Where(id => !channelViews.Contains(id))
                    .Concat(channelViews)
                    .ToArray();

                var config = BuildUserConfiguration(user);
                config.OrderedViews = ordered;
                await _userManager.UpdateConfigurationAsync(user.Id, config).ConfigureAwait(false);
                updated.Add(user.Username);
            }

            return Ok(new { ok = true, users = updated });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reorder the menu");
            return Ok(new { ok = false, message = ex.Message, users = updated });
        }
    }

    // UpdateConfigurationAsync overwrites the WHOLE configuration, so every current value has to
    // be carried over (mirrors UserManager.GetUserDto's mapping) before changing OrderedViews.
    private static UserConfiguration BuildUserConfiguration(Jellyfin.Database.Implementations.Entities.User user)
    {
        return new UserConfiguration
        {
            SubtitleMode = user.SubtitleMode,
            HidePlayedInLatest = user.HidePlayedInLatest,
            EnableLocalPassword = user.EnableLocalPassword,
            PlayDefaultAudioTrack = user.PlayDefaultAudioTrack,
            DisplayCollectionsView = user.DisplayCollectionsView,
            DisplayMissingEpisodes = user.DisplayMissingEpisodes,
            AudioLanguagePreference = user.AudioLanguagePreference,
            RememberAudioSelections = user.RememberAudioSelections,
            EnableNextEpisodeAutoPlay = user.EnableNextEpisodeAutoPlay,
            RememberSubtitleSelections = user.RememberSubtitleSelections,
            SubtitleLanguagePreference = user.SubtitleLanguagePreference ?? string.Empty,
            OrderedViews = user.GetPreferenceValues<Guid>(Jellyfin.Database.Implementations.Enums.PreferenceKind.OrderedViews),
            GroupedFolders = user.GetPreferenceValues<Guid>(Jellyfin.Database.Implementations.Enums.PreferenceKind.GroupedFolders),
            MyMediaExcludes = user.GetPreferenceValues<Guid>(Jellyfin.Database.Implementations.Enums.PreferenceKind.MyMediaExcludes),
            LatestItemsExcludes = user.GetPreferenceValues<Guid>(Jellyfin.Database.Implementations.Enums.PreferenceKind.LatestItemExcludes),
            CastReceiverId = user.CastReceiverId
        };
    }

    /// <summary>
    /// Validates the AI provider configuration with a tiny prompt.
    /// </summary>
    /// <param name="ai">The AI recommender.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The connection status.</returns>
    [HttpGet("Ai/Test")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> TestAi([FromServices] Recommendations.AiRecommender ai, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
        if (string.IsNullOrWhiteSpace(config.AiApiKey))
        {
            return Ok(new { ok = false, message = "Set an AI API key first." });
        }

        try
        {
            var reply = await ai.CompleteAsync("Reply with exactly: OK", cancellationToken).ConfigureAwait(false);
            return Ok(new { ok = true, provider = config.AiProvider, reply = reply.Length <= 80 ? reply : reply[..80] });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI connection test failed");
            return Ok(new { ok = false, message = ex.Message });
        }
    }

    /// <summary>
    /// Serves the client-side enhancement script (release list with download buttons and live
    /// SABnzbd status). Anonymous because it is referenced from index.html before login; it
    /// contains no secrets, and every API call it makes runs with the logged-in user's token.
    /// </summary>
    /// <returns>The JavaScript file.</returns>
    [HttpGet("ClientScript")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult ClientScript()
    {
        var stream = GetType().Assembly.GetManifestResourceStream("Jellyfin.Plugin.TreasureMaps.Web.treasuremaps.js");
        if (stream is null)
        {
            return NotFound();
        }

        return File(stream, "application/javascript");
    }

    /// <summary>
    /// Gets the current SABnzbd download status (active queue with progress/speed/ETA plus the
    /// most recent completed/failed jobs), used by the client script for live status display.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The download status.</returns>
    [HttpGet("Downloads/Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> DownloadStatus(CancellationToken cancellationToken)
    {
        if (!SabnzbdClient.IsConfigured)
        {
            return Ok(new { ok = false, message = "SABnzbd is not configured.", items = Array.Empty<object>() });
        }

        try
        {
            var (speed, items) = await _sabnzbd.GetDownloadStatusAsync(cancellationToken).ConfigureAwait(false);
            return Ok(new
            {
                ok = true,
                speed,
                items = items.Select(i => new
                {
                    id = i.Id,
                    name = i.Name,
                    status = i.Status,
                    percent = i.Percent,
                    timeLeft = i.TimeLeft,
                    sizeMb = i.SizeMb,
                    leftMb = i.LeftMb,
                    failMessage = i.FailMessage
                })
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read the SABnzbd download status");
            return Ok(new { ok = false, message = ex.Message, items = Array.Empty<object>() });
        }
    }

    /// <summary>
    /// Queues a metadata+image refresh for every person that has no primary image yet, so actor
    /// photos (from TMDB, matched by name) appear on the details pages.
    /// </summary>
    /// <param name="providerManager">The provider manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <returns>The number of queued refreshes.</returns>
    [HttpPost("People/RefreshImages")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult RefreshPeopleImages(
        [FromServices] MediaBrowser.Controller.Providers.IProviderManager providerManager,
        [FromServices] MediaBrowser.Model.IO.IFileSystem fileSystem)
    {
        try
        {
            var queued = PeopleImageService.QueueMissingPeopleImages(_libraryManager, providerManager, fileSystem, _logger, 500);
            return Ok(new { ok = true, queued });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to queue people image refreshes");
            return Ok(new { ok = false, message = ex.Message });
        }
    }

    private const string ThemeBegin = "/* TREASURE-GLASS-BEGIN */";
    private const string ThemeEnd = "/* TREASURE-GLASS-END */";

    /// <summary>
    /// Applies the bundled "Treasure Glass" (macOS-like glassmorphism) theme to the server's
    /// branding Custom CSS, so every web client gets the modern glass look. Existing custom CSS
    /// outside the theme's marker block is preserved.
    /// </summary>
    /// <returns>The result of the operation.</returns>
    [HttpPost("Theme/Apply")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult ApplyTheme()
    {
        try
        {
            using var stream = GetType().Assembly.GetManifestResourceStream("Jellyfin.Plugin.TreasureMaps.Theme.glass.css");
            if (stream is null)
            {
                return Ok(new { ok = false, message = "Bundled theme resource not found." });
            }

            using var reader = new StreamReader(stream);
            var css = reader.ReadToEnd();

            var branding = _configurationManager.GetConfiguration<BrandingOptions>("branding");
            var existing = StripThemeBlock(branding.CustomCss);
            branding.CustomCss = (string.IsNullOrWhiteSpace(existing) ? string.Empty : existing.TrimEnd() + "\n\n")
                + ThemeBegin + "\n" + css + "\n" + ThemeEnd;
            _configurationManager.SaveConfiguration("branding", branding);

            _logger.LogInformation("Applied the Treasure Glass theme to the branding custom CSS");
            return Ok(new { ok = true, bytes = css.Length });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply the glass theme");
            return Ok(new { ok = false, message = ex.Message });
        }
    }

    /// <summary>
    /// Removes the "Treasure Glass" theme from the branding Custom CSS (other custom CSS is kept).
    /// </summary>
    /// <returns>The result of the operation.</returns>
    [HttpPost("Theme/Remove")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult RemoveTheme()
    {
        try
        {
            var branding = _configurationManager.GetConfiguration<BrandingOptions>("branding");
            branding.CustomCss = StripThemeBlock(branding.CustomCss);
            _configurationManager.SaveConfiguration("branding", branding);
            return Ok(new { ok = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove the glass theme");
            return Ok(new { ok = false, message = ex.Message });
        }
    }

    private static string? StripThemeBlock(string? css)
    {
        if (string.IsNullOrEmpty(css))
        {
            return css;
        }

        var start = css.IndexOf(ThemeBegin, StringComparison.Ordinal);
        if (start < 0)
        {
            return css;
        }

        var end = css.IndexOf(ThemeEnd, start, StringComparison.Ordinal);
        if (end < 0)
        {
            return css[..start].TrimEnd();
        }

        return (css[..start] + css[(end + ThemeEnd.Length)..]).Trim();
    }

    /// <summary>
    /// Searches Treasure-Maps for the Browse &amp; Grab page.
    /// </summary>
    /// <param name="type">The media type to search: <c>movie</c> or <c>tv</c>.</param>
    /// <param name="q">The free-text query (optional).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A lightweight list of releases for the UI.</returns>
    [HttpGet("Search")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] string type, [FromQuery] string? q, [FromQuery] string? genre, CancellationToken cancellationToken)
    {
        if (!TreasureMapsApiClient.IsConfigured)
        {
            return Ok(new { ok = false, message = "Configure the Treasure-Maps connection first.", items = Array.Empty<object>() });
        }

        var kind = (type ?? "movie").ToLowerInvariant();
        var limit = Plugin.Instance?.Configuration.ResultLimit ?? 60;
        try
        {
            var response = kind switch
            {
                "trending" => await _client.GetTrendingAsync(limit, cancellationToken).ConfigureAwait(false),
                "tv" => await _client.SearchTvAsync(q, limit, cancellationToken).ConfigureAwait(false),
                _ => await _client.SearchMoviesAsync(q, genre, limit, cancellationToken).ConfigureAwait(false)
            };

            var items = (response?.Items ?? Enumerable.Empty<Release>())
                .Where(r => !string.IsNullOrWhiteSpace(r.Guid))
                .Select(r =>
                {
                    var parsed = ReleaseNameParser.Parse(r.Title);
                    var isTvItem = kind switch
                    {
                        "tv" => true,
                        "movie" => false,
                        _ => r.Tv is not null && r.Movie is null
                            || (r.Movie is null && (r.Category?.Name?.Contains("TV", StringComparison.OrdinalIgnoreCase) ?? false))
                    };
                    var title = r.Movie?.Title ?? r.Tv?.Title ?? r.Title;
                    var ratingStr = r.Movie?.Rating;
                    double? rating = double.TryParse(ratingStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var rv) ? rv : null;
                    var year = r.Movie?.Year ?? r.Tv?.FirstAired;
                    if (year is { Length: > 4 })
                    {
                        year = year[..4];
                    }

                    return new
                    {
                        guid = r.Guid,
                        title,
                        scene = r.Title,
                        year,
                        rating,
                        poster = r.Images?.Cover,
                        type = isTvItem ? "tv" : "movie",
                        genres = r.Movie?.Genres ?? new System.Collections.Generic.List<string>(),
                        quality = string.Join(" · ", parsed.DisplayTags)
                    };
                })
                .ToList();

            return Ok(new { ok = true, items });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Treasure-Maps search failed");
            return Ok(new { ok = false, message = ex.Message, items = Array.Empty<object>() });
        }
    }

    /// <summary>
    /// Returns the indexer's genres (for the Browse page genre selector).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The list of genre names.</returns>
    [HttpGet("Genres")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Genres(CancellationToken cancellationToken)
    {
        if (!TreasureMapsApiClient.IsConfigured)
        {
            return Ok(new { ok = false, genres = Array.Empty<string>() });
        }

        try
        {
            // The indexer exposes thousands of niche genres (incl. adult tags); only the curated
            // common-genre whitelist is surfaced.
            var caps = await _client.GetCapsAsync(cancellationToken).ConfigureAwait(false);
            var genres = Channels.CommonGenres.FilterAvailable(
                (caps?.Genres ?? Enumerable.Empty<Api.CapsNamedItem>()).Select(g => g.Name));

            return Ok(new { ok = true, genres });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Treasure-Maps caps failed");
            return Ok(new { ok = false, genres = Array.Empty<string>() });
        }
    }

    /// <summary>
    /// Grabs a release: downloads its NZB and pushes it to SABnzbd with the category that matches the
    /// media type (so movies and series land in their own folders), or writes it to the drop folder.
    /// </summary>
    /// <param name="guid">The release GUID.</param>
    /// <param name="type">The media type (<c>movie</c> or <c>tv</c>); auto-detected from the name when omitted.</param>
    /// <param name="name">An optional human-readable name for the SABnzbd job.</param>
    /// <param name="poster">An optional cover URL, shown on the Downloads folder tile.</param>
    /// <param name="grabService">The shared grab service (artwork registry).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result of the grab (SABnzbd job ids or the written file path).</returns>
    [HttpPost("Releases/{guid}/Grab")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Grab([FromRoute] string guid, [FromQuery] string? type, [FromQuery] string? name, [FromQuery] string? poster, [FromServices] GrabService grabService, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
        if (!SabnzbdClient.IsConfigured && string.IsNullOrWhiteSpace(config.NzbDropFolder))
        {
            return BadRequest(new { ok = false, message = "Configure SABnzbd or an NZB drop folder first." });
        }

        var isTv = ResolveIsTv(type, name);
        var category = PickCategory(config, isTv);

        try
        {
            var payload = await _client.DownloadNzbAsync(guid, cancellationToken).ConfigureAwait(false);
            var jobName = string.IsNullOrWhiteSpace(name) ? guid : name!;
            var safeName = Sanitize(jobName);

            if (SabnzbdClient.IsConfigured)
            {
                var nzoIds = await _sabnzbd.AddNzbAsync(payload, safeName, category, cancellationToken).ConfigureAwait(false);
                grabService.RegisterArtwork(nzoIds, safeName, poster);
                _logger.LogInformation("Grabbed {Guid} into SABnzbd category '{Category}' ({Ids})", guid, category, string.Join(",", nzoIds));
                return Ok(new { ok = true, target = "sabnzbd", category, mediaType = isTv ? "tv" : "movie", nzoIds, bytes = payload.Length });
            }

            Directory.CreateDirectory(config.NzbDropFolder);
            var path = Path.Combine(config.NzbDropFolder, safeName + ".nzb");
            await System.IO.File.WriteAllBytesAsync(path, payload, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Grabbed Treasure-Maps release {Guid} to {Path}", guid, path);
            return Ok(new { ok = true, target = "folder", path, bytes = payload.Length });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to grab Treasure-Maps release {Guid}", guid);
            return Ok(new { ok = false, message = ex.Message });
        }
    }

    private static bool ResolveIsTv(string? type, string? name)
    {
        if (string.Equals(type, "tv", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(type, "movie", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // No explicit type: infer from the scene name (SxxExx => series).
        return !string.IsNullOrWhiteSpace(name)
            && System.Text.RegularExpressions.Regex.IsMatch(name!, "S[0-9]{1,2}E[0-9]{1,3}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static string? PickCategory(Configuration.PluginConfiguration config, bool isTv)
    {
        var preferred = isTv ? config.SabnzbdTvCategory : config.SabnzbdMovieCategory;
        return !string.IsNullOrWhiteSpace(preferred) ? preferred : config.SabnzbdCategory;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
