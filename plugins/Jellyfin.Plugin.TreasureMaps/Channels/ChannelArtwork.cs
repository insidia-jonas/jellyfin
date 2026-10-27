using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.TreasureMaps.Channels;

/// <summary>
/// Builds simple poster tiles for the channel and its category folders. Fire TV / Android TV
/// clients show a generic colored rectangle when a folder has no primary image.
/// </summary>
public static class ChannelArtwork
{
    private static readonly object Lock = new();
    private static readonly string[] FontCandidates =
    {
        "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
        "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf",
        "/usr/share/fonts/truetype/macos/Inter-Regular.ttf",
        "/usr/share/fonts/truetype/freefont/FreeSansBold.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
    };

    /// <summary>
    /// Returns a local PNG path for a labeled poster, generating it with ffmpeg on first use.
    /// </summary>
    /// <param name="key">Stable file key (folder id).</param>
    /// <param name="label">Text drawn on the poster.</param>
    /// <param name="directory">Optional isolated artwork directory for tools and tests.</param>
    /// <returns>The PNG path, or null when generation fails.</returns>
    public static string? GetPosterPath(string key, string label, string? directory = null)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        lock (Lock)
        {
            // Channel items retain this path in the database across reboots. Never
            // return /tmp paths, which disappear while their database references live on.
            var dir = directory ?? Plugin.Instance?.ArtworkDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "jellyfin-evolution", "artwork-v4");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, Sanitize(key) + ".png");
            if (IsCompletePng(file))
            {
                return file;
            }

            var artworkKey = key switch
            {
                "channel" => "indexer",
                "movies-de" or "trending-movie" => "movies",
                "tv-de" or "trending-tv" => "tv",
                var value when value.StartsWith("genre:", StringComparison.Ordinal) => "genres",
                var value when value.StartsWith("find:", StringComparison.Ordinal) => "find",
                var value when value.StartsWith("tmfeed:", StringComparison.Ordinal) => "trending",
                var value when value.StartsWith("pg:", StringComparison.Ordinal) => "find",
                var value when value.StartsWith("releases-empty-", StringComparison.Ordinal) => "indexer",
                _ => key
            };
            using var artwork = typeof(ChannelArtwork).Assembly.GetManifestResourceStream(
                "Jellyfin.Plugin.TreasureMaps.Channels.Artwork." + artworkKey + ".png");
            var temporary = Path.Combine(dir, Sanitize(key) + "." + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                if (artwork is not null)
                {
                    using var target = File.Create(temporary);
                    artwork.CopyTo(target);
                    target.Flush(flushToDisk: true);
                }
                else
                {
                    var font = FontCandidates.FirstOrDefault(File.Exists);
                    if (font is null || !TryDraw(temporary, label, font))
                    {
                        return null;
                    }
                }

                if (!IsCompletePng(temporary))
                {
                    return null;
                }

                File.Move(temporary, file, overwrite: true);
                return file;
            }
            finally
            {
                File.Delete(temporary);
            }
        }
    }

    private static bool IsCompletePng(string file)
    {
        if (!File.Exists(file) || new FileInfo(file).Length < 32)
        {
            return false;
        }

        using var stream = File.OpenRead(file);
        Span<byte> header = stackalloc byte[8];
        stream.ReadExactly(header);
        stream.Seek(-12, SeekOrigin.End);
        Span<byte> end = stackalloc byte[12];
        stream.ReadExactly(end);
        return header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            && end.SequenceEqual(new byte[] { 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130 });
    }

    private static bool TryDraw(string file, string label, string font)
    {
        try
        {
            var text = (label ?? string.Empty).Replace('\\', '/');
            if (text.Length > 22)
            {
                text = text[..22].TrimEnd() + "…";
            }

            var fontsize = text.Length > 16 ? 36 : text.Length > 10 ? 44 : 54;
            var escaped = text.Replace(":", "\\:", StringComparison.Ordinal).Replace("'", string.Empty, StringComparison.Ordinal);
            var fontEscaped = font.Replace(":", "\\:", StringComparison.Ordinal).Replace("'", string.Empty, StringComparison.Ordinal);
            var args = "-y -f lavfi -i color=c=0x1c1630:s=500x750 -vf \"" +
                       "drawbox=x=24:y=24:w=452:h=702:color=0xf5e6c8@0.35:t=6," +
                       "drawtext=fontfile='" + fontEscaped + "':text='" + escaped +
                       "':x=(w-text_w)/2:y=(h-text_h)/2:fontsize=" + fontsize +
                       ":fontcolor=0xf5e6c8:borderw=2:bordercolor=0x110d1c\" " +
                       "-frames:v 1 \"" + file + "\"";

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = args,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(15000))
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                    // ignored
                }

                return false;
            }

            return process.ExitCode == 0 && File.Exists(file) && new FileInfo(file).Length > 200;
        }
        catch
        {
            return false;
        }
    }

    private static string Sanitize(string key)
        => new string(key.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
}
