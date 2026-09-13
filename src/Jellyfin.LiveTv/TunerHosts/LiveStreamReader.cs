using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.LiveTv.TunerHosts;

/// <summary>
/// Forwards a live-stream file and notifies the owner when the client disconnects.
/// </summary>
internal sealed class LiveStreamReader : Stream
{
    private readonly Stream _inner;
    private readonly Action _onDisposed;
    private int _disposed;

    public LiveStreamReader(Stream inner, Action onDisposed)
    {
        _inner = inner;
        _onDisposed = onDisposed;
    }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush() => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _inner.Read(buffer);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (disposing)
        {
            _inner.Dispose();
            _onDisposed();
        }

        base.Dispose(disposing);
    }
}
