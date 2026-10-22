namespace RHI.Core.Deployment;

/// <summary>
/// #233: a download body that gives up when no byte arrives for <see cref="TrustedDownload.IdleLimit"/> — the one idle limit of every
/// HTTP body RHI reads. Each read gets its own timer, so the limit is per gap, never a total deadline: a big file on a
/// slow but steady link still finishes. A stall throws <see cref="DownloadStalledException"/> (never an
/// <see cref="OperationCanceledException"/>, so it is never read as a user Cancel); the caller's own token still
/// cancels as before and wins when both happen at once.
/// </summary>
public sealed class IdleTimeoutStream : Stream
{
    private readonly Stream _inner;
    private readonly TimeSpan _idleLimit;
    private readonly string _source;
    private readonly TimeProvider _time;

    /// <param name="time">The clock the idle timer runs on (tests: a manual clock; default the system's).</param>
    public IdleTimeoutStream(Stream inner, TimeSpan idleLimit, string source, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (idleLimit <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleLimit));
        _inner = inner;
        _idleLimit = idleLimit;
        _source = source;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Wraps a response body under <see cref="TrustedDownload.IdleLimit"/>; <paramref name="source"/> (the download
    /// policy's name) ends the stall message.
    /// </summary>
    public static Stream Wrap(Stream body, string source) => new IdleTimeoutStream(body, TrustedDownload.IdleLimit, source, TrustedDownload.IdleClock);

    /// <summary>
    /// A whole text body under the idle limit (for readers outside <see cref="TrustedDownload"/>): decoded with the
    /// response's charset when it names one .NET knows, else UTF-8 (a BOM is honoured).
    /// </summary>
    public static async Task<string> ReadStringAsync(HttpContent content, string source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var encoding = System.Text.Encoding.UTF8;
        if (content.Headers.ContentType?.CharSet is { Length: > 0 } charset)
            try { encoding = System.Text.Encoding.GetEncoding(charset.Trim('"')); }
            catch (ArgumentException) { /* unknown charset: UTF-8, as the body's BOM may still say */ }
        await using var body = Wrap(await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), source);
        using var reader = new StreamReader(body, encoding, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // A new timer for every read: the limit is the gap since the last byte, never the time since the first.
        using var idle = new CancellationTokenSource(_idleLimit, _time);
        using var read = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, idle.Token);
        try
        {
            return await _inner.ReadAsync(buffer, read.Token).ConfigureAwait(false);
        }
        // Only the idle timer fired: the caller's token (a user Cancel, a shutdown) keeps its own exception.
        catch (Exception ex) when (idle.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new DownloadStalledException(_source, _idleLimit, ex);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// #233: a download stopped sending data for the idle limit and RHI gave up. A network failure
/// (<see cref="HttpRequestException"/>, as every existing "could not reach" rule expects), never a Cancel and never
/// "current". The message is fixed text first, the seconds, then the source's name last.
/// </summary>
public sealed class DownloadStalledException : HttpRequestException
{
    /// <summary>Fixed start of the message (PT-BR presentation pair); the whole seconds follow.</summary>
    public const string MessagePrefix = "The download stopped: no data arrived for ";

    /// <summary>Fixed text between the seconds and the source's name (PT-BR presentation pair).</summary>
    public const string MessageMiddle = " seconds. Source: ";

    /// <summary><see cref="MessageMiddle"/> after exactly one second (PT-BR presentation pair).</summary>
    public const string MessageMiddleOneSecond = " second. Source: ";

    public DownloadStalledException(string source, TimeSpan idleLimit, Exception? inner = null)
        : base(MessageFor(source, idleLimit), inner)
    {
        DownloadSource = source;
        IdleLimit = idleLimit;
    }

    /// <summary>The download policy's name ("OptiScaler", "GitHub API").</summary>
    public string DownloadSource { get; }

    public TimeSpan IdleLimit { get; }

    public static string MessageFor(string source, TimeSpan idleLimit) => MessagePrefix + SecondsThenSource(idleLimit, source);

    /// <summary>"60 seconds. Source: X" / "1 second. Source: X": whole seconds, rounded up, at least one.</summary>
    internal static string SecondsThenSource(TimeSpan limit, string source)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(limit.TotalSeconds));
        return $"{seconds}{(seconds == 1 ? MessageMiddleOneSecond : MessageMiddle)}{source}";
    }
}

/// <summary>
/// #233 correction: the app's <see cref="HttpClient.Timeout"/> ran out before the server's answer was complete (the wait
/// for its headers, or a body a handler reads inside <c>SendAsync</c>). .NET reports that as a
/// <see cref="TaskCanceledException"/> ("The request was canceled due to the configured HttpClient.Timeout …"), which is
/// neither a user Cancel nor plain text; RHI's readers turn it into this network failure with its own EN + PT-BR text.
/// </summary>
public sealed class DownloadTimedOutException : HttpRequestException
{
    /// <summary>Fixed start of the message (PT-BR presentation pair); the seconds and the source's name follow.</summary>
    public const string MessagePrefix = "The server took too long to answer: RHI stopped waiting after ";

    public DownloadTimedOutException(string source, TimeSpan timeout, Exception? inner = null)
        : base(MessageFor(source, timeout), inner)
    {
        DownloadSource = source;
        Timeout = timeout;
    }

    public string DownloadSource { get; }

    public TimeSpan Timeout { get; }

    public static string MessageFor(string source, TimeSpan timeout)
        => MessagePrefix + DownloadStalledException.SecondsThenSource(timeout, source);

    /// <summary>
    /// True when <paramref name="error"/> is <see cref="HttpClient"/>'s own timeout (its <see cref="TaskCanceledException"/>
    /// carries a <see cref="TimeoutException"/>) and not the caller's Cancel.
    /// </summary>
    public static bool IsClientTimeout(Exception error, CancellationToken callersToken)
        => error is TaskCanceledException { InnerException: TimeoutException } && !callersToken.IsCancellationRequested;
}
