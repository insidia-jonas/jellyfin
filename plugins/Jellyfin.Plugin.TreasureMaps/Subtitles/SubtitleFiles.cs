using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.TreasureMaps.Languages;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.TreasureMaps.Subtitles;

/// <summary>
/// Resolves a library video path and the <c>{name}.{lang}.srt</c> sidecar next to it.
/// </summary>
public static class SubtitleFiles
{
    /// <summary>
    /// Finds the playable video file for a library item.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The media path, or null.</returns>
    public static string? ResolveMediaPath(BaseItem? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Path))
        {
            return null;
        }

        if (File.Exists(item.Path))
        {
            return item.Path;
        }

        if (!Directory.Exists(item.Path))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(item.Path, "*", SearchOption.AllDirectories)
                .Where(LibraryPaths.IsLibraryVideo)
                .OrderByDescending(static f =>
                {
                    try
                    {
                        return new FileInfo(f).Length;
                    }
                    catch (Exception)
                    {
                        return 0L;
                    }
                })
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds the sidecar path <c>Movie.Name.de.srt</c> next to the video.
    /// </summary>
    /// <param name="mediaPath">The video path.</param>
    /// <param name="language">The target language.</param>
    /// <returns>The sidecar path.</returns>
    public static string SidecarPath(string mediaPath, string language)
    {
        var dir = Path.GetDirectoryName(mediaPath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(mediaPath);
        return Path.Combine(dir, name + "." + SanitizeLanguage(language) + ".srt");
    }

    /// <summary>
    /// Reads an existing sidecar when present.
    /// </summary>
    /// <param name="mediaPath">The video path.</param>
    /// <param name="language">The target language.</param>
    /// <param name="srt">The SRT text.</param>
    /// <returns>True when a non-empty sidecar exists.</returns>
    public static bool TryRead(string mediaPath, string language, out string srt)
    {
        srt = string.Empty;
        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            return false;
        }

        var path = SidecarPath(mediaPath, language);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            srt = File.ReadAllText(path);
            return !string.IsNullOrWhiteSpace(srt);
        }
        catch (Exception)
        {
            srt = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Writes a UTF-8 SRT sidecar next to the video.
    /// </summary>
    /// <param name="mediaPath">The video path.</param>
    /// <param name="language">The target language.</param>
    /// <param name="srt">The SRT text.</param>
    /// <returns>The sidecar path.</returns>
    public static string Write(string mediaPath, string language, string srt)
    {
        var path = SidecarPath(mediaPath, language);
        File.WriteAllText(path, srt ?? string.Empty);
        return path;
    }

    /// <summary>
    /// Normalizes a language token to a short filename-safe code.
    /// </summary>
    /// <param name="language">The raw language.</param>
    /// <returns>A 2–3 letter code.</returns>
    public static string SanitizeLanguage(string? language)
    {
        var normalized = LanguageMatcher.Normalize(language);
        if (string.IsNullOrEmpty(normalized))
        {
            normalized = string.IsNullOrWhiteSpace(language) ? "de" : language.Trim().ToLowerInvariant();
        }

        var safe = new string(normalized.Where(char.IsLetterOrDigit).Take(3).ToArray());
        return string.IsNullOrEmpty(safe) ? "de" : safe;
    }
}
