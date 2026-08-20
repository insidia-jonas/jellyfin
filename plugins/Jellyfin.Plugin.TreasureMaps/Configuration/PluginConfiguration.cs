using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TreasureMaps.Configuration;

/// <summary>
/// Configuration for the Treasure-Maps plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the base URL of the Treasure-Maps API (for example <c>https://treasure-maps.example</c>).
    /// The <c>/api/v1</c> prefix is appended automatically.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the API key sent as the <c>X-API-Key</c> header.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the maximum number of releases fetched per browse request.
    /// </summary>
    public int ResultLimit { get; set; } = 60;

    /// <summary>
    /// Gets or sets the minimum community rating used when browsing (0 disables the filter).
    /// </summary>
    public double MinRating { get; set; }

    /// <summary>
    /// Gets or sets the preferred (primary) language. Releases in this language are shown first.
    /// Accepts an ISO code (<c>de</c>, <c>en</c>, <c>es</c>) or a language name (<c>German</c>).
    /// </summary>
    public string PrimaryLanguage { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the accepted secondary languages, in order of preference. Same format as
    /// <see cref="PrimaryLanguage"/>.
    /// </summary>
    public string[] SecondaryLanguages { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether releases that do not match the primary or any
    /// secondary language are hidden. Releases without language information are always kept.
    /// </summary>
    public bool FilterByLanguage { get; set; }

    /// <summary>
    /// Gets or sets an optional local folder into which grabbed NZB files are written.
    /// Used as a fallback when SABnzbd is not configured. Point a Usenet download client
    /// (SABnzbd / NZBGet "watched folder") at this path to actually download the movie.
    /// </summary>
    public string NzbDropFolder { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the base URL of the SABnzbd instance (for example <c>http://localhost:8080</c>).
    /// When set together with <see cref="SabnzbdApiKey"/>, grabbed releases are pushed straight
    /// into the SABnzbd download queue instead of only being written to <see cref="NzbDropFolder"/>.
    /// </summary>
    public string SabnzbdUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the SABnzbd API key (SABnzbd → Config → General → API Key).
    /// </summary>
    public string SabnzbdApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the default/fallback SABnzbd category (used when a per-type category is not set).
    /// </summary>
    public string SabnzbdCategory { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the SABnzbd category for movie grabs (e.g. <c>movies</c>). SABnzbd routes this
    /// category to its own completed folder, which you point the Jellyfin "Movies" library at.
    /// </summary>
    public string SabnzbdMovieCategory { get; set; } = "movies";

    /// <summary>
    /// Gets or sets the SABnzbd category for TV grabs (e.g. <c>tv</c>). SABnzbd routes this category
    /// to its own completed folder, which you point the Jellyfin "Shows" library at.
    /// </summary>
    public string SabnzbdTvCategory { get; set; } = "tv";

    /// <summary>
    /// Gets or sets the download folder for the movie category, applied to SABnzbd by the
    /// "Set up SABnzbd" action. Relative paths are resolved under SABnzbd's completed-downloads
    /// folder; absolute paths are used as-is.
    /// </summary>
    public string SabnzbdMovieFolder { get; set; } = "movies";

    /// <summary>
    /// Gets or sets the download folder for the TV category, applied to SABnzbd by the
    /// "Set up SABnzbd" action.
    /// </summary>
    public string SabnzbdTvFolder { get; set; } = "tv";

    /// <summary>
    /// Gets or sets a value indicating whether releases are enriched with xREL ratings
    /// (looked up by release/scene name).
    /// </summary>
    public bool EnableXrel { get; set; }

    /// <summary>
    /// Gets or sets the xREL API base URL. Defaults to the public xREL v2 API.
    /// </summary>
    public string XrelBaseUrl { get; set; } = "https://api.xrel.to/v2";

    /// <summary>
    /// Gets or sets a value indicating whether the OpenSubtitles subtitle provider is enabled.
    /// </summary>
    public bool EnableOpenSubtitles { get; set; }

    /// <summary>
    /// Gets or sets the OpenSubtitles API key (from your opensubtitles.com consumer/app).
    /// </summary>
    public string OpenSubtitlesApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the OpenSubtitles username (required to download subtitles).
    /// </summary>
    public string OpenSubtitlesUsername { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the OpenSubtitles password (required to download subtitles).
    /// </summary>
    public string OpenSubtitlesPassword { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the OpenSubtitles API base URL. Defaults to the public REST API.
    /// </summary>
    public string OpenSubtitlesBaseUrl { get; set; } = "https://api.opensubtitles.com/api/v1";
}
