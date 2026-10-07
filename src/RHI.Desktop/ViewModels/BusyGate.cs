namespace RHI.Desktop.ViewModels;

/// <summary>
/// Lets work that must run after the current busy operation (a library refresh after adding or
/// removing a manual game) wait for it instead of being skipped. The view model reports every busy
/// change; <see cref="WhenIdleAsync"/> completes once nothing is busy. Callers continue on their own
/// context, so a caller on the UI thread that sees idle can claim busy before anyone else runs.
/// </summary>
public sealed class BusyGate
{
    private TaskCompletionSource? _idle;

    public bool IsBusy { get; private set; }

    public void SetBusy(bool busy)
    {
        IsBusy = busy;
        if (busy) return;
        var idle = _idle;
        _idle = null;
        idle?.TrySetResult();
    }

    public async Task WhenIdleAsync(CancellationToken cancellationToken = default)
    {
        while (IsBusy)
        {
            _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await _idle.Task.WaitAsync(cancellationToken);
        }
    }
}
