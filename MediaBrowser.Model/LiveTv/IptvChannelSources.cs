using System;
using System.Collections.Generic;

namespace MediaBrowser.Model.LiveTv;

/// <summary>Read-only, credential-free choices for one accessible channel.</summary>
public sealed class IptvChannelSources
{
    /// <summary>Gets or sets the media source used for automatic failover.</summary>
    public string AutomaticMediaSourceId { get; set; } = string.Empty;

    /// <summary>Gets or sets the observed source of the specified playback, including failover.</summary>
    public string? PlayingSourceId { get; set; }

    /// <summary>Gets or sets the background worker state.</summary>
    public string State { get; set; } = "Waiting";

    /// <summary>Gets or sets an account/capacity problem, distinct from a dead channel.</summary>
    public string? AccountReason { get; set; }

    /// <summary>Gets or sets the configured source choices.</summary>
    public IReadOnlyList<IptvChannelSource> Sources { get; set; } = [];
}

/// <summary>A configured server and its recent media evidence.</summary>
public sealed class IptvChannelSource
{
    /// <summary>Gets or sets the opaque source identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the explicit playback selection identifier.</summary>
    public string MediaSourceId { get; set; } = string.Empty;

    /// <summary>Gets or sets the readable server name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the entry host, without credentials or paths.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Gets or sets whether this server is the configured default.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Gets or sets Unknown, Reachable, Unstable or Failed.</summary>
    public string Status { get; set; } = "Unknown";

    /// <summary>Gets or sets whether the evidence concerns this channel.</summary>
    public bool ChannelSpecific { get; set; }

    /// <summary>Gets or sets the time of the latest observation, not the menu read.</summary>
    public DateTime? LastCheckedUtc { get; set; }

    /// <summary>Gets or sets median decoder startup time, not network ping.</summary>
    public long? MedianStartMilliseconds { get; set; }

    /// <summary>Gets or sets the observation count.</summary>
    public int Samples { get; set; }
}
