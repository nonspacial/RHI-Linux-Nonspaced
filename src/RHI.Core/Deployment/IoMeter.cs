namespace RHI.Core.Deployment;

/// <summary>
/// Test seam that counts the bytes the deployment toolkit reads and writes inside a scope
/// (AsyncLocal, so parallel tests do not see each other). Outside a scope streams are not wrapped.
/// </summary>
internal static class IoMeter
{
    private static readonly AsyncLocal<Scope?> Current = new();

    public static Scope Begin()
    {
        var scope = new Scope(Current.Value);
        Current.Value = scope;
        return scope;
    }

    public static Stream Wrap(Stream stream) => Current.Value is { } scope ? new CountingStream(stream, scope) : stream;

    public sealed class Scope : IDisposable
    {
        private readonly Scope? _parent;
        private long _read;
        private long _written;
        private int _opened;

        internal Scope(Scope? parent) => _parent = parent;

        public long BytesRead => Interlocked.Read(ref _read);
        public long BytesWritten => Interlocked.Read(ref _written);
        public int StreamsOpened => Volatile.Read(ref _opened);

        internal void AddRead(long count) { Interlocked.Add(ref _read, count); _parent?.AddRead(count); }
        internal void AddWritten(long count) { Interlocked.Add(ref _written, count); _parent?.AddWritten(count); }
        internal void AddOpened() { Interlocked.Increment(ref _opened); _parent?.AddOpened(); }

        public void Dispose() => Current.Value = _parent;
    }

    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;
        private readonly Scope _scope;

        public CountingStream(Stream inner, Scope scope)
        {
            _inner = inner;
            _scope = scope;
            scope.AddOpened();
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            _scope.AddRead(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = _inner.Read(buffer);
            _scope.AddRead(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            _scope.AddRead(read);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            _scope.AddWritten(count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _inner.Write(buffer);
            _scope.AddWritten(buffer.Length);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            _scope.AddWritten(buffer.Length);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
