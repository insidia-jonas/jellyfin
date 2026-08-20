using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TreasureMaps.Xrel;

/// <summary>
/// Normalized xREL rating for a release, used by the mapper and tests.
/// </summary>
/// <param name="VideoRating">Scene video-quality rating (0-10), if available.</param>
/// <param name="AudioRating">Scene audio-quality rating (0-10), if available.</param>
/// <param name="NumRatings">Number of ratings the release received.</param>
/// <param name="TitleRating">The linked title (ext_info) user rating (0-10), if available.</param>
/// <param name="GroupName">The release group, if available.</param>
public readonly record struct XrelRating(double? VideoRating, double? AudioRating, int NumRatings, double? TitleRating, string? GroupName)
{
    /// <summary>Gets a value indicating whether the rating carries any usable information.</summary>
    public bool HasAny => VideoRating.HasValue || AudioRating.HasValue || TitleRating.HasValue;
}

/// <summary>
/// The xREL <c>release</c> object returned by <c>/release/info.json</c> (subset).
/// </summary>
public class XrelRelease
{
    /// <summary>Gets or sets the release id.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>Gets or sets the scene directory name.</summary>
    [JsonPropertyName("dirname")]
    public string? Dirname { get; set; }

    /// <summary>Gets or sets the release group name.</summary>
    [JsonPropertyName("group_name")]
    public string? GroupName { get; set; }

    /// <summary>Gets or sets the number of ratings.</summary>
    [JsonPropertyName("num_ratings")]
    public int NumRatings { get; set; }

    /// <summary>Gets or sets the video-quality rating.</summary>
    [JsonPropertyName("video_rating")]
    public double? VideoRating { get; set; }

    /// <summary>Gets or sets the audio-quality rating.</summary>
    [JsonPropertyName("audio_rating")]
    public double? AudioRating { get; set; }

    /// <summary>Gets or sets the linked title info.</summary>
    [JsonPropertyName("ext_info")]
    public XrelExtInfo? ExtInfo { get; set; }

    /// <summary>Converts the release payload into a normalized <see cref="XrelRating"/>.</summary>
    /// <returns>The normalized rating.</returns>
    public XrelRating ToRating()
        => new XrelRating(VideoRating, AudioRating, NumRatings, ExtInfo?.Rating, GroupName);
}

/// <summary>
/// The xREL <c>release_ext_info</c> object (the linked movie/show).
/// </summary>
public class XrelExtInfo
{
    /// <summary>Gets or sets the title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>Gets or sets the title user rating (0-10).</summary>
    [JsonPropertyName("rating")]
    public double? Rating { get; set; }

    /// <summary>Gets or sets the number of title ratings.</summary>
    [JsonPropertyName("num_ratings")]
    public int NumRatings { get; set; }
}
