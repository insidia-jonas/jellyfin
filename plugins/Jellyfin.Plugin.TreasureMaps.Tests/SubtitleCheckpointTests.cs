using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TreasureMaps.Configuration;
using Jellyfin.Plugin.TreasureMaps.Recommendations;
using Jellyfin.Plugin.TreasureMaps.Subtitles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TreasureMaps.Tests;

public sealed class SubtitleCheckpointTests
{
    [Fact]
    public async Task TranslationFailureReusesSpeechAndCompletedTranslationsWithoutBillingThemAgain()
    {
        var dir = Path.Combine(Path.GetTempPath(), "subtitle-checkpoint-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "sample.wav");
        try
        {
            using var ffmpeg = new Process { StartInfo = new ProcessStartInfo(MediaProbe.FindTool("ffmpeg")!) { UseShellExecute = false, RedirectStandardError = true } };
            foreach (var arg in new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "sine=duration=2", path }) ffmpeg.StartInfo.ArgumentList.Add(arg);
            ffmpeg.Start();
            await ffmpeg.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, ffmpeg.ExitCode);
            var config = new PluginConfiguration { EnableAiSubtitles = true, AiProvider = "grok", AiApiKey = "fake-no-network" };
            using var handler = new Provider();
            var factory = new Factory(handler);
            var ai = new AiRecommender(factory, NullLogger<AiRecommender>.Instance, () => config);
            using var service = new AiSubtitleService(factory, ai, NullLogger<AiSubtitleService>.Instance, () => config);
            var quote = new SubtitleQuote { Path = path, Language = "de", Chunks = 1, Seconds = 2, IncludesTranslation = true };
            await Assert.ThrowsAsync<TimeoutException>(() => service.GenerateAsync(quote, TestContext.Current.CancellationToken));
            Assert.False(SubtitleFiles.TryRead(path, "de", out _));
            var result = await service.GenerateAsync(quote, TestContext.Current.CancellationToken);
            Assert.Equal(1, handler.SpeechCalls);
            Assert.Equal(2, handler.TranslationCalls);
            Assert.Contains("Hallo", result, StringComparison.Ordinal);
            Assert.True(SubtitleFiles.TryRead(path, "de", out _));
            Assert.Equal(result, await service.GenerateAsync(quote, TestContext.Current.CancellationToken));
            Assert.Equal(2, handler.TranslationCalls);
        }
        finally { Directory.Delete(dir, true); }
    }

    private sealed class Factory(Provider handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }

    private sealed class Provider : HttpMessageHandler
    {
        public int SpeechCalls { get; private set; }
        public int TranslationCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body;
            if (request.RequestUri!.AbsolutePath.EndsWith("/stt", StringComparison.Ordinal))
            {
                SpeechCalls++;
                body = """{"language":"en","words":[{"text":"Hello","start":0.1,"end":1.5}]}""";
            }
            else
            {
                TranslationCalls++;
                if (TranslationCalls == 1) throw new TimeoutException("Synthetic provider timeout");
                body = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = "[{\"index\":1,\"text\":\"Hallo\"}]" } } } });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
