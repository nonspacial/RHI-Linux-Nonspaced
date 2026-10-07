using RHI.Desktop.ViewModels;

namespace RHI.Core.Tests;

public sealed class OneShotStatusNoticeTests
{
    private readonly Queue<Action> _deferred = new();

    private OneShotStatusNotice Create() => new(_deferred.Enqueue);

    private void RunDeferred()
    {
        while (_deferred.TryDequeue(out var action))
            action();
    }

    [Fact]
    public void Decorate_WhileIdle_AppendsNoticeToTheNextStatusOnly()
    {
        var notice = Create();

        var shown = notice.Raise("Token rejected");

        Assert.Same(shown, notice.Decorate(shown)); // displaying the notice itself does not consume it
        Assert.Equal("Installed ReShade · Token rejected", notice.Decorate("Installed ReShade"));
        Assert.Equal("Scan complete", notice.Decorate("Scan complete"));
    }

    [Fact]
    public void Decorate_WithoutNotice_ReturnsSameInstance()
    {
        var notice = Create();
        const string status = "Ready";

        Assert.Same(status, notice.Decorate(status));
    }

    [Fact]
    public void Decorate_DuringOperation_KeepsNoticeOnProgressLinesUntilTheFinalLine()
    {
        var notice = Create();
        notice.SetOperationRunning(true);

        notice.Raise("Token rejected");

        Assert.Equal("Scanning 1/3 · Token rejected", notice.Decorate("Scanning 1/3"));
        Assert.Equal("Scanning 2/3 · Token rejected", notice.Decorate("Scanning 2/3"));
        Assert.Equal("Scan complete · Token rejected", notice.Decorate("Scan complete"));
        notice.SetOperationRunning(false);
        RunDeferred();
        Assert.Equal("Ready", notice.Decorate("Ready")); // shown on the final line, then consumed
    }

    [Fact]
    public void SetOperationRunning_NextOperationStartsBeforeSettle_KeepsNotice()
    {
        var notice = Create();
        notice.SetOperationRunning(true);
        notice.Raise("Token rejected");

        // The first operation ends on its progress line; a second starts straight away.
        notice.SetOperationRunning(false);
        notice.SetOperationRunning(true);
        RunDeferred();

        Assert.Equal("Scan complete · Token rejected", notice.Decorate("Scan complete"));
        notice.SetOperationRunning(false);
        RunDeferred();
        Assert.Equal("Ready", notice.Decorate("Ready"));
    }

    [Fact]
    public void SetOperationRunning_WithoutNotice_DefersNothing()
    {
        var notice = Create();

        notice.SetOperationRunning(true);
        notice.SetOperationRunning(false);

        Assert.Empty(_deferred);
    }
}
