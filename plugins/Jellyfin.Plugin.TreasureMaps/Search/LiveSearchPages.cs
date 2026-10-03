using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TreasureMaps.Api;
using Jellyfin.Plugin.TreasureMaps.Channels;

namespace Jellyfin.Plugin.TreasureMaps.Search;

/// <summary>Fetches useful search pages on demand within one overall deadline.</summary>
public static class LiveSearchPages
{
    /// <summary>Gets a bounded search result. A slow kind must not discard the other kind.</summary>
    /// <param name="query">The title query.</param>
    /// <param name="take">Desired distinct matching titles.</param>
    /// <param name="fetch">Fetches one page of at most 100 releases.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    /// <param name="timeout">Optional test deadline; defaults to six seconds.</param>
    /// <returns>The successful releases.</returns>
    public static async Task<IReadOnlyList<Release>> FetchAsync(
        string query,
        int take,
        Func<string, int, CancellationToken, Task<(IReadOnlyList<Release> Items, bool Ok)>> fetch,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(6));
        var all = new List<Release>();
        var parsed = TreasureMapsSearch.ParseQuery(query);
        var kinds = parsed.Kind is null ? new[] { "movie", "tv" } : new[] { parsed.Kind };
        var first = kinds.Select(kind => Run(kind, 0)).ToArray();
        await WaitForPages(first).ConfigureAwait(false);
        var completed = first.Where(t => t.IsCompletedSuccessfully).Select(t => t.Result).ToList();
        all.AddRange(completed.SelectMany(p => p.Items));

        var matches = ReleaseGrouper.Group(all.Where(r => TreasureMapsSearch.ScoreRelease(r, query) > 0)).Count;
        if (!deadline.IsCancellationRequested && matches < take)
        {
            var more = completed.Where(p => p.Ok && p.Items.Count >= 100).Select(p => Run(p.Kind, 100)).ToArray();
            await WaitForPages(more).ConfigureAwait(false);
            all.AddRange(more.Where(t => t.IsCompletedSuccessfully).SelectMany(t => t.Result.Items));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!completed.Any(p => p.Ok) && all.Count == 0)
        {
            throw new System.Net.Http.HttpRequestException("Treasure-Maps search is temporarily unavailable. Please retry.");
        }

        return all.DistinctBy(r => r.Guid).ToList();

        async Task<(string Kind, IReadOnlyList<Release> Items, bool Ok)> Run(string kind, int offset)
        {
            try
            {
                var page = await fetch(kind, offset, deadline.Token).ConfigureAwait(false);
                return (kind, page.Items, page.Ok);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                return (kind, Array.Empty<Release>(), false);
            }
        }

        async Task WaitForPages(Task<(string Kind, IReadOnlyList<Release> Items, bool Ok)>[] tasks)
        {
            try { await Task.WhenAll(tasks).WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        }
    }
}
