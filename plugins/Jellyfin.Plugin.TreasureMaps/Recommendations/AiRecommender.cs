using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TreasureMaps.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TreasureMaps.Recommendations;

/// <summary>
/// A single AI title recommendation.
/// </summary>
public sealed class AiRecommendation
{
    /// <summary>Gets or sets the recommended title.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the release year, if known.</summary>
    [JsonPropertyName("year")]
    public int? Year { get; set; }

    /// <summary>Gets or sets the media type (<c>movie</c> or <c>tv</c>).</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "movie";

    /// <summary>Gets or sets a short reason why this fits the user's taste.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

/// <summary>
/// Talks to a configurable LLM provider (xAI Grok, OpenAI or Anthropic — or any
/// OpenAI-compatible gateway via the base-URL override) to turn a user's watch/favorite
/// history into title recommendations for the "For You" channel category.
/// </summary>
public class AiRecommender
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    // Recommendations are cached per user+history so browsing does not burn API tokens.
    private static readonly ConcurrentDictionary<string, (DateTimeOffset Expires, IReadOnlyList<AiRecommendation> Value)> _cache = new();

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AiRecommender> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AiRecommender"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public AiRecommender(IHttpClientFactory httpClientFactory, ILogger<AiRecommender> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Gets a value indicating whether the AI recommender is configured and enabled.
    /// </summary>
    public static bool IsEnabled =>
        Config.EnableForYou && !string.IsNullOrWhiteSpace(Config.AiApiKey);

    /// <summary>
    /// Gets recommendations for the given history, cached per user.
    /// </summary>
    /// <param name="cacheKey">A stable key for the requesting user.</param>
    /// <param name="watched">Recently watched titles ("Title (Year) [movie|tv]").</param>
    /// <param name="favorites">Favorite/downloaded titles.</param>
    /// <param name="count">How many recommendations to request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The recommendations (possibly empty).</returns>
    public async Task<IReadOnlyList<AiRecommendation>> GetRecommendationsAsync(
        string cacheKey,
        IReadOnlyList<string> watched,
        IReadOnlyList<string> favorites,
        int count,
        CancellationToken cancellationToken)
    {
        var key = cacheKey + "|" + ShortHash(string.Join(";", watched) + "#" + string.Join(";", favorites));
        if (_cache.TryGetValue(key, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
        {
            return cached.Value;
        }

        var prompt = BuildPrompt(watched, favorites, count);
        var text = await CompleteAsync(prompt, cancellationToken).ConfigureAwait(false);
        var recommendations = ParseRecommendations(text);

        if (recommendations.Count > 0)
        {
            var hours = Math.Max(1, Config.ForYouCacheHours);
            _cache[key] = (DateTimeOffset.UtcNow.AddHours(hours), recommendations);
        }

        return recommendations;
    }

    /// <summary>
    /// Sends a raw prompt to the configured provider and returns the model's text reply.
    /// Also used by the connection test.
    /// </summary>
    /// <param name="prompt">The user prompt.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The model reply text.</returns>
    public async Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken)
    {
        var config = Config;
        var provider = (config.AiProvider ?? "openai").Trim().ToLowerInvariant();

        return provider switch
        {
            "anthropic" => await CompleteAnthropicAsync(config, prompt, cancellationToken).ConfigureAwait(false),
            _ => await CompleteOpenAiStyleAsync(config, provider, prompt, cancellationToken).ConfigureAwait(false)
        };
    }

    /// <summary>
    /// Builds the recommendation prompt from the user's history.
    /// </summary>
    /// <param name="watched">Recently watched titles.</param>
    /// <param name="favorites">Favorite titles.</param>
    /// <param name="count">How many recommendations to request.</param>
    /// <returns>The prompt.</returns>
    public static string BuildPrompt(IReadOnlyList<string> watched, IReadOnlyList<string> favorites, int count)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a movie and TV recommendation engine for a private media server.");
        sb.AppendLine("Based on this user's complete history, recommend titles they would love next.");
        sb.AppendLine();
        sb.AppendLine("Watched:");
        sb.AppendLine(watched.Count > 0 ? string.Join("\n", watched.Select(t => "- " + t)) : "- (nothing yet)");
        sb.AppendLine();
        sb.AppendLine("Favorites / downloaded:");
        sb.AppendLine(favorites.Count > 0 ? string.Join("\n", favorites.Select(t => "- " + t)) : "- (none)");
        sb.AppendLine();
        sb.Append("Recommend exactly ").Append(count).AppendLine(" titles: a mix of movies and TV shows matching the user's taste.");
        sb.AppendLine("Do NOT recommend titles already listed above. Prefer well-known, obtainable releases.");
        sb.AppendLine("Reply with ONLY a JSON array, no other text, in this shape:");
        sb.AppendLine("[{\"title\":\"...\",\"year\":2024,\"type\":\"movie\",\"reason\":\"one short sentence\"}]");
        return sb.ToString();
    }

    /// <summary>
    /// Extracts the JSON recommendation array from a model reply (tolerates markdown fences and
    /// surrounding prose).
    /// </summary>
    /// <param name="text">The raw model reply.</param>
    /// <returns>The parsed recommendations (empty when nothing parseable).</returns>
    public static IReadOnlyList<AiRecommendation> ParseRecommendations(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<AiRecommendation>();
        }

        var start = text.IndexOf('[', StringComparison.Ordinal);
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start)
        {
            return Array.Empty<AiRecommendation>();
        }

        try
        {
            var list = JsonSerializer.Deserialize<List<AiRecommendation>>(text[start..(end + 1)], _jsonOptions);
            return (IReadOnlyList<AiRecommendation>?)list?.Where(r => !string.IsNullOrWhiteSpace(r.Title)).ToList()
                ?? Array.Empty<AiRecommendation>();
        }
        catch (JsonException)
        {
            return Array.Empty<AiRecommendation>();
        }
    }

    private async Task<string> CompleteOpenAiStyleAsync(PluginConfiguration config, string provider, string prompt, CancellationToken cancellationToken)
    {
        var baseUrl = !string.IsNullOrWhiteSpace(config.AiBaseUrl)
            ? config.AiBaseUrl.TrimEnd('/')
            : provider == "grok" ? "https://api.x.ai/v1" : "https://api.openai.com/v1";
        var model = !string.IsNullOrWhiteSpace(config.AiModel)
            ? config.AiModel
            : provider == "grok" ? "grok-3-mini" : "gpt-4o-mini";

        var payload = new
        {
            model,
            messages = new[] { new { role = "user", content = prompt } },
            temperature = 0.7
        };

        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(60);
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions");
        request.Headers.Add("Authorization", "Bearer " + config.AiApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"AI provider returned {(int)response.StatusCode}: {Truncate(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
    }

    private async Task<string> CompleteAnthropicAsync(PluginConfiguration config, string prompt, CancellationToken cancellationToken)
    {
        var baseUrl = !string.IsNullOrWhiteSpace(config.AiBaseUrl)
            ? config.AiBaseUrl.TrimEnd('/')
            : "https://api.anthropic.com/v1";
        var model = !string.IsNullOrWhiteSpace(config.AiModel) ? config.AiModel : "claude-3-5-haiku-latest";

        var payload = new
        {
            model,
            max_tokens = 2048,
            messages = new[] { new { role = "user", content = prompt } }
        };

        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(60);
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/messages");
        request.Headers.Add("x-api-key", config.AiApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"AI provider returned {(int)response.StatusCode}: {Truncate(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        var parts = doc.RootElement.GetProperty("content");
        var sb = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var t))
            {
                sb.Append(t.GetString());
            }
        }

        return sb.ToString();
    }

    private static string Truncate(string value)
        => value.Length <= 300 ? value : value[..300];

    private static string ShortHash(string value)
    {
        var bytes = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, 8);
    }
}
