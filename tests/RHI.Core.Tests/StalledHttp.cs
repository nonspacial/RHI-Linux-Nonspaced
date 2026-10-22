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

    public static HttpResponseMessage Stalled(int bytesFirst = 512, StallingStream? stream = null, string? etag = null, DateTimeOffset? lastModified = null)
    {
        var response = Response(stream ?? new StallingStream(bytesFirst));
        // A validator makes the app's GitHub API cache handler read (and keep) the body itself (#233 correction).
        if (etag is not null) response.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(etag);
        response.Content.Headers.LastModified = lastModified;
        return response;
    }

    /// <summary>A server that accepts the request and never answers (until the request's token is cancelled).</summary>
    public sealed class NeverAnswers : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

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

/// <summary>
/// Serves <c>chunks</c> chunks of <c>chunkSize</c> bytes (byte value = chunk index), pausing <c>gap</c> before each: real
/// time, or with a <see cref="ManualClock"/> the clock moves on by <c>gap</c> (no real wait) and the read then honours
/// its token as a socket read would.
/// </summary>
internal sealed class TrickleStream(int chunks, int chunkSize, TimeSpan gap, ManualClock? clock = null) : AsyncReadStream
{
    private int _sent;
    private int _offsetInChunk;

    public static byte[] Expected(int chunks, int chunkSize)
        => Enumerable.Range(0, chunks).SelectMany(i => Enumerable.Repeat((byte)i, chunkSize)).ToArray();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_sent >= chunks) return 0;
        if (_offsetInChunk == 0 && clock is not null)
        {
            clock.Advance(gap);
            cancellationToken.ThrowIfCancellationRequested();
        }
        else if (_offsetInChunk == 0) await Task.Delay(gap, cancellationToken);
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

/// <summary>
/// A clock that moves only when told (<see cref="Advance"/>): timers created on it fire synchronously once the clock
/// reaches their due time, so idle-limit tests never depend on how busy the machine is.
/// </summary>
internal sealed class ManualClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<Timer> _timers = [];
    private TimeSpan _now;

    public TimeSpan Elapsed { get { lock (_gate) return _now; } }

    /// <summary>Timers created and not yet disposed (each read's idle timer is disposed when the read ends).</summary>
    public int LiveTimers { get { lock (_gate) return _timers.Count; } }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Elapsed.Ticks;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + Elapsed;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        lock (_gate) _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<Timer> due;
        lock (_gate)
        {
            _now += by;
            due = _timers.Where(timer => timer.Due is { } at && at <= _now).ToList();
            foreach (var timer in due) timer.Due = null; // one-shot (an idle timer has no period)
        }
        foreach (var timer in due) timer.Fire();
    }

    private sealed class Timer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public TimeSpan? Due { get; set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate) Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
            return true;
        }

        public void Dispose()
        {
            lock (clock._gate) clock._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
