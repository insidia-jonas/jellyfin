using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TreasureMaps.Subtitles;
using Xunit;

namespace Jellyfin.Plugin.TreasureMaps.Tests;

public class SubtitlesTests
{
    private static readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void MovieHasher_IsDeterministic_AndSizeSensitive()
    {
        // Build two 256 KiB streams that differ by one byte.
        var a = new byte[256 * 1024];
        for (var i = 0; i < a.Length; i++)
        {
            a[i] = (byte)(i % 251);
        }

        var b = (byte[])a.Clone();
        b[100] ^= 0xFF;

        using var sa = new MemoryStream(a);
        using var sb = new MemoryStream(b);

        var ha1 = MovieHasher.ComputeHash(sa, a.Length);
        sa.Position = 0;
        var ha2 = MovieHasher.ComputeHash(sa, a.Length);
        var hb = MovieHasher.ComputeHash(sb, b.Length);

        Assert.NotNull(ha1);
        Assert.Equal(16, ha1!.Length);       // 16 hex chars = 64-bit
        Assert.Equal(ha1, ha2);              // deterministic
        Assert.NotEqual(ha1, hb);            // content in first 64KiB affects the hash
    }

    [Fact]
    public void MovieHasher_TooSmall_ReturnsNull()
    {
        using var s = new MemoryStream(new byte[1024]);
        Assert.Null(MovieHasher.ComputeHash(s, 1024));
    }

    [Fact]
    public void SearchResponse_Deserializes()
    {
        const string json = """
        {
          "data": [
            {
              "id": "12345",
              "attributes": {
                "language": "de",
                "download_count": 4210,
                "ratings": 8.5,
                "hearing_impaired": false,
                "ai_translated": false,
                "machine_translated": false,
                "moviehash_match": true,
                "release": "Dune.2021.1080p.BluRay",
                "files": [ { "file_id": 998877, "file_name": "Dune.srt" } ]
              }
            }
          ]
        }
        """;

        var response = JsonSerializer.Deserialize<OsSearchResponse>(json, _options);

        Assert.NotNull(response);
        var sub = Assert.Single(response!.Data);
        Assert.Equal("de", sub.Attributes!.Language);
        Assert.Equal(4210, sub.Attributes.DownloadCount);
        Assert.True(sub.Attributes.MoviehashMatch);
        Assert.Equal(998877, sub.Attributes.Files[0].FileId);
    }

    [Fact]
    public void LooksLikeEmail_DetectsAddressUsedAsUsername()
    {
        Assert.True(OpenSubtitlesErrors.LooksLikeEmail("j.kemmner@gmx.de"));
        Assert.False(OpenSubtitlesErrors.LooksLikeEmail("jkemmner"));
        Assert.False(OpenSubtitlesErrors.LooksLikeEmail(null));
    }

    [Fact]
    public void FormatHttpError_ExplainsEmailLogin()
    {
        var message = OpenSubtitlesErrors.FormatHttpError(
            400,
            """{"message":"Error, invalid username/password - remember to use your username and not your email to authenticate","status":400}""",
            "j.kemmner@gmx.de");

        Assert.Contains("username", message, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not the email", message, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("400 (Bad Request)", message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void SubtitleCost_English_WhisperOnly()
    {
        var quote = SubtitleCost.Build(90 * 60, "en", 0.006m, 0.15m, "whisper-1", "Dune");

        Assert.Equal(90, quote.Minutes);
        Assert.Equal(6, quote.Chunks);
        Assert.Equal(0.54m, quote.WhisperUsd);
        Assert.Equal(0m, quote.TranslationUsd);
        Assert.False(quote.IncludesTranslation);
        Assert.Equal("0.54 USD", SubtitleCost.FormatUsd(quote.TotalUsd));
        Assert.Contains("KI erzeugen · ca. 0.54 USD · 90 Min. Whisper", quote.Summary, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Übersetzung", quote.Summary, System.StringComparison.Ordinal);
    }

    [Fact]
    public void SubtitleCost_German_IncludesTranslationAndShowsBeforeStart()
    {
        var quote = SubtitleCost.Build(118 * 60, "de", 0.006m, 0.15m, "whisper-1");

        Assert.Equal(118, quote.Minutes);
        Assert.Equal(8, quote.Chunks);
        Assert.Equal(0.708m, quote.WhisperUsd);
        Assert.Equal(0.0071m, quote.TranslationUsd);
        Assert.True(quote.IncludesTranslation);
        Assert.Contains("KI erzeugen · ca. 0.72 USD", quote.Summary, System.StringComparison.Ordinal);
        Assert.Contains("Übersetzung DE", quote.Summary, System.StringComparison.Ordinal);
        Assert.Contains("8 Teile", quote.Summary, System.StringComparison.Ordinal);
    }

    [Fact]
    public void SubtitleCost_ExistingSidecar_IsFree()
    {
        var quote = SubtitleCost.Build(60, "de", 0.006m, 0.15m, "whisper-1");
        quote.AlreadyExists = true;
        quote.Summary = SubtitleCost.FormatSummary(quote);

        Assert.Contains("Bereits erzeugt · 0.00 USD", quote.Summary, System.StringComparison.Ordinal);
        Assert.Contains("DE.srt", quote.Summary, System.StringComparison.Ordinal);
    }

    [Fact]
    public void SrtCues_ShiftConcatAndChunk()
    {
        const string srt = "1\n00:00:01,000 --> 00:00:02,500\nHello\n\n2\n00:00:03,000 --> 00:00:04,000\nWorld\n";
        var (shifted, next) = SrtCues.Shift(srt, System.TimeSpan.FromMinutes(15), 5);

        Assert.Equal(7, next);
        Assert.Contains("5\n00:15:01,000 --> 00:15:02,500\nHello", shifted, System.StringComparison.Ordinal);
        Assert.Contains("6\n00:15:03,000 --> 00:15:04,000\nWorld", shifted, System.StringComparison.Ordinal);

        var joined = SrtCues.Concat(new[] { shifted, "7\n00:30:00,000 --> 00:30:01,000\nBye\n" });
        Assert.Contains("Bye", joined, System.StringComparison.Ordinal);
        Assert.EndsWith("\n", joined, System.StringComparison.Ordinal);

        var chunks = SrtCues.Chunk(srt, 40);
        Assert.True(chunks.Count >= 2);
    }

    [Fact]
    public void AiSubtitleId_RoundtripsPathAndCostInputs()
    {
        var quote = new SubtitleQuote
        {
            Path = "/media/movies/Dune (2021)/Dune.mkv",
            Language = "de",
            Seconds = 7080,
            Minutes = 118,
            Chunks = 8,
            IncludesTranslation = true
        };

        var encoded = AiSubtitleProvider.EncodeId(quote);
        Assert.StartsWith("ai|", encoded, System.StringComparison.Ordinal);

        var decoded = AiSubtitleProvider.DecodeId(encoded);
        Assert.Equal(quote.Path, decoded.Path);
        Assert.Equal("de", decoded.Language);
        Assert.Equal(7080, decoded.Seconds);
        Assert.Equal(118, decoded.Minutes);
        Assert.Equal(8, decoded.Chunks);
        Assert.True(decoded.IncludesTranslation);
    }

    [Fact]
    public void ToThreeLetter_MapsCommonCodes()
    {
        Assert.Equal("ger", OpenSubtitlesProvider.ToThreeLetter("de"));
        Assert.Equal("eng", OpenSubtitlesProvider.ToThreeLetter("en"));
        Assert.Equal("ger", OpenSubtitlesProvider.ToThreeLetter(null!));
    }

    [Fact]
    public void SubtitleFiles_SidecarPath_UsesLanguageCode()
    {
        var path = SubtitleFiles.SidecarPath("/data/movies/Heat.mkv", "German");
        Assert.Equal("/data/movies/Heat.de.srt", path.Replace('\\', '/'));
        Assert.Equal("de", SubtitleFiles.SanitizeLanguage("ger"));
        Assert.Equal("de", SubtitleFiles.SanitizeLanguage(null));
    }

    [Fact]
    public async Task MediaProbe_ReadsDuration_ExtractsAudio_AndWritesSidecar()
    {
        Assert.False(string.IsNullOrEmpty(MediaProbe.FindTool("ffmpeg")));
        Assert.False(string.IsNullOrEmpty(MediaProbe.FindTool("ffprobe")));

        var dir = Path.Combine(Path.GetTempPath(), "tm-sub-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var video = Path.Combine(dir, "clip.mp4");
            CreateTinyClip(video);
            var seconds = await MediaProbe.GetDurationSecondsAsync(video, CancellationToken.None);
            Assert.NotNull(seconds);
            Assert.InRange(seconds!.Value, 1.5, 2.6);

            var audio = Path.Combine(dir, "slice.mp3");
            await MediaProbe.ExtractAudioAsync(video, audio, TimeSpan.Zero, TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.True(File.Exists(audio));
            Assert.True(new FileInfo(audio).Length > 0);

            var sidecar = SubtitleFiles.Write(video, "de", "1\n00:00:00,000 --> 00:00:01,000\nHi\n");
            Assert.True(File.Exists(sidecar));
            Assert.True(SubtitleFiles.TryRead(video, "de", out var srt));
            Assert.Contains("Hi", srt, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void CreateTinyClip(string path)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = MediaProbe.FindTool("ffmpeg")!,
            Arguments = "-y -f lavfi -i color=c=black:s=160x120:d=2 -f lavfi -i sine=frequency=440:duration=2 -shortest -c:v libx264 -c:a aac \"" + path + "\"",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        });
        Assert.NotNull(process);
        process!.WaitForExit(30000);
        Assert.Equal(0, process.ExitCode);
        Assert.True(File.Exists(path));
    }
}
