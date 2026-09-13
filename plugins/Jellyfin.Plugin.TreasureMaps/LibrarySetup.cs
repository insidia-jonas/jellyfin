using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TreasureMaps;

/// <summary>
/// Creates or extends the Jellyfin Movies / TV Shows libraries so SABnzbd's completed
/// folders are actually scanned.
/// </summary>
public static class LibrarySetup
{
    /// <summary>
    /// Finds an existing virtual folder by collection type, then by name.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="name">The preferred folder name (e.g. Movies).</param>
    /// <param name="collectionType">The collection type.</param>
    /// <returns>The folder, or null.</returns>
    public static VirtualFolderInfo? Find(ILibraryManager libraryManager, string name, CollectionTypeOptions collectionType)
    {
        var folders = libraryManager.GetVirtualFolders();
        return folders.FirstOrDefault(v => v.CollectionType == collectionType)
            ?? folders.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Creates the library or adds <paramref name="path"/> when it is missing.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="name">The library name to create when none exists.</param>
    /// <param name="collectionType">Movies or TV Shows.</param>
    /// <param name="path">The folder SABnzbd writes into.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>created, path added, already configured, or skipped.</returns>
    public static async Task<string> EnsureAsync(
        ILibraryManager libraryManager,
        string name,
        CollectionTypeOptions collectionType,
        string path,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "skipped";
        }

        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not create library folder {Path}", path);
        }

        var existing = Find(libraryManager, name, collectionType);
        if (existing is null)
        {
            var options = new LibraryOptions
            {
                PathInfos = new[] { new MediaPathInfo(path) },
                EnableRealtimeMonitor = true
            };
            await libraryManager.AddVirtualFolder(name, collectionType, options, true).ConfigureAwait(false);
            logger.LogInformation("Created Jellyfin library '{Name}' -> {Path}", name, path);
            return "created";
        }

        if (existing.Locations?.Any(l => string.Equals(l, path, StringComparison.OrdinalIgnoreCase)) == true)
        {
            return "already configured";
        }

        try
        {
            libraryManager.AddMediaPath(existing.Name, new MediaPathInfo(path));
            logger.LogInformation("Added {Path} to existing Jellyfin library '{Name}'", path, existing.Name);
            return "path added";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not add {Path} to library '{Name}'", path, existing.Name);
            return "add failed";
        }
    }
}
