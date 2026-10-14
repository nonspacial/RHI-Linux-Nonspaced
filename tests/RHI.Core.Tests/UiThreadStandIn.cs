namespace RHI.Core.Tests;

/// <summary>
/// A one-thread UI stand-in for view-model tests. In the app every view-model continuation runs on Avalonia's UI thread;
/// in a plain test (xUnit's multi-thread context, or none) they run on several threads at once, so two writers can set
/// the status line concurrently, which the app never does. Work given to <see cref="RunAsync{T}"/> runs on this one
/// thread with this context current, so every await inside resumes here, as on the UI thread.
/// </summary>
internal sealed class UiThreadStandIn : SynchronizationContext, IDisposable
{
    private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly Thread _thread;

    public UiThreadStandIn()
    {
        _thread = new Thread(() =>
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable()) callback(state);
        }) { IsBackground = true, Name = "test-ui" };
        _thread.Start();
    }

    public int ThreadId => _thread.ManagedThreadId;

    public bool IsCurrent => Environment.CurrentManagedThreadId == ThreadId;

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    public void Post(Action action) => Post(_ => action(), null);

    public Task<T> RunAsync<T>(Func<Task<T>> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async _ =>
        {
            try { done.SetResult(await work()); }
            catch (Exception ex) { done.SetException(ex); }
        }, null);
        return done.Task;
    }

    public void Dispose() => _queue.CompleteAdding();
}
