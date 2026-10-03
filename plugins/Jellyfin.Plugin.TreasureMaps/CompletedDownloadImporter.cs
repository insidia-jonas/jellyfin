using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.TreasureMaps;

/// <summary>Discovers one completed download before queuing its metadata enrichment.</summary>
public sealed class CompletedDownloadImporter
{
    private readonly ILibraryManager _library;
    private readonly IProviderManager _providers;
    private readonly IFileSystem _files;

    /// <summary>Initializes a new instance of the <see cref="CompletedDownloadImporter"/> class.</summary>
    public CompletedDownloadImporter(ILibraryManager library, IProviderManager providers, IFileSystem files)
    {
        _library = library;
        _providers = providers;
        _files = files;
    }

    /// <summary>Returns true only after the completed path has been discovered in Jellyfin.</summary>
    public async Task<bool> ImportAsync(string? storage, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storage) || (!Directory.Exists(storage) && !File.Exists(storage)))
        {
            return false;
        }

        var paths = File.Exists(storage) ? new[] { storage } : Directory.EnumerateFiles(storage, "*", SearchOption.AllDirectories).Where(LibraryPaths.IsLibraryVideo).ToArray();
        var imported = paths.Length > 0;
        foreach (var path in paths)
        {
            imported &= await ImportFileAsync(Path.GetFullPath(path), cancellationToken).ConfigureAwait(false);
        }

        return imported;
    }

    private async Task<bool> ImportFileAsync(string path, CancellationToken cancellationToken)
    {
        var item = _library.FindByPath(path, null);
        var discovery = new MetadataRefreshOptions(new DirectoryService(_files))
        {
            MetadataRefreshMode = MetadataRefreshMode.None,
            ImageRefreshMode = MetadataRefreshMode.None,
            IsAutomated = true
        };

        var discovered = new HashSet<Guid>();
        // Discover one level at a time. Never recurse through unrelated movie/show roots.
        while (item is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ancestor = Path.GetDirectoryName(path);
            Folder? folder = null;
            while (!string.IsNullOrEmpty(ancestor))
            {
                folder = _library.FindByPath(ancestor, true) as Folder;
                if (folder is not null) { break; }
                ancestor = Path.GetDirectoryName(ancestor);
            }

            if (folder is null or AggregateFolder || !discovered.Add(folder.Id)) { return false; }
            await folder.ValidateChildren(new Progress<double>(), discovery, recursive: false, cancellationToken: cancellationToken).ConfigureAwait(false);
            item = _library.FindByPath(path, null);
            if (item is null)
            {
                var next = Path.GetRelativePath(ancestor!, path).Split(Path.DirectorySeparatorChar)[0];
                var child = _library.FindByPath(Path.Combine(ancestor!, next), null);
                if (child is null || child.Id == folder.Id) { return false; }
                // Movie folders can resolve directly to a video whose Path is the media file.
                if (child is not Folder) { item = child; }
            }
        }

        if (item is Folder completedFolder)
        {
            await completedFolder.ValidateChildren(new Progress<double>(), discovery, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        _providers.QueueRefresh(item.Id, new MetadataRefreshOptions(new DirectoryService(_files)) { IsAutomated = true }, RefreshPriority.High);
        return true;
    }
}
