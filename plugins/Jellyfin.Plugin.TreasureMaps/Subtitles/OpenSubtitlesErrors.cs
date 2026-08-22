using System;
using System.Text.Json;

namespace Jellyfin.Plugin.TreasureMaps.Subtitles;

/// <summary>
/// OpenSubtitles REST login/search error helpers. The API returns HTTP 400 when the
/// login field is an email address instead of the account username.
/// </summary>
public static class OpenSubtitlesErrors
{
    /// <summary>
    /// Whether the configured login looks like an email (rejected by <c>POST /login</c>).
    /// </summary>
    /// <param name="username">The configured username.</param>
    /// <returns>True when the value contains <c>@</c>.</returns>
    public static bool LooksLikeEmail(string? username)
        => !string.IsNullOrWhiteSpace(username) && username.Contains('@', StringComparison.Ordinal);

    /// <summary>
    /// Builds a user-facing login error, preferring the API JSON <c>message</c>.
    /// </summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <param name="body">The response body.</param>
    /// <param name="username">The username that was sent, for the email hint.</param>
    /// <returns>The message to show on the config page.</returns>
    public static string FormatHttpError(int statusCode, string? body, string? username = null)
    {
        var apiMessage = ReadMessage(body);
        if (LooksLikeEmail(username) || (apiMessage?.Contains("not your email", StringComparison.OrdinalIgnoreCase) ?? false)
            || (apiMessage?.Contains("username and not your email", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return "OpenSubtitles wants the account username from opensubtitles.com, not the email address. "
                   + "Open your profile there and paste the username (no @).";
        }

        if (!string.IsNullOrWhiteSpace(apiMessage))
        {
            return apiMessage;
        }

        return "HTTP " + statusCode + (string.IsNullOrWhiteSpace(body) ? string.Empty : ": " + body.Trim());
    }

    private static string? ReadMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString();
            }

            if (doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String)
            {
                return error.GetString();
            }
        }
        catch (JsonException)
        {
            // keep the raw body
        }

        return null;
    }
}
