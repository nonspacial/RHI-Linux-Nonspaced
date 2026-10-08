using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// The one owner of the window's busy state (Refs #76, #77). An operation that changes game files, the library or
/// long-running state claims the gate with <see cref="TryBegin"/> and holds it until its <see cref="OperationScope"/> is
/// disposed; while one is held every other claim is refused (null), so two operations can never clear each other's
/// gate and the overlay can never drop while one is still writing. Each operation gets its own cancellation token,
/// cancelled by <see cref="CancelCommand"/> (the Cancel button in the busy overlay). Work that must run after the
/// current operation (a library refresh after an edit) waits with <see cref="WhenIdleAsync"/>. UI thread only.
/// </summary>
public sealed partial class OperationCoordinator : ObservableObject
{
    private OperationScope? _current;
    private TaskCompletionSource? _idle;

    /// <summary>True while an operation holds the gate.</summary>
    public bool IsBusy => _current is not null;

    /// <summary>What the running operation is (diagnostics and tests), or null when idle.</summary>
    public string? CurrentKind => _current?.Kind;

    /// <summary>True while the running operation can be cancelled and has not been asked to stop yet.</summary>
    public bool CanCancel => _current is { Cancellable: true, CancelRequested: false };

    /// <summary>
    /// Claims the gate for <paramref name="kind"/>; null when another operation holds it. The caller disposes the scope
    /// when it is done (a <c>using</c>); disposing twice, or disposing a scope that no longer owns the gate, does nothing.
    /// </summary>
    /// <param name="linkedToken">A token that also cancels the operation (a dialog's own token, the background loop's).</param>
    public OperationScope? TryBegin(string kind, bool cancellable = true, CancellationToken linkedToken = default)
    {
        if (_current is not null) return null;
        var scope = new OperationScope(this, kind, cancellable, linkedToken);
        _current = scope;
        RaiseStateChanged();
        return scope;
    }

    /// <summary>Asks the running operation to stop; its token is cancelled and it ends on its own.</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    public void Cancel()
    {
        if (_current is not { Cancellable: true } scope || scope.CancelRequested) return;
        scope.RequestCancel();
        RaiseStateChanged();
    }

    /// <summary>Completes once no operation holds the gate (immediately when idle).</summary>
    public async Task WhenIdleAsync(CancellationToken cancellationToken = default)
    {
        while (_current is not null)
        {
            _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await _idle.Task.WaitAsync(cancellationToken);
        }
    }

    internal void End(OperationScope scope)
    {
        if (!ReferenceEquals(_current, scope)) return; // a stale scope never clears the current owner's gate
        _current = null;
        RaiseStateChanged();
        var idle = _idle;
        _idle = null;
        idle?.TrySetResult();
    }

    private void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CurrentKind));
        OnPropertyChanged(nameof(CanCancel));
        CancelCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>One claim on the <see cref="OperationCoordinator"/>'s gate; dispose it to release the gate.</summary>
public sealed class OperationScope : IDisposable
{
    private readonly OperationCoordinator _owner;
    private readonly CancellationTokenSource _cts;
    private bool _disposed;

    internal OperationScope(OperationCoordinator owner, string kind, bool cancellable, CancellationToken linkedToken)
    {
        _owner = owner;
        Kind = kind;
        Cancellable = cancellable;
        _cts = linkedToken.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(linkedToken) : new CancellationTokenSource();
        Token = _cts.Token; // read once: work that outlives the scope may still check it after Dispose
    }

    public string Kind { get; }
    public bool Cancellable { get; }
    public bool CancelRequested { get; private set; }

    /// <summary>Cancelled when the user presses Cancel (or the linked token is cancelled).</summary>
    public CancellationToken Token { get; }

    /// <summary>True when the operation stopped because it was asked to (Cancel or the linked token).</summary>
    public bool WasCancelled(Exception error) => error is OperationCanceledException && Token.IsCancellationRequested;

    internal void RequestCancel()
    {
        CancelRequested = true;
        _cts.Cancel();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _owner.End(this);
        _cts.Dispose();
    }
}

/// <summary>An operation was refused because another one holds the busy gate; the message is localised.</summary>
public sealed class OperationRefusedException(string message) : InvalidOperationException(message);
