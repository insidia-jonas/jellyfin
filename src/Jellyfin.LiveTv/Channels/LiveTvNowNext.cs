using System;

namespace Jellyfin.LiveTv.Channels;

/// <summary>
/// Current and following guide entries for one tuner channel.
/// </summary>
internal sealed class LiveTvNowNext
{
    /// <summary>Gets the title on air now.</summary>
    public string? NowTitle { get; init; }

    /// <summary>Gets the UTC start of the current program.</summary>
    public DateTime? NowStart { get; init; }

    /// <summary>Gets the UTC end of the current program.</summary>
    public DateTime? NowEnd { get; init; }

    /// <summary>Gets the plot of the current program.</summary>
    public string? NowOverview { get; init; }

    /// <summary>Gets artwork for the current program.</summary>
    public string? NowImageUrl { get; init; }

    /// <summary>Gets the next program title.</summary>
    public string? NextTitle { get; init; }

    /// <summary>Gets the UTC start of the next program.</summary>
    public DateTime? NextStart { get; init; }

    /// <summary>Gets the UTC end of the next program.</summary>
    public DateTime? NextEnd { get; init; }
}
