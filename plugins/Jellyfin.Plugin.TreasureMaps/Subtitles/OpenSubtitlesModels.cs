using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TreasureMaps.Subtitles;

/// <summary>Login response from <c>POST /login</c>.</summary>
public class OsLoginResponse
{
    /// <summary>Gets or sets the bearer token used for downloads.</summary>
    [JsonPropertyName("token")]
    public string? Token { get; set; }

    /// <summary>Gets or sets the account's preferred API base URL.</summary>
    [JsonPropertyName("base_url")]
    public string? BaseUrl { get; set; }
}

/// <summary>Search response from <c>GET /subtitles</c>.</summary>
public class OsSearchResponse
{
    /// <summary>Gets or sets the result rows.</summary>
    [JsonPropertyName("data")]
    public IReadOnlyList<OsSubtitle> Data { get; set; } = new List<OsSubtitle>();
}

/// <summary>A single subtitle result.</summary>
public class OsSubtitle
{
    /// <summary>Gets or sets the subtitle id.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>Gets or sets the attributes.</summary>
    [JsonPropertyName("attributes")]
    public OsSubtitleAttributes? Attributes { get; set; }
}

/// <summary>Subtitle attributes.</summary>
public class OsSubtitleAttributes
{
    /// <summary>Gets or sets the language (2-letter code).</summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>Gets or sets the download count.</summary>
    [JsonPropertyName("download_count")]
    public int DownloadCount { get; set; }

    /// <summary>Gets or sets the community rating.</summary>
    [JsonPropertyName("ratings")]
    public double Ratings { get; set; }

    /// <summary>Gets or sets a value indicating whether the subtitle is hearing-impaired.</summary>
    [JsonPropertyName("hearing_impaired")]
    public bool HearingImpaired { get; set; }

    /// <summary>Gets or sets a value indicating whether the subtitle is AI-translated.</summary>
    [JsonPropertyName("ai_translated")]
    public bool AiTranslated { get; set; }

    /// <summary>Gets or sets a value indicating whether the subtitle is machine-translated.</summary>
    [JsonPropertyName("machine_translated")]
    public bool MachineTranslated { get; set; }

    /// <summary>Gets or sets a value indicating whether the subtitle is for foreign parts only (forced).</summary>
    [JsonPropertyName("foreign_parts_only")]
    public bool ForeignPartsOnly { get; set; }

    /// <summary>Gets or sets a value indicating whether this result matched by movie hash.</summary>
    [JsonPropertyName("moviehash_match")]
    public bool MoviehashMatch { get; set; }

    /// <summary>Gets or sets the uploader/release name.</summary>
    [JsonPropertyName("release")]
    public string? Release { get; set; }

    /// <summary>Gets or sets the upload date.</summary>
    [JsonPropertyName("upload_date")]
    public string? UploadDate { get; set; }

    /// <summary>Gets or sets the files (the first file's id is used for download).</summary>
    [JsonPropertyName("files")]
    public IReadOnlyList<OsSubtitleFile> Files { get; set; } = new List<OsSubtitleFile>();
}

/// <summary>A downloadable subtitle file entry.</summary>
public class OsSubtitleFile
{
    /// <summary>Gets or sets the file id used for the download request.</summary>
    [JsonPropertyName("file_id")]
    public int FileId { get; set; }

    /// <summary>Gets or sets the file name.</summary>
    [JsonPropertyName("file_name")]
    public string? FileName { get; set; }
}

/// <summary>Download response from <c>POST /download</c>.</summary>
public class OsDownloadResponse
{
    /// <summary>Gets or sets the direct link to fetch the subtitle content.</summary>
    [JsonPropertyName("link")]
    public string? Link { get; set; }

    /// <summary>Gets or sets the file name.</summary>
    [JsonPropertyName("file_name")]
    public string? FileName { get; set; }

    /// <summary>Gets or sets the remaining daily downloads.</summary>
    [JsonPropertyName("remaining")]
    public int Remaining { get; set; }
}
