using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.LiveTv.Channels;

/// <summary>
/// Formats Live TV library-channel tiles: clean sender names and a compact now/next EPG line.
/// </summary>
internal static partial class LiveTvLibraryChannelPresentation
{
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex ExtraSpace();

    [GeneratedRegex(@"^[A-Z]{2}\s*[-:–]\s+", RegexOptions.CultureInvariant)]
    private static partial Regex CountryPrefix();

    /// <summary>
    /// Strips playlist junk from a channel or group-title so Fire TV cards stay readable.
    /// </summary>
    /// <param name="name">The raw M3U name.</param>
    /// <returns>The cleaned name, or empty.</returns>
    public static string CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var core = name;
        var pipe = core.LastIndexOf('|');
        if (pipe >= 0 && pipe < core.Length - 1)
        {
            core = core[(pipe + 1)..];
        }

        core = ExtraSpace().Replace(core.Trim(), " ");
        core = CountryPrefix().Replace(core, string.Empty).Trim();
        return core;
    }

    /// <summary>
    /// Builds a stable name-sort key (channel number first).
    /// </summary>
    /// <param name="number">The playlist channel number.</param>
    /// <param name="name">The cleaned display name.</param>
    /// <returns>The sort key.</returns>
    public static string SortName(string? number, string name)
    {
        if (double.TryParse(number, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0)
        {
            return parsed.ToString("00000.00", CultureInfo.InvariantCulture) + "-" + name;
        }

        return "zzzzz-" + name;
    }

    /// <summary>
    /// Card title: sender name, plus the current program when the guide has one.
    /// </summary>
    /// <param name="channelName">The cleaned channel name.</param>
    /// <param name="guide">The now/next block, or <c>null</c>.</param>
    /// <returns>The tile title.</returns>
    public static string CardName(string channelName, LiveTvNowNext? guide)
    {
        if (guide is null || string.IsNullOrWhiteSpace(guide.NowTitle))
        {
            return channelName;
        }

        return channelName + "  ·  " + guide.NowTitle.Trim();
    }

    /// <summary>
    /// Details overview: now, next, and a short plot.
    /// </summary>
    /// <param name="guide">The now/next block, or <c>null</c>.</param>
    /// <returns>The overview, or empty.</returns>
    public static string Overview(LiveTvNowNext? guide)
    {
        if (guide is null)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(guide.NowTitle))
        {
            text.Append("Jetzt: ").Append(guide.NowTitle);
            AppendRange(text, guide.NowStart, guide.NowEnd);
        }

        if (!string.IsNullOrWhiteSpace(guide.NextTitle))
        {
            if (text.Length > 0)
            {
                text.AppendLine();
            }

            text.Append("Danach: ").Append(guide.NextTitle);
            AppendRange(text, guide.NextStart, guide.NextEnd);
        }

        if (!string.IsNullOrWhiteSpace(guide.NowOverview))
        {
            if (text.Length > 0)
            {
                text.AppendLine().AppendLine();
            }

            var plot = guide.NowOverview.Trim();
            if (plot.Length > 280)
            {
                plot = plot[..277].TrimEnd() + "…";
            }

            text.Append(plot);
        }

        return text.ToString();
    }

    private static void AppendRange(StringBuilder text, DateTime? start, DateTime? end)
    {
        if (start is null && end is null)
        {
            return;
        }

        text.Append(" (");
        if (start is not null)
        {
            text.Append(start.Value.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture));
        }

        if (end is not null)
        {
            if (start is not null)
            {
                text.Append('–');
            }

            text.Append(end.Value.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture));
        }

        text.Append(')');
    }
}
