using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.LiveTv.TunerHosts;

/// <summary>
/// Kodi IPTV Simple compatible stream URL helpers: pipe headers and live-now tokens.
/// </summary>
internal static partial class M3uStreamUrl
{
    [GeneratedRegex(@"\{lutc(?::([^}]+))?\}|\$\{(?:now|timestamp)(?::([^}]+))?\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LiveNowToken();

    /// <summary>
    /// Splits a Kodi-style stream line <c>url|user-agent=…&amp;referer=…</c> into a clean URL and headers.
    /// </summary>
    /// <param name="raw">The M3U URL line.</param>
    /// <returns>The playable URL and any stream headers.</returns>
    public static (string Url, Dictionary<string, string> Headers) Split(string raw)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (string.Empty, headers);
        }

        var line = raw.Trim();
        if (line.StartsWith('@'))
        {
            line = line[1..];
        }

        var pipe = line.IndexOf('|', StringComparison.Ordinal);
        if (pipe < 0)
        {
            return (line, headers);
        }

        var url = line[..pipe].Trim();
        ApplyHeaderPairs(headers, line[(pipe + 1)..]);
        return (url, headers);
    }

    /// <summary>
    /// Parses <c>name=value&amp;name=value</c> stream headers (Kodi / inputstream.adaptive).
    /// </summary>
    /// <param name="headers">The destination header map.</param>
    /// <param name="pairs">The raw pair string.</param>
    public static void ApplyHeaderPairs(Dictionary<string, string> headers, string? pairs)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (string.IsNullOrWhiteSpace(pairs))
        {
            return;
        }

        foreach (var part in pairs.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = part[..separator].Trim().TrimStart('!');
            var value = part[(separator + 1)..].Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            ApplyIdentityHeader(headers, key, value);
        }
    }

    /// <summary>
    /// Maps a VLC/Kodi identity key onto a real HTTP header name.
    /// </summary>
    /// <param name="headers">The destination header map.</param>
    /// <param name="key">The property name.</param>
    /// <param name="value">The header value.</param>
    public static void ApplyIdentityHeader(Dictionary<string, string> headers, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var name = NormalizeHeaderName(key);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        headers[name] = value;
    }

    /// <summary>
    /// Replaces Kodi live-now tokens in a channel URL (<c>{lutc}</c>, <c>${now}</c>, <c>${timestamp}</c>).
    /// </summary>
    /// <param name="url">The stream URL.</param>
    /// <param name="utcNow">The current UTC time.</param>
    /// <returns>The URL with tokens expanded.</returns>
    public static string SubstituteLiveNow(string url, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.Contains('{', StringComparison.Ordinal))
        {
            return url;
        }

        var utc = utcNow.Kind == DateTimeKind.Utc ? utcNow : utcNow.ToUniversalTime();
        return LiveNowToken().Replace(url, match =>
        {
            var format = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            return FormatLiveNow(utc, format);
        });
    }

    private static string FormatLiveNow(DateTime utc, string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return new DateTimeOffset(utc).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        }

        var text = new StringBuilder(format.Length * 2);
        foreach (var ch in format)
        {
            text.Append(ch switch
            {
                'Y' => utc.ToString("yyyy", CultureInfo.InvariantCulture),
                'm' => utc.ToString("MM", CultureInfo.InvariantCulture),
                'd' => utc.ToString("dd", CultureInfo.InvariantCulture),
                'H' => utc.ToString("HH", CultureInfo.InvariantCulture),
                'M' => utc.ToString("mm", CultureInfo.InvariantCulture),
                'S' => utc.ToString("ss", CultureInfo.InvariantCulture),
                _ => ch.ToString()
            });
        }

        return text.ToString();
    }

    private static string NormalizeHeaderName(string key)
    {
        if (key.Equals("user-agent", StringComparison.OrdinalIgnoreCase)
            || key.Equals("http-user-agent", StringComparison.OrdinalIgnoreCase))
        {
            return "User-Agent";
        }

        if (key.Equals("referer", StringComparison.OrdinalIgnoreCase)
            || key.Equals("referrer", StringComparison.OrdinalIgnoreCase)
            || key.Equals("http-referer", StringComparison.OrdinalIgnoreCase)
            || key.Equals("http-referrer", StringComparison.OrdinalIgnoreCase))
        {
            return "Referer";
        }

        if (key.Equals("origin", StringComparison.OrdinalIgnoreCase)
            || key.Equals("http-origin", StringComparison.OrdinalIgnoreCase))
        {
            return "Origin";
        }

        if (key.Equals("cookie", StringComparison.OrdinalIgnoreCase)
            || key.Equals("cookies", StringComparison.OrdinalIgnoreCase))
        {
            return "Cookie";
        }

        return key;
    }
}
