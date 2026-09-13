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
    /// <returns>The PNG path, or null when generation fails.</returns>
    public static string? GetPosterPath(string key, string label)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        lock (Lock)
        {
            var dir = Path.Combine(Path.GetTempPath(), "treasuremaps", "posters");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, Sanitize(key) + ".png");
            if (File.Exists(file) && new FileInfo(file).Length > 200)
            {
                return file;
            }

            var font = FontCandidates.FirstOrDefault(File.Exists);
            if (font is null || !TryDraw(file, label, font))
            {
                return File.Exists(file) ? file : null;
            }

            return file;
        }
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
