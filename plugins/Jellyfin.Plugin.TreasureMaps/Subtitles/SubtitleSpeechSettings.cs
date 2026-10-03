using System;
using Jellyfin.Plugin.TreasureMaps.Configuration;

namespace Jellyfin.Plugin.TreasureMaps.Subtitles;

/// <summary>Selects the speech API without sending chat credentials to another provider.</summary>
public sealed record SubtitleSpeechSettings(string Url, string Key, string Model, bool IsGrok, decimal UsdPerMinute)
{
    /// <summary>Resolves explicit speech settings or uses the configured Grok account.</summary>
    public static SubtitleSpeechSettings Resolve(PluginConfiguration config, bool englishTranslation)
    {
        var grok = config.SubtitleSpeechProvider == "grok"
            || (config.SubtitleSpeechProvider == "auto" && config.AiProvider == "grok"
                && string.IsNullOrWhiteSpace(config.WhisperApiKey) && string.IsNullOrWhiteSpace(config.WhisperBaseUrl));
        if (grok)
        {
            if (string.IsNullOrWhiteSpace(config.AiApiKey)) { throw new InvalidOperationException("Für Grok-Untertitel fehlt der xAI-API-Schlüssel."); }
            return new SubtitleSpeechSettings("https://api.x.ai/v1/stt", config.AiApiKey,
                string.IsNullOrWhiteSpace(config.GrokSpeechModel) ? "grok-voice-transcribe-2.0" : config.GrokSpeechModel,
                true, config.GrokSpeechUsdPerHour / 60m);
        }

        var key = config.WhisperApiKey;
        if (string.IsNullOrWhiteSpace(key) && config.AiProvider == "openai"
            && string.IsNullOrWhiteSpace(config.AiBaseUrl) && string.IsNullOrWhiteSpace(config.WhisperBaseUrl))
        {
            key = config.AiApiKey;
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("Für diesen Spracherkennungsanbieter ist ein eigener API-Schlüssel erforderlich. Grok-Schlüssel werden nicht an Whisper gesendet.");
        }

        var root = string.IsNullOrWhiteSpace(config.WhisperBaseUrl) ? "https://api.openai.com/v1" : config.WhisperBaseUrl.TrimEnd('/');
        if (!root.EndsWith("/audio/transcriptions", StringComparison.OrdinalIgnoreCase) && !root.EndsWith("/audio/translations", StringComparison.OrdinalIgnoreCase))
        {
            root += englishTranslation ? "/audio/translations" : "/audio/transcriptions";
        }

        return new SubtitleSpeechSettings(root, key, string.IsNullOrWhiteSpace(config.WhisperModel) ? "whisper-1" : config.WhisperModel, false, config.WhisperUsdPerMinute);
    }
}
