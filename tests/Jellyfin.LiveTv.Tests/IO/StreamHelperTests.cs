using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.LiveTv.IO;
using Xunit;

namespace Jellyfin.LiveTv.Tests.IO;

public class StreamHelperTests
{
    [Fact]
    public async Task CopyToAsync_IdleTimeout_ThrowsTimeoutException()
    {
        var helper = new StreamHelper();
        await using var source = new NeverEndingStream();
        await using var destination = new MemoryStream();

        await Assert.ThrowsAsync<TimeoutException>(() =>
            helper.CopyToAsync(source, destination, 1024, null, TimeSpan.FromMilliseconds(80), CancellationToken.None));
    }

    [Fact]
    public async Task CopyToAsync_CopiesAvailableBytes()
    {
        var helper = new StreamHelper();
        var payload = new byte[] { 1, 2, 3, 4 };
        await using var source = new MemoryStream(payload);
        await using var destination = new MemoryStream();

        await helper.CopyToAsync(source, destination, 1024, null, TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.Equal(payload, destination.ToArray());
    }

    private sealed class NeverEndingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }
}
