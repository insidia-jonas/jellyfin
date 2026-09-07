using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.TreasureMaps.Subtitles;

/// <summary>
/// Runs ffprobe/ffmpeg from the Jellyfin or system install to read duration and extract audio.
/// </summary>
public static class MediaProbe
{
    /// <summary>
    /// Reads the duration of a media file in seconds.
    /// </summary>
    /// <param name="path">The media path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Seconds, or null when probing fails.</returns>
    public static async Task<double?> GetDurationSecondsAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        var probe = FindTool("ffprobe");
        if (probe is null)
        {
            return null;
        }

        var result = await RunAsync(
            probe,
            "-v error -show_entries format=duration -of default=nk=1:nw=1 \"" + path.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"",
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);

        return double.TryParse(result.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? seconds
            : null;
    }

    /// <summary>
    /// Extracts a mono 16 kHz MP3 slice for Whisper.
    /// </summary>
    /// <param name="input">The media path.</param>
    /// <param name="output">The destination MP3.</param>
    /// <param name="start">The start offset.</param>
    /// <param name="length">The slice length.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when ffmpeg finishes.</returns>
    public static async Task ExtractAudioAsync(
        string input,
        string output,
        TimeSpan start,
        TimeSpan length,
        CancellationToken cancellationToken)
    {
        var ffmpeg = FindTool("ffmpeg") ?? throw new InvalidOperationException("ffmpeg is not installed (needed to create AI subtitles).");
        var args = string.Format(
            CultureInfo.InvariantCulture,
            "-y -ss {0} -t {1} -i \"{2}\" -vn -ac 1 -ar 16000 -b:a 32k \"{3}\"",
            Format(start),
            Format(length),
            input.Replace("\"", "\\\"", StringComparison.Ordinal),
            output.Replace("\"", "\\\"", StringComparison.Ordinal));
        await RunAsync(ffmpeg, args, TimeSpan.FromMinutes(8), cancellationToken).ConfigureAwait(false);
        if (!File.Exists(output) || new FileInfo(output).Length == 0)
        {
            throw new InvalidOperationException("ffmpeg did not write an audio slice for Whisper.");
        }
    }

    /// <summary>
    /// Finds ffmpeg or ffprobe on the usual Jellyfin paths.
    /// </summary>
    /// <param name="name">The tool name.</param>
    /// <returns>The path, or null.</returns>
    public static string? FindTool(string name)
    {
        foreach (var candidate in new[]
        {
            Path.Combine("/usr/lib/jellyfin-ffmpeg", name),
            Path.Combine("/usr/bin", name),
            name
        })
        {
            if (candidate == name || File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string Format(TimeSpan value)
        => ((int)value.TotalSeconds).ToString(CultureInfo.InvariantCulture);

    private static async Task<string> RunAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo.FileName = fileName;
        process.StartInfo.Arguments = arguments;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignored
            }

            throw new TimeoutException(fileName + " timed out.");
        }

        var output = await stdout.ConfigureAwait(false);
        if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
        {
            throw new InvalidOperationException(fileName + " failed: " + (await stderr.ConfigureAwait(false)).Trim());
        }

        return output;
    }
}
