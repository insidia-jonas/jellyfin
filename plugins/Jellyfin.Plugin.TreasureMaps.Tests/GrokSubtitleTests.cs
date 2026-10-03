using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.TreasureMaps.Configuration;
using Jellyfin.Plugin.TreasureMaps.Recommendations;
using Jellyfin.Plugin.TreasureMaps.Subtitles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TreasureMaps.Tests;

public class GrokSubtitleTests
{
    [Fact]
    public void GrokAccountUsesXaiSpeechAndNeverSendsItsKeyToWhisper()
    {
        var config = new PluginConfiguration { AiProvider = "grok", AiApiKey = "test-xai-key" };
        var speech = SubtitleSpeechSettings.Resolve(config, false);
        Assert.Equal("https://api.x.ai/v1/stt", speech.Url);
        Assert.True(speech.IsGrok);
        Assert.Equal("grok-voice-transcribe-2.0", speech.Model);
        config.SubtitleSpeechProvider = "whisper";
        Assert.Throws<InvalidOperationException>(() => SubtitleSpeechSettings.Resolve(config, false));
        config.WhisperApiKey = "separate-speech-key";
        speech = SubtitleSpeechSettings.Resolve(config, false);
        Assert.False(speech.IsGrok);
        Assert.Equal("separate-speech-key", speech.Key);
    }

    [Fact]
    public void WordsBecomeTimedCuesAndSilenceSeparatesSentences()
    {
        var transcript = GrokTranscription.Parse("""{"language":"en","words":[{"text":"Hello","start":1,"end":1.3},{"text":"world.","start":1.4,"end":2},{"text":"Welcome!","start":4,"end":5}]}""");
        var cues = SrtCues.Parse(transcript.Srt);
        Assert.Equal("en", transcript.Language);
        Assert.Equal(2, cues.Count);
        Assert.Equal("Hello world.", cues[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(1), cues[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(5), cues[1].End);
    }

    [Fact]
    public void SpeechWithoutTimestampsCannotBecomeInventedSubtitles()
    {
        Assert.Throws<InvalidOperationException>(() => GrokTranscription.Parse("""{"language":"en","text":"Hello"}"""));
        Assert.Empty(GrokTranscription.Parse("""{"language":"en","text":"","words":[]}""").Srt);
        Assert.Throws<InvalidOperationException>(() => GrokTranscription.Parse("""{"words":[{"text":"bad","start":2,"end":1}]}"""));
    }

    [Fact]
    public void TranslationKeepsFirstCueAndTimestampsBeyondOneHour()
    {
        const string source = "9\n02:03:01,000 --> 02:03:02,000\nHello\n\n10\n02:03:03,000 --> 02:03:04,000\nGoodbye\n";
        var result = SrtCues.ApplyTranslation(source, """
        ```json
        [{"index":10,"text":"Auf Wiedersehen"},{"index":9,"text":"Hallo"}]
        ```
        """);
        var before = SrtCues.Parse(source);
        var after = SrtCues.Parse(result);
        Assert.Equal(before.Select(c => (c.Index, c.Start, c.End)), after.Select(c => (c.Index, c.Start, c.End)));
        Assert.Equal("Hallo", after[0].Text);
        Assert.Equal("Auf Wiedersehen", after[1].Text);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"index\":1,\"text\":\"\"}]")]
    [InlineData("[{\"index\":1,\"text\":\"Hallo\"},{\"index\":1,\"text\":\"Hallo\"}]")]
    [InlineData("Sorry, I cannot translate this")]
    public void BrokenTranslationFailsInsteadOfSavingEnglishAsGerman(string reply)
        => Assert.Throws<InvalidOperationException>(() => SrtCues.ApplyTranslation("1\n00:00:01,000 --> 00:00:02,000\nHello", reply));

    [Fact]
    public void GrokQuoteIncludesPossibleEnglishTranslation()
    {
        var quote = SubtitleCost.Build(3600, "en", 0.10m / 60m, 1m, "grok-voice-transcribe-2.0", includeEnglishTranslation: true);
        Assert.Equal(0.10m, quote.WhisperUsd);
        Assert.True(quote.IncludesTranslation);
        Assert.Contains("Grok", quote.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveSyntheticSampleTranscribesTranslatesAndWritesGermanSidecar()
    {
        var configPath = Environment.GetEnvironmentVariable("JELLYFIN_TEST_GROK_CONFIG");
        var sample = Environment.GetEnvironmentVariable("JELLYFIN_TEST_SUBTITLE_SAMPLE");
        if (string.IsNullOrWhiteSpace(configPath) || string.IsNullOrWhiteSpace(sample))
        {
            Assert.Skip("Opt-in paid provider test: requires a private configuration and synthetic sample of at most 20 seconds.");
        }

        var config = JsonSerializer.Deserialize<PluginConfiguration>(await File.ReadAllTextAsync(configPath!, TestContext.Current.CancellationToken))!;
        var factory = new ClientFactory();
        var ai = new AiRecommender(factory, NullLogger<AiRecommender>.Instance, () => config);
        using var service = new AiSubtitleService(factory, ai, NullLogger<AiSubtitleService>.Instance, () => config);
        var quote = await service.QuoteAsync(sample!, "de", "Synthetic subtitle test", null, TestContext.Current.CancellationToken);
        Assert.InRange(quote.Seconds, 1, 20);
        Assert.InRange(quote.TotalUsd, 0, 0.05m);
        var result = await service.GenerateAsync(quote, TestContext.Current.CancellationToken);
        Assert.NotEmpty(SrtCues.Parse(result));
        Assert.Contains("Untertitel", result, StringComparison.OrdinalIgnoreCase);
        Assert.True(SubtitleFiles.TryRead(sample!, "de", out var saved));
        Assert.Equal(result, saved);
        // Repeating the request must reuse the saved file and incur no additional inference.
        Assert.Equal(result, await service.GenerateAsync(quote, TestContext.Current.CancellationToken));
    }

    private sealed class ClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
