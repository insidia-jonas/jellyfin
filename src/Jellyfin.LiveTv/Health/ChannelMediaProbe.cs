using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.LiveTv.Health;

/// <summary>Decodes a short media sample within one cancellable provider reservation.</summary>
public sealed partial class ChannelMediaProbe
{
    private readonly IMediaEncoder _encoder;

    /// <summary>Initializes a new instance of the <see cref="ChannelMediaProbe"/> class.</summary>
    /// <param name="encoder">The configured Jellyfin ffmpeg installation.</param>
    public ChannelMediaProbe(IMediaEncoder encoder) => _encoder = encoder;

    internal async Task<ProbeResult> ProbeAsync(MediaSourceInfo source, CancellationToken cancellationToken, bool requireVideo = true)
    {
        if (source.Protocol != MediaProtocol.Http || !Uri.TryCreate(source.Path, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return new ProbeResult(false, null, 0);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        var info = new ProcessStartInfo(_encoder.EncoderPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-nostats", "-progress", "pipe:1", "-threads", "1", "-filter_threads", "1", "-rw_timeout", "8000000", "-analyzeduration", "2000000", "-probesize", "1048576" })
        {
            info.ArgumentList.Add(argument);
        }

        var headers = source.RequiredHttpHeaders?.Where(p => !string.IsNullOrWhiteSpace(p.Value)
            && !p.Key.Contains('\r', StringComparison.Ordinal) && !p.Key.Contains('\n', StringComparison.Ordinal)
            && !p.Value.Contains('\r', StringComparison.Ordinal) && !p.Value.Contains('\n', StringComparison.Ordinal));
        if (headers is not null && headers.Any())
        {
            info.ArgumentList.Add("-headers");
            info.ArgumentList.Add(string.Concat(headers.Select(p => p.Key + ": " + p.Value + "\r\n")));
        }

        foreach (var argument in new[] { "-i", source.Path, "-t", "1", "-map", "0:v:0?", "-map", "0:a:0?", "-threads", "1", "-f", "null", "-" })
        {
            info.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = info };
        var watch = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
        }
        finally
        {
            // Foreground tuning must wait for actual process exit, not just a cancellation request.
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                }
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var progress = await output.ConfigureAwait(false);
        var stderr = await errors.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!timedOut && process.ExitCode == 0 && HasDecodedMedia(progress, requireVideo))
        {
            return new ProbeResult(true, null, watch.ElapsedMilliseconds);
        }

        return new ProbeResult(false, FailureFromOutput(stderr, timedOut), watch.ElapsedMilliseconds);
    }

    internal static ChannelFailure FailureFromOutput(string stderr, bool timedOut)
    {
        var match = HttpError().Match(stderr);
        if (match.Success)
        {
            return ChannelFailure.FromStatus(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
        }

        // FFmpeg can exit on a socket timeout before our process deadline expires.
        // That does not prove that this channel contains invalid media.
        if (new[] { "Connection timed out", "Connection refused", "Network is unreachable", "No route to host", "Failed to resolve", "Temporary failure in name resolution" }
            .Any(message => stderr.Contains(message, StringComparison.OrdinalIgnoreCase)))
        {
            return new ChannelFailure("ProviderNetwork", true, false);
        }

        return new ChannelFailure(timedOut ? "ProbeTimeout" : "InvalidMedia", false, false);
    }

    internal static bool HasDecodedMedia(string progress, bool requireVideo = false) => progress.Split('\n').Any(line =>
    {
        var pair = line.Trim().Split('=', 2);
        return pair.Length == 2 && (pair[0] == "frame" || (!requireVideo && pair[0] == "out_time_us"))
            && long.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0;
    });

    [GeneratedRegex(@"(?:HTTP error|Server returned|HTTP/[\d.]+)\s+(\d{3})", RegexOptions.IgnoreCase)]
    private static partial Regex HttpError();

    internal sealed record ProbeResult(bool Success, ChannelFailure? Failure, long ElapsedMilliseconds);
}
