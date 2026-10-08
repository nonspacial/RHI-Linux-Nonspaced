using System.Collections.Concurrent;

namespace RHI.Core.Tests;

/// <summary>
/// Records every request sent after <see cref="CloseGate"/> (call it from <see cref="CancelAtWritePhase.RunAsync{T}"/>'s
/// <c>atWritePhase</c>): a deployment downloads everything before its first change, so once the write gate closed the
/// list stays empty (w2 merge review, minor 1: a download there cannot be cancelled and a network failure means a full
/// rollback of files already changed).
/// </summary>
internal sealed class GateWatchingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private volatile bool _gateClosed;

    public ConcurrentQueue<string> LateRequests { get; } = new();

    public void CloseGate() => _gateClosed = true;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_gateClosed) LateRequests.Enqueue(request.RequestUri?.AbsoluteUri ?? "?");
        return base.SendAsync(request, cancellationToken);
    }
}
