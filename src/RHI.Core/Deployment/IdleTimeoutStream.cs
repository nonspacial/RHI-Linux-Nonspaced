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

    public IdleTimeoutStream(Stream inner, TimeSpan idleLimit, string source)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (idleLimit <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleLimit));
        _inner = inner;
        _idleLimit = idleLimit;
        _source = source;
    }

    /// <summary>
    /// Wraps a response body under <see cref="TrustedDownload.IdleLimit"/>; <paramref name="source"/> (the download
    /// policy's name) ends the stall message.
    /// </summary>
    public static Stream Wrap(Stream body, string source) => new IdleTimeoutStream(body, TrustedDownload.IdleLimit, source);

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
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(_idleLimit);
        try
        {
            return await _inner.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
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

    public DownloadStalledException(string source, TimeSpan idleLimit, Exception? inner = null)
        : base(MessageFor(source, idleLimit), inner)
    {
        DownloadSource = source;
        IdleLimit = idleLimit;
    }

    /// <summary>The download policy's name ("OptiScaler", "GitHub API").</summary>
    public string DownloadSource { get; }

    public TimeSpan IdleLimit { get; }

    public static string MessageFor(string source, TimeSpan idleLimit)
        => $"{MessagePrefix}{Math.Max(1, (int)Math.Ceiling(idleLimit.TotalSeconds))}{MessageMiddle}{source}";
}
