using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TreasureMaps.Languages;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.TreasureMaps.Subtitles;

/// <summary>Local, single-worker speech alignment. Writes a new sidecar; never overwrites the input.</summary>
public sealed class SubtitleSyncService(IMediaSourceManager sources)
{
    public const string PythonPath = "/opt/jellyfin-subtitle-sync/bin/python";
    public static bool Available => File.Exists(PythonPath);
    public static bool Supports(MediaStream stream) => stream.Type == MediaStreamType.Subtitle
        && new[] { "subrip", "srt", "ass", "ssa", "webvtt", "vtt", "mov_text", "text" }.Contains(stream.Codec?.ToLowerInvariant());

    public static string Fingerprint(string video, MediaStream subtitle, MediaStream audio)
    {
        var identity = string.Join("|", video, new FileInfo(video).Length, File.GetLastWriteTimeUtc(video).Ticks,
            subtitle.Path, subtitle.Codec, subtitle.Language, subtitle.DisplayTitle, audio.Codec, audio.Language, audio.DisplayTitle);
        if (subtitle.IsExternal && File.Exists(subtitle.Path)) identity += "|" + new FileInfo(subtitle.Path).Length + "|" + File.GetLastWriteTimeUtc(subtitle.Path).Ticks;
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)));
    }

    public (MediaStream Subtitle, MediaStream Audio) Tracks(BaseItem item, int subtitleIndex, int audioIndex)
    {
        var streams = sources.GetMediaStreams(new MediaBrowser.Controller.Persistence.MediaStreamQuery { ItemId = item.Id });
        var subtitle = streams.FirstOrDefault(s => s.Index == subtitleIndex && Supports(s));
        var audio = streams.FirstOrDefault(s => s.Index == audioIndex && s.Type == MediaStreamType.Audio && !s.IsExternal);
        if (subtitle is null || audio is null) throw new InvalidOperationException("Tonspur-Abgleich: Bitte eine vorhandene Text-Untertitelspur und eine Tonspur wählen. Bilduntertitel werden nicht unterstützt.");
        return (subtitle, audio);
    }

    public object Options(BaseItem item)
    {
        var streams = sources.GetMediaStreams(new MediaBrowser.Controller.Persistence.MediaStreamQuery { ItemId = item.Id });
        return new { available = Available,
            subtitles = streams.Where(Supports).Select(s => new { index = s.Index, title = s.DisplayTitle, language = s.Language }).ToArray(),
            audio = streams.Where(s => s.Type == MediaStreamType.Audio && !s.IsExternal).Select(s => new { index = s.Index, title = s.DisplayTitle }).ToArray() };
    }

    public async Task RunAsync(BaseItem item, SubtitleJob job, Action<SubtitleProgress> progress, CancellationToken cancellationToken)
    {
        if (!Available) throw new InvalidOperationException("Tonspur-Abgleich: Lokales Werkzeug fehlt. Installationsskript mit --install-subtitle-sync ausführen.");
        var (subtitle, audio) = Tracks(item, job.SubtitleIndex ?? -1, job.AudioIndex ?? -1);
        // Jellyfin may renumber public stream indexes after adding sidecars. ffmpeg
        // needs the ordinal within the actual container's audio/subtitle streams.
        var streams = sources.GetMediaStreams(new MediaBrowser.Controller.Persistence.MediaStreamQuery { ItemId = item.Id });
        var audioOrdinal = Array.FindIndex(streams.Where(s => s.Type == MediaStreamType.Audio && !s.IsExternal).OrderBy(s => s.Index).ToArray(), s => s.Index == audio.Index);
        var subtitleOrdinal = Array.FindIndex(streams.Where(s => s.Type == MediaStreamType.Subtitle && !s.IsExternal).OrderBy(s => s.Index).ToArray(), s => s.Index == subtitle.Index);
        var video = SubtitleFiles.ResolveMediaPath(item) ?? throw new InvalidOperationException("Tonspur-Abgleich: Lokale Mediendatei nicht verfügbar.");
        if (Fingerprint(video, subtitle, audio) != job.TrackFingerprint)
            throw new InvalidOperationException("Tonspur-Abgleich: Medien oder Spuren wurden geändert. Bitte die Spuren erneut auswählen.");
        var language = LanguageMatcher.Normalize(subtitle.Language);
        if (!Regex.IsMatch(language ?? "", "^[a-z]{2,3}$")) language = "und";
        var target = Path.Combine(Path.GetDirectoryName(video)!, Path.GetFileNameWithoutExtension(video)
            + $".Synchronisiert-Tonspur-{audioOrdinal + 1}.{language}.srt");
        if (subtitle.IsExternal && Path.GetFullPath(subtitle.Path) == Path.GetFullPath(target))
            throw new InvalidOperationException("Tonspur-Abgleich: Diese Spur wurde bereits synchronisiert. Für einen neuen Abgleich bitte die Originalspur wählen.");
        var fingerprint = (new FileInfo(video).Length, File.GetLastWriteTimeUtc(video));
        var work = Path.Combine(Path.GetTempPath(), "evolution-sync-" + job.Id.ToString("N"));
        Directory.CreateDirectory(work);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(work, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(20));
        try
        {
            progress(new("extracting", 5, "Untertitel werden für den lokalen Tonspur-Abgleich vorbereitet."));
            var input = Path.Combine(work, "input.srt");
            var output = Path.Combine(work, "aligned.srt");
            var ffmpeg = MediaProbe.FindTool("ffmpeg")!;
            if (subtitle.IsExternal && (string.IsNullOrWhiteSpace(subtitle.Path) || !Path.IsPathFullyQualified(subtitle.Path) || !File.Exists(subtitle.Path)))
                throw new InvalidOperationException("Tonspur-Abgleich: Untertiteldatei nicht lokal verfügbar.");
            await RunProcess(ffmpeg, new[] { "-nostdin", "-v", "error", "-threads", "1", "-i", subtitle.IsExternal ? subtitle.Path : video,
                "-map", subtitle.IsExternal ? "0:s:0" : "0:s:" + subtitleOrdinal.ToString(CultureInfo.InvariantCulture), "-c:s", "srt", "-y", input }, null, work, deadline.Token).ConfigureAwait(false);
            var original = SrtCues.Parse(await ReadBounded(input, deadline.Token).ConfigureAwait(false));
            if (original.Count < 20) throw new InvalidOperationException("Tonspur-Abgleich: Zu wenige Textpassagen für einen verlässlichen Abgleich (mindestens 20 nötig).");
            progress(new("aligning", 20, "Stimmen werden lokal mit den Untertiteln abgeglichen. Du kannst den Film verlassen. Keine API-Kosten."));
            using var resource = typeof(SubtitleSyncService).Assembly.GetManifestResourceStream("Jellyfin.Plugin.TreasureMaps.Subtitles.subtitle-sync.py")!;
            var script = Path.Combine(work, "sync.py");
            await using (var file = File.Create(script)) { await resource.CopyToAsync(file, deadline.Token).ConfigureAwait(false); }
            await RunProcess(PythonPath, new[] { script }, JsonSerializer.Serialize(new { video, input, output, audio = audioOrdinal, ffmpeg }), work, deadline.Token).ConfigureAwait(false);
            var aligned = SrtCues.Parse(await ReadBounded(output, deadline.Token).ConfigureAwait(false));
            if (aligned.Count < original.Count * 0.95 || aligned.Count > original.Count || aligned.Any(c => c.End <= c.Start)
                || fingerprint != (new FileInfo(video).Length, File.GetLastWriteTimeUtc(video))
                || Fingerprint(video, subtitle, audio) != job.TrackFingerprint)
                throw new InvalidOperationException("Tonspur-Abgleich: Ergebnis unvollständig oder Mediendatei geändert. Das Original bleibt erhalten.");
            progress(new("saving", 95, "Synchronisierte Spur wird zusätzlich gespeichert."));
            // Copy to the media filesystem before the atomic rename; /tmp may be another volume.
            var staged = target + "." + job.Id.ToString("N") + ".tmp";
            try
            {
                File.Copy(output, staged, false);
                deadline.Token.ThrowIfCancellationRequested();
                File.Move(staged, target, true);
            }
            finally { if (File.Exists(staged)) File.Delete(staged); }
        }
        finally { Directory.Delete(work, true); }
    }

    private static async Task<string> ReadBounded(string path, CancellationToken ct)
    {
        if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > 4_000_000)
            throw new InvalidOperationException("Tonspur-Abgleich: Keine gültige Untertiteldatei erzeugt.");
        return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
    }

    private static async Task RunProcess(string executable, string[] arguments, string? input, string work, CancellationToken ct)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = work,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        info.Environment["OMP_NUM_THREADS"] = "1";
        info.Environment["OPENBLAS_NUM_THREADS"] = "1";
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Tonspur-Abgleich: Werkzeug konnte nicht gestartet werden.");
        using var kill = ct.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        if (input is not null) await process.StandardInput.WriteLineAsync(input.AsMemory(), ct).ConfigureAwait(false);
        process.StandardInput.Close();
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidOperationException("Tonspur-Abgleich: Kein zuverlässiges Ergebnis. Eine passende Untertitelversion wählen oder den Versatz manuell korrigieren.");
    }
}
