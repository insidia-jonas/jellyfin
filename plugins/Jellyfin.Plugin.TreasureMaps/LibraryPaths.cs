using System;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.TreasureMaps;

/// <summary>
/// Resolves SABnzbd category folders into the Jellyfin Movies / TV Shows library paths and
/// inspects those folders for video files.
/// </summary>
public static class LibraryPaths
{
    private static readonly string[] VideoExtensions =
    {
        ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".ts", ".m2ts", ".wmv", ".webm", ".mpg", ".mpeg"
    };

    /// <summary>
    /// Resolves a configured folder (absolute, or relative to SABnzbd's complete dir).
    /// </summary>
    /// <param name="folder">The configured folder (e.g. <c>movies</c> or an absolute path).</param>
    /// <param name="category">Fallback when <paramref name="folder"/> is empty.</param>
    /// <param name="completeDir">SABnzbd <c>misc.complete_dir</c>.</param>
    /// <returns>The absolute path, or null when it cannot be resolved.</returns>
    public static string? Resolve(string? folder, string? category, string? completeDir)
    {
        var value = !string.IsNullOrWhiteSpace(folder) ? folder : category;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Path.IsPathRooted(value))
        {
            return value;
        }

        return string.IsNullOrWhiteSpace(completeDir) ? null : Path.Combine(completeDir, value);
    }

    /// <summary>
    /// Counts video files under a folder (skips names containing "sample", matching Jellyfin).
    /// </summary>
    /// <param name="path">The folder to inspect.</param>
    /// <returns>The number of video files, or -1 when the folder does not exist.</returns>
    public static int CountVideos(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return -1;
        }

        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Count(IsLibraryVideo);
        }
        catch (Exception)
        {
            return -1;
        }
    }

    /// <summary>
    /// Returns true when Jellyfin would treat this file as a library video.
    /// </summary>
    /// <param name="filePath">The file path.</param>
    /// <returns>True when the file looks like a playable video (not a sample).</returns>
    public static bool IsLibraryVideo(string filePath)
    {
        var name = Path.GetFileName(filePath);
        if (name.Contains("sample", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var ext = Path.GetExtension(filePath);
        return VideoExtensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase));
    }
}
