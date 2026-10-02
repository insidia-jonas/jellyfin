using System;
using System.IO;
using System.Net.Http;

namespace Jellyfin.LiveTv.Health;

internal sealed record ChannelFailure(string Reason, bool ProviderWide, bool StopRetries)
{
    internal static ChannelFailure FromStatus(int code) => code switch
    {
        401 or 403 => new("ProviderAuthentication", true, true),
        409 or 429 or 453 or 509 => new("ProviderBusy", true, true),
        404 or 410 => new("ChannelMissing", false, false),
        >= 500 => new("ProviderUnavailable", true, false),
        _ => new("InvalidMedia", false, false)
    };

    internal static ChannelFailure FromException(Exception error) => error switch
    {
        HttpRequestException { StatusCode: not null } http => FromStatus((int)http.StatusCode.Value),
        HttpRequestException => new("ProviderNetwork", true, false),
        TimeoutException => new("NoMediaData", false, false),
        EndOfStreamException => new("Interrupted", false, false),
        _ => new("InvalidMedia", false, false)
    };
}
