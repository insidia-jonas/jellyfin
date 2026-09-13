using System;

namespace Jellyfin.Plugin.TreasureMaps;

/// <summary>
/// One Treasure-Maps grab remembered across restarts so Downloads can show the movie title
/// and poster, and can hide SABnzbd jobs that were not started from this plugin.
/// </summary>
public sealed class GrabRecord
{
    /// <summary>Gets or sets the SABnzbd job id.</summary>
    public string? NzoId { get; set; }

    /// <summary>Gets or sets the normalized SAB job / scene name key.</summary>
    public string? NameKey { get; set; }

    /// <summary>Gets or sets the human movie/show title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the cover URL or local image path.</summary>
    public string? CoverUrl { get; set; }

    /// <summary>Gets or sets <c>movie</c> or <c>tv</c>.</summary>
    public string? Kind { get; set; }

    /// <summary>Gets or sets the indexer release guid.</summary>
    public string? Guid { get; set; }

    /// <summary>Gets or sets the quality badge (resolution, source, size).</summary>
    public string? Quality { get; set; }

    /// <summary>Gets or sets when the grab was queued.</summary>
    public DateTime GrabbedAt { get; set; }
}
