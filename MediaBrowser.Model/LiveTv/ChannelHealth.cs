using System;

namespace MediaBrowser.Model.LiveTv;

/// <summary>Recent observations of a channel, without provider URLs or credentials.</summary>
public sealed class ChannelHealth
{
    /// <summary>Gets or sets the status: Unknown, Healthy, Unstable or Unavailable.</summary>
    public string Status { get; set; } = "Unknown";

    /// <summary>Gets or sets a non-sensitive reason code.</summary>
    public string? Reason { get; set; }

    /// <summary>Gets or sets the last observation time.</summary>
    public DateTime? LastCheckedUtc { get; set; }

    /// <summary>Gets or sets the last verified media flow time.</summary>
    public DateTime? LastSuccessUtc { get; set; }

    /// <summary>Gets or sets the most recent playback request time.</summary>
    public DateTime? LastPlayedUtc { get; set; }

    /// <summary>Gets or sets milliseconds until the first media data arrived.</summary>
    public long? StartMilliseconds { get; set; }

    /// <summary>Gets or sets the bytes received in the latest observed playback.</summary>
    public long BytesReceived { get; set; }

    /// <summary>Gets or sets interruptions since the most recent playback began.</summary>
    public int Interruptions { get; set; }
}
