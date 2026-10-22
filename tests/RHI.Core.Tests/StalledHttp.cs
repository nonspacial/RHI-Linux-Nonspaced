using System.Net;

namespace RHI.Core.Tests;

/// <summary>
/// #233 fixtures: HTTP bodies that send a few bytes and then nothing (a stalled server), or that trickle small chunks
/// with a pause between them (slow but steady). Both honour the read's token, as a real socket read does.
/// </summary>
internal static class StalledHttp
{
    public static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    public static HttpResponseMessage Response(Stream body) => new(HttpStatusCode.OK) { Content = new StreamContent(body) };

    public static HttpResponseMessage Stalled(int bytesFirst = 512, StallingStream? stream = null)
        => Response(stream ?? new StallingStream(bytesFirst));

    /// <summary>Routes each request to an answer (null: the request fails as offline).</summary>
    public sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage?> route) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request.RequestUri!.AbsoluteUri);
            return route(request) is { } answer
                ? Task.FromResult(answer)
                : Task.FromException<HttpResponseMessage>(new HttpRequestException("offline test"));
        }
    }
}

/// <summary>Serves <c>bytesFirst</c> bytes, then waits for ever (until the read's token is cancelled).</summary>
internal sealed class StallingStream(int bytesFirst) : AsyncReadStream
{
    private int _served;
    private readonly TaskCompletionSource _stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once a read is waiting with nothing more to send.</summary>
    public Task Stalled => _stalled.Task;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_served < bytesFirst)
        {
            var n = Math.Min(buffer.Length, bytesFirst - _served);
            buffer.Span[..n].Fill(0x5A);
            _served += n;
            return n;
        }
        _stalled.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }
}

/// <summary>Serves <c>chunks</c> chunks of <c>chunkSize</c> bytes (byte value = chunk index), pausing <c>gap</c> before each.</summary>
internal sealed class TrickleStream(int chunks, int chunkSize, TimeSpan gap) : AsyncReadStream
{
    private int _sent;
    private int _offsetInChunk;

    public static byte[] Expected(int chunks, int chunkSize)
        => Enumerable.Range(0, chunks).SelectMany(i => Enumerable.Repeat((byte)i, chunkSize)).ToArray();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_sent >= chunks) return 0;
        if (_offsetInChunk == 0) await Task.Delay(gap, cancellationToken);
        var n = Math.Min(buffer.Length, chunkSize - _offsetInChunk);
        buffer.Span[..n].Fill((byte)_sent);
        _offsetInChunk += n;
        if (_offsetInChunk == chunkSize) { _sent++; _offsetInChunk = 0; }
        return n;
    }
}

/// <summary>A read-only body that serves async reads only (what HttpClient's content stream is used for).</summary>
internal abstract class AsyncReadStream : Stream
{
    public abstract override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("async reads only");
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
