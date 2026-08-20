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
    /// Gets or sets the SABnzbd category to assign to grabbed downloads (optional, e.g. <c>movies</c>).
    /// </summary>
    public string SabnzbdCategory { get; set; } = string.Empty;
}
