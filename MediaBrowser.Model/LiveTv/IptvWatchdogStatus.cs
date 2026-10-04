using System;
using System.Collections.Generic;

namespace MediaBrowser.Model.LiveTv;

/// <summary>A credential-free snapshot; reading it never opens a provider connection.</summary>
public sealed class IptvWatchdogStatus
{
    /// <summary>Gets or sets whether idle media checks are enabled.</summary>
    public bool IdleChecksEnabled { get; set; }

    /// <summary>Gets or sets whether other channels are included in the idle rotation.</summary>
    public bool SweepEnabled { get; set; }

    /// <summary>Gets or sets the worker state.</summary>
    public string State { get; set; } = "Waiting";

    /// <summary>Gets or sets the last completed idle check.</summary>
    public DateTime? LastCheckUtc { get; set; }

    /// <summary>Gets or sets tuner comparisons.</summary>
    public IReadOnlyList<IptvTunerHealth> Tuners { get; set; } = [];
}

/// <summary>Observed sources belonging to one configured tuner.</summary>
public sealed class IptvTunerHealth
{
    /// <summary>Gets or sets the tuner identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets an account-wide reason that prevents testing and recommendations.</summary>
    public string? AccountReason { get; set; }

    /// <summary>Gets or sets the recommended source identifier, if enough comparable evidence exists.</summary>
    public string? RecommendedSourceId { get; set; }

    /// <summary>Gets or sets the recommendation reason.</summary>
    public string? RecommendationReason { get; set; }

    /// <summary>Gets or sets destination hosts shared by configured entry servers.</summary>
    public IReadOnlyList<string> SharedDestinations { get; set; } = [];

    /// <summary>Gets or sets source comparisons from the last six hours.</summary>
    public IReadOnlyList<IptvSourceHealth> Sources { get; set; } = [];
}

/// <summary>Bounded observations of one configured entry server, never a pinned CDN destination.</summary>
public sealed class IptvSourceHealth
{
    /// <summary>Gets or sets the opaque source identifier, scoped to the current access configuration.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the entry host without paths, queries or user information.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Gets or sets the entry protocol.</summary>
    public string Scheme { get; set; } = string.Empty;

    /// <summary>Gets or sets whether this is the configured primary.</summary>
    public bool Active { get; set; }

    /// <summary>Gets or sets the most recent observation.</summary>
    public DateTime? LastCheckedUtc { get; set; }

    /// <summary>Gets or sets independent observations, deduplicating retries and progress events.</summary>
    public int Samples { get; set; }

    /// <summary>Gets or sets the number of different channels observed.</summary>
    public int Channels { get; set; }

    /// <summary>Gets or sets channels with fresh decoder or client playback evidence.</summary>
    public int VerifiedChannels { get; set; }

    /// <summary>Gets or sets the fraction of observations with successful media flow.</summary>
    public double? SuccessRate { get; set; }

    /// <summary>Gets or sets median time to decoded media, when available.</summary>
    public long? MedianStartMilliseconds { get; set; }

    /// <summary>Gets or sets observed interruptions.</summary>
    public int Interruptions { get; set; }

    /// <summary>Gets or sets bytes observed during ordinary playback only.</summary>
    public long BytesReceived { get; set; }

    /// <summary>Gets or sets a non-sensitive failure or insufficient-evidence reason.</summary>
    public string? Reason { get; set; }

    /// <summary>Gets or sets recently observed redirect destinations, without paths or credentials.</summary>
    public IReadOnlyList<string> DestinationHosts { get; set; } = [];
}
