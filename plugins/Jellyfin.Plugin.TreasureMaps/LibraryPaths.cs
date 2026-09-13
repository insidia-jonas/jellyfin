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
    /// Turns a SABnzbd history <c>storage</c> path (the job folder or a video file) into the
    /// Movies / TV library folder that should be scanned — typically the category directory
    /// above the job (<c>…/movies</c>, <c>…/tv</c>).
    /// </summary>
    /// <param name="storage">The history storage/path value, may be null.</param>
    /// <param name="configuredRoot">The configured movie/TV folder, used when storage sits under it.</param>
    /// <returns>The library root, or null when nothing can be resolved.</returns>
    public static string? LibraryRootFromCompletedPath(string? storage, string? configuredRoot = null)
    {
        var path = NormalizeExistingPath(storage);
        if (path is null)
        {
            return string.IsNullOrWhiteSpace(configuredRoot) ? null : TrimSlash(configuredRoot);
        }

        if (!string.IsNullOrWhiteSpace(configuredRoot) && IsUnderOrEqual(path, configuredRoot))
        {
            return TrimSlash(configuredRoot);
        }

        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent) && LooksLikeCategoryFolder(Path.GetFileName(parent)))
        {
            return parent;
        }

        if (LooksLikeCategoryFolder(Path.GetFileName(path)))
        {
            return path;
        }

        return string.IsNullOrWhiteSpace(parent) ? path : parent;
    }

    private static string? NormalizeExistingPath(string? storage)
    {
        if (string.IsNullOrWhiteSpace(storage))
        {
            return null;
        }

        var path = TrimSlash(storage);
        if (path.Length == 0)
        {
            return null;
        }

        if (IsLibraryVideo(path) || LooksLikeFile(path))
        {
            path = Path.GetDirectoryName(path) ?? path;
        }

        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static bool LooksLikeFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Length is > 1 and <= 5 && ext[0] == '.';
    }

    private static bool LooksLikeCategoryFolder(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return name.Trim().ToLowerInvariant() switch
        {
            "movies" or "movie" or "filme" or "film" or "films" => true,
            "tv" or "tvshows" or "tv-shows" or "series" or "shows" or "serien" => true,
            _ => false
        };
    }

    private static bool IsUnderOrEqual(string path, string root)
    {
        try
        {
            var fullPath = TrimSlash(Path.GetFullPath(path));
            var fullRoot = TrimSlash(Path.GetFullPath(root));
            if (string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var prefix = fullRoot + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string TrimSlash(string path)
        => path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

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
