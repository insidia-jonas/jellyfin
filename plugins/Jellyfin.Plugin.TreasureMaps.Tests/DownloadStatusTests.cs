using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TreasureMaps.Tests;

public class DownloadStatusTests
{
    [Fact]
    public async Task MultipleBrowsersAndImportWorkerShareOneSnapshotEvenWhenOneLeaves()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var transport = new Transport(async request =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task;
            return request.RequestUri!.Query.Contains("mode=queue", StringComparison.Ordinal)
                ? """{"queue":{"speed":"0","slots":[]}}"""
                : """{"history":{"slots":[{"nzo_id":"completed","name":"Film","status":"Completed","storage":"/media/Film"}]}}""";
        });
        var client = new SabnzbdClient(transport, NullLogger<SabnzbdClient>.Instance);
        using var cancelled = new CancellationTokenSource();
        var first = client.GetDownloadStatusAsync(cancelled.Token);
        await started.Task;
        var others = Enumerable.Range(0, 16).Select(_ => client.GetDownloadStatusAsync(TestContext.Current.CancellationToken)).ToArray();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first);
        release.SetResult();
        foreach (var result in await Task.WhenAll(others)) { Assert.Equal("Completed", Assert.Single(result.Items).Status); }
        await client.GetDownloadStatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task AuthenticationErrorCannotBeCachedAsAnEmptyQueue()
    {
        using var transport = new Transport(_ => Task.FromResult("""{"error":"API Key Incorrect"}"""));
        var client = new SabnzbdClient(transport, NullLogger<SabnzbdClient>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetDownloadStatusAsync(TestContext.Current.CancellationToken));
        Assert.Null(client.LastDownloadStatus);
    }

    private sealed class Transport(Func<HttpRequestMessage, Task<string>> reply) : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("http://sab.test") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => new(HttpStatusCode.OK) { Content = new StringContent(await reply(request)) };
    }
}
