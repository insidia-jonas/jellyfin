using System;
using System.Linq;
using Jellyfin.LiveTv.Health;
using MediaBrowser.Model.LiveTv;

namespace Jellyfin.LiveTv.TunerHosts;

/// <summary>Access-scoped opaque selections. No client-supplied URL is ever accepted.</summary>
internal static class IptvSourceChoice
{
    internal const string Marker = "_iptv_";

    internal static string Id(TunerHostInfo tuner, string baseId, string origin) => baseId + Marker + IptvWatchdog.SourceId(tuner, origin);

    internal static string? Resolve(TunerHostInfo tuner, string baseId, string? requested)
        => IptvWatchdog.Sources(tuner).FirstOrDefault(s => string.Equals(Id(tuner, baseId, s), requested, StringComparison.Ordinal));

    internal static string Name(string origin)
    {
        var host = new Uri(origin).Host;
        if (!host.EndsWith(".plinkspile.cc", StringComparison.OrdinalIgnoreCase))
        {
            return host;
        }

        return host.Split('.')[0].ToUpperInvariant() switch
        {
            "AS01" => "CDN",
            "AM01" => "AM01 · Amsterdam",
            "AM02" => "AM02 · Amsterdam",
            "NL01" => "NL01 · Amsterdam",
            "CH01" => "CH01 · Zürich",
            "RO01" => "RO01 · Romania",
            "RU01" => "RU01 · Russia",
            "RU02" => "RU02 · Moscow",
            _ => host
        };
    }
}
