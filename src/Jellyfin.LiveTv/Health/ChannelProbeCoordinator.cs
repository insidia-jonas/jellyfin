using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.LiveTv.Health;

/// <summary>Gives viewers and recordings priority over one server-wide idle probe.</summary>
[SuppressMessage("Design", "CA1001", Justification = "Probe leases are owned and disposed by the worker after its media process exits.")]
[SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP006:Implement IDisposable", Justification = "The worker owns and disposes probe leases only after the media process exits.")]
public sealed class ChannelProbeCoordinator
{
    private readonly object _sync = new();
    private readonly TimeProvider _clock;
    private int _playbacks;
    private DateTimeOffset _lastPlayback;
    private ProbeLease? _probe;
    private bool _probesEnabled = true;

    /// <summary>Initializes a new instance of the <see cref="ChannelProbeCoordinator"/> class.</summary>
    public ChannelProbeCoordinator()
        : this(TimeProvider.System)
    {
    }

    internal ChannelProbeCoordinator(TimeProvider clock) => _clock = clock;

    internal void SetProbesEnabled(bool enabled)
    {
        lock (_sync)
        {
            _probesEnabled = enabled;
            // Configuration changes preempt old-source work too. Keep the lease
            // until the decoder actually exits, exactly as for foreground demand.
            _probe?.Cancellation.Cancel();
        }
    }

    internal async Task<IDisposable> AcquirePlayback(CancellationToken cancellationToken)
    {
        ProbeLease? probe;
        Task cancel;
        lock (_sync)
        {
            _playbacks++;
            probe = _probe;
            // Mark foreground demand before cancellation, preventing a new probe racing in.
            cancel = probe?.Cancellation.CancelAsync() ?? Task.CompletedTask;
        }

        var lease = new PlaybackLease(this);
        try
        {
            await cancel.ConfigureAwait(false);
            if (probe is not null)
            {
                // The probe signals only after its process/connection has actually closed.
                await probe.Finished.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    internal ProbeLease? TryAcquireProbe()
    {
        lock (_sync)
        {
            if (!_probesEnabled || _playbacks != 0 || _probe is not null || _clock.GetUtcNow() - _lastPlayback < TimeSpan.FromMinutes(2))
            {
                return null;
            }

            _probe = new ProbeLease(this);
            return _probe;
        }
    }

    internal sealed class ProbeLease(ChannelProbeCoordinator owner) : IDisposable
    {
        internal CancellationTokenSource Cancellation { get; } = new();

        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose()
        {
            lock (owner._sync)
            {
                if (ReferenceEquals(owner._probe, this))
                {
                    owner._probe = null;
                    Finished.TrySetResult();
                    Cancellation.Dispose();
                }
            }
        }
    }

    private sealed class PlaybackLease(ChannelProbeCoordinator owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                lock (owner._sync)
                {
                    owner._playbacks--;
                    owner._lastPlayback = owner._clock.GetUtcNow();
                }
            }
        }
    }
}
