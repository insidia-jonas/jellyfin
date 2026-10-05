using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TreasureMaps.Subtitles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TreasureMaps.Tests;

public sealed class SubtitleJobTests
{
    private static string Store() => Path.Combine(Path.GetTempPath(), "subtitle-jobs-test-" + Guid.NewGuid(), "jobs.json");
    private static SubtitleJobQueue Queue(string path, Func<SubtitleJob, Action<SubtitleProgress>, CancellationToken, Task> run)
        => new(path, run, NullLogger<SubtitleJobQueue>.Instance);

    [Fact]
    public async Task JobsAreDurableDeduplicatedSerialAndIndependentOfSubmittingRequest()
    {
        var path = Store();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var executions = 0;
        using var queue = Queue(path, async (_, progress, ct) =>
        {
            Interlocked.Increment(ref executions); progress(new("transcribing", 20, "Test")); started.TrySetResult();
            await release.Task.WaitAsync(ct);
        });
        try
        {
            await queue.StartAsync(CancellationToken.None);
            var owner = Guid.NewGuid(); var item = Guid.NewGuid();
            var first = queue.Enqueue(owner, item, new() { Path = "/private/movie", Language = "de" }, false);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(first.Id, queue.Enqueue(owner, item, new() { Language = "de" }, false).Id);
            queue.Enqueue(owner, Guid.NewGuid(), new(), false);
            Assert.Equal(1, executions);
            Assert.Contains("transcribing", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken), StringComparison.Ordinal);
            Assert.DoesNotContain("/private/movie", JsonSerializer.Serialize(queue.Snapshot().Single(j => j.Id == first.Id).View()), StringComparison.Ordinal);
            release.SetResult();
            await Until(() => queue.Snapshot().All(j => j.State == "completed"));
            Assert.Equal(2, executions);
            Assert.All(queue.Snapshot(), j => Assert.Equal(100, j.Percent));
        }
        finally { await queue.StopAsync(CancellationToken.None); Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public async Task RestartNeverAutomaticallyBillsInterruptedOrQueuedJobs()
    {
        var path = Store();
        using (var queue = Queue(path, (_, _, _) => Task.CompletedTask))
            queue.Enqueue(Guid.NewGuid(), Guid.NewGuid(), new(), false);
        var executions = 0;
        using var restored = Queue(path, (_, _, _) => { executions++; return Task.CompletedTask; });
        try
        {
            await restored.StartAsync(CancellationToken.None);
            Assert.Equal("interrupted", Assert.Single(restored.Snapshot()).State);
            Assert.Equal(0, executions);
        }
        finally { await restored.StopAsync(CancellationToken.None); Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public async Task CancellationStopsWorkerBeforeTheNextJobAndFailureDoesNotBlockQueue()
    {
        var path = Store(); var started = new TaskCompletionSource();
        using var queue = Queue(path, async (job, _, ct) =>
        {
            if (job.Quote.Title == "cancel") { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); }
            else throw new InvalidOperationException("Speech service returned HTTP 401. secret-key https://private.invalid");
        });
        try
        {
            await queue.StartAsync(CancellationToken.None);
            var first = queue.Enqueue(Guid.NewGuid(), Guid.NewGuid(), new() { Title = "cancel" }, false);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var second = queue.Enqueue(Guid.NewGuid(), Guid.NewGuid(), new(), false);
            Assert.True(queue.Cancel(first.Id));
            await Until(() => queue.Snapshot().All(j => !j.Active));
            Assert.Equal("cancelled", queue.Snapshot().Single(j => j.Id == first.Id).State);
            var failure = queue.Snapshot().Single(j => j.Id == second.Id);
            Assert.Equal("failed", failure.State); Assert.Contains("401", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("secret", failure.Message, StringComparison.Ordinal); Assert.DoesNotContain("private", failure.Message, StringComparison.Ordinal);
        }
        finally { await queue.StopAsync(CancellationToken.None); Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void QueueIsBoundedAndCorruptHistoryCannotBeOverwritten()
    {
        var path = Store();
        try
        {
            using var queue = Queue(path, (_, _, _) => Task.CompletedTask);
            for (var i = 0; i < 8; i++) queue.Enqueue(Guid.NewGuid(), Guid.NewGuid(), new(), false);
            Assert.Throws<InvalidOperationException>(() => queue.Enqueue(Guid.NewGuid(), Guid.NewGuid(), new(), false));
            File.WriteAllText(path, "broken");
            using var broken = Queue(path, (_, _, _) => Task.CompletedTask);
            Assert.NotNull(broken.StorageError);
            Assert.Throws<InvalidOperationException>(() => broken.Enqueue(Guid.NewGuid(), Guid.NewGuid(), new(), false));
            Assert.Equal("broken", File.ReadAllText(path));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    private static async Task Until(Func<bool> done)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!done()) await Task.Delay(10, timeout.Token);
    }
}
