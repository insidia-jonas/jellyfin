using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TreasureMaps.Api;

/// <summary>
/// Top-level response for a release list endpoint (<c>/movie</c>, <c>/search</c>, <c>/trending</c>).
/// </summary>
public class ReleaseListResponse
{
    /// <summary>Gets or sets the returned releases.</summary>
    [JsonPropertyName("items")]
    public IReadOnlyList<Release> Items { get; set; } = new List<Release>();

    /// <summary>Gets or sets the pagination block.</summary>
    [JsonPropertyName("pagination")]
    public Pagination? Pagination { get; set; }
}

/// <summary>Pagination metadata.</summary>
public class Pagination
{
    /// <summary>Gets or sets the page limit.</summary>
    [JsonPropertyName("limit")]
    public int Limit { get; set; }

    /// <summary>Gets or sets the page offset.</summary>
    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    /// <summary>Gets or sets the total number of matches.</summary>
    [JsonPropertyName("total")]
    public int Total { get; set; }
}

/// <summary>A single indexer release.</summary>
public class Release
{
    /// <summary>Gets or sets the release GUID.</summary>
    [JsonPropertyName("guid")]
    public string Guid { get; set; } = string.Empty;

    /// <summary>Gets or sets the raw release title.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the size in bytes.</summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>Gets or sets the number of grabs.</summary>
    [JsonPropertyName("grabs")]
    public int Grabs { get; set; }

    /// <summary>Gets or sets the audio languages present in the release.</summary>
    [JsonPropertyName("audio_languages")]
    public IReadOnlyList<string>? AudioLanguages { get; set; }

    /// <summary>Gets or sets the subtitle languages available in the release.</summary>
    [JsonPropertyName("subtitles")]
    public IReadOnlyList<string>? Subtitles { get; set; }

    /// <summary>Gets or sets the external identifiers.</summary>
    [JsonPropertyName("ids")]
    public ReleaseIds? Ids { get; set; }

    /// <summary>Gets or sets the image URLs.</summary>
    [JsonPropertyName("images")]
    public ReleaseImages? Images { get; set; }

    /// <summary>Gets or sets the video technical details.</summary>
    [JsonPropertyName("video")]
    public ReleaseVideo? Video { get; set; }

    /// <summary>Gets or sets the movie metadata.</summary>
    [JsonPropertyName("movie")]
    public ReleaseMovie? Movie { get; set; }

    /// <summary>Gets or sets the TV metadata.</summary>
    [JsonPropertyName("tv")]
    public ReleaseTv? Tv { get; set; }

    /// <summary>Gets or sets the related links.</summary>
    [JsonPropertyName("links")]
    public ReleaseLinks? Links { get; set; }

    /// <summary>Gets or sets the indexer category (used to infer movie vs TV for raw feeds).</summary>
    [JsonPropertyName("category")]
    public ReleaseCategory? Category { get; set; }
}

/// <summary>The indexer category of a release.</summary>
public class ReleaseCategory
{
    /// <summary>Gets or sets the category id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the category name (e.g. "Movies - DE &gt; HD").</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

/// <summary>TV metadata for a release.</summary>
public class ReleaseTv
{
    /// <summary>Gets or sets the series title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>Gets or sets the IMDb id.</summary>
    [JsonPropertyName("imdb")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Imdb { get; set; }

    /// <summary>Gets or sets the TMDB id.</summary>
    [JsonPropertyName("tmdb")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Tmdb { get; set; }

    /// <summary>Gets or sets the first-aired date (used for the year).</summary>
    [JsonPropertyName("first_aired")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? FirstAired { get; set; }

    /// <summary>Gets or sets the series overview/description.</summary>
    [JsonPropertyName("overview")]
    public string? Overview { get; set; }
}

/// <summary>External identifiers for a release.</summary>
public class ReleaseIds
{
    /// <summary>Gets or sets the IMDb id.</summary>
    [JsonPropertyName("imdb")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Imdb { get; set; }

    /// <summary>Gets or sets the TMDB id.</summary>
    [JsonPropertyName("tmdb")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Tmdb { get; set; }
}

/// <summary>Image URLs for a release.</summary>
public class ReleaseImages
{
    /// <summary>Gets or sets the cover/poster URL.</summary>
    [JsonPropertyName("cover")]
    public string? Cover { get; set; }

    /// <summary>Gets or sets the backdrop URL.</summary>
    [JsonPropertyName("backdrop")]
    public string? Backdrop { get; set; }
}

/// <summary>Video technical details for a release.</summary>
public class ReleaseVideo
{
    /// <summary>Gets or sets the video codec.</summary>
    [JsonPropertyName("codec")]
    public string? Codec { get; set; }

    /// <summary>Gets or sets the resolution (e.g. 1080p).</summary>
    [JsonPropertyName("resolution")]
    public string? Resolution { get; set; }
}

/// <summary>Movie metadata for a release.</summary>
public class ReleaseMovie
{
    /// <summary>Gets or sets the movie title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>Gets or sets the tagline.</summary>
    [JsonPropertyName("tagline")]
    public string? Tagline { get; set; }

    /// <summary>Gets or sets the plot.</summary>
    [JsonPropertyName("plot")]
    public string? Plot { get; set; }

    /// <summary>Gets or sets the rating (may be a string or a number).</summary>
    [JsonPropertyName("rating")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Rating { get; set; }

    /// <summary>Gets or sets the genres (accepts a comma-separated string or an array).</summary>
    [JsonPropertyName("genres")]
    [JsonConverter(typeof(StringOrArrayConverter))]
    public List<string>? Genres { get; set; }

    /// <summary>Gets or sets the release year (may be a string or a number).</summary>
    [JsonPropertyName("year")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Year { get; set; }

    /// <summary>Gets or sets the director.</summary>
    [JsonPropertyName("director")]
    public string? Director { get; set; }

    /// <summary>Gets or sets the actors (accepts a comma-separated string or an array).</summary>
    [JsonPropertyName("actors")]
    [JsonConverter(typeof(StringOrArrayConverter))]
    public List<string>? Actors { get; set; }
}

/// <summary>Related links for a release.</summary>
public class ReleaseLinks
{
    /// <summary>Gets or sets the details page URL.</summary>
    [JsonPropertyName("details")]
    public string? Details { get; set; }

    /// <summary>Gets or sets the NZB download URL.</summary>
    [JsonPropertyName("download")]
    public string? Download { get; set; }
}

/// <summary>Response for the <c>/caps</c> endpoint (subset used by the plugin).</summary>
public class CapsResponse
{
    /// <summary>Gets or sets the available genres.</summary>
    [JsonPropertyName("genres")]
    public IReadOnlyList<CapsNamedItem> Genres { get; set; } = new List<CapsNamedItem>();

    /// <summary>Gets or sets the available categories.</summary>
    [JsonPropertyName("categories")]
    public IReadOnlyList<CapsNamedItem> Categories { get; set; } = new List<CapsNamedItem>();
}

/// <summary>A named capability entry (genre or category).</summary>
public class CapsNamedItem
{
    /// <summary>Gets or sets the id.</summary>
    [JsonPropertyName("id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Id { get; set; }

    /// <summary>Gets or sets the display name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

/// <summary>Wrapper for the <c>/user</c> endpoint (<c>{ "user": { ... } }</c>).</summary>
public class UserInfoResponse
{
    /// <summary>Gets or sets the user info.</summary>
    [JsonPropertyName("user")]
    public UserInfo? User { get; set; }
}

/// <summary>User info from the <c>/user</c> endpoint (subset used for connection testing).</summary>
public class UserInfo
{
    /// <summary>Gets or sets the username.</summary>
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    /// <summary>Gets or sets the account role id.</summary>
    [JsonPropertyName("role_id")]
    public int RoleId { get; set; }

    /// <summary>Gets or sets the total number of grabs.</summary>
    [JsonPropertyName("grabs")]
    public int Grabs { get; set; }
}
