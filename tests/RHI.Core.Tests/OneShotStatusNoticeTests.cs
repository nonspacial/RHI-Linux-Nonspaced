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
        notice.SetBusy(true);

        notice.Raise("Token rejected");

        Assert.Equal("Scanning 1/3 · Token rejected", notice.Decorate("Scanning 1/3"));
        Assert.Equal("Scanning 2/3 · Token rejected", notice.Decorate("Scanning 2/3"));
        Assert.Equal("Scan complete · Token rejected", notice.Decorate("Scan complete"));
        notice.SetBusy(false);
        RunDeferred();
        Assert.Equal("Ready", notice.Decorate("Ready")); // shown on the final line, then consumed
    }

    [Fact]
    public void SetOperationRunning_NextOperationStartsBeforeSettle_KeepsNotice()
    {
        var notice = Create();
        notice.SetBusy(true);
        notice.Raise("Token rejected");

        // The first operation ends on its progress line; a second starts straight away.
        notice.SetBusy(false);
        notice.SetBusy(true);
        RunDeferred();

        Assert.Equal("Scan complete · Token rejected", notice.Decorate("Scan complete"));
        notice.SetBusy(false);
        RunDeferred();
        Assert.Equal("Ready", notice.Decorate("Ready"));
    }

    [Fact]
    public void SetOperationRunning_WithoutNotice_DefersNothing()
    {
        var notice = Create();

        notice.SetBusy(true);
        notice.SetBusy(false);

        Assert.Empty(_deferred);
    }

    // up-133 re-review nit 1: a notice raised after an operation finished is appended to its result line, not put in
    // its place.
    [Fact]
    public void Raise_AppendsToTheCurrentLineInsteadOfReplacingIt()
    {
        var notice = Create();

        Assert.Equal("Installed ReShade · Token rejected", notice.Raise("Token rejected", "Installed ReShade"));
        Assert.Equal("Installed ReShade · Token rejected", notice.Raise("Token rejected", "Installed ReShade · Token rejected"));
        Assert.Equal("Token rejected", notice.Raise("Token rejected", ""));
        Assert.Equal("Token rejected", notice.Raise("Token rejected"));
    }

    // up-133 re-review nit 2: the view model's two busy flags (IsBusy, IsSelectionProbeBusy) both count as running.
    [Fact]
    public void BusyAndSelectionProbe_NoticeStaysUntilBothHaveEnded()
    {
        var notice = Create();
        notice.SetBusy(true);
        notice.SetSelectionProbeBusy(true);
        notice.Raise("Token rejected", "Scanning");

        notice.SetBusy(false);                     // the scan ends; the selected game's probe is still running
        Assert.Empty(_deferred);
        RunDeferred();
        Assert.Equal("Analysing Prey · Token rejected", notice.Decorate("Analysing Prey"));

        notice.SetSelectionProbeBusy(false);
        RunDeferred();
        Assert.False(notice.OperationRunning);
        Assert.Equal("Ready", notice.Decorate("Ready"));
    }

    // up-133 re-review nit 3: a new notice raised after an operation ends but before the deferred settle runs is kept.
    [Fact]
    public void NoticeRaisedBeforeTheSettleRuns_IsNotDropped()
    {
        var notice = Create();
        notice.SetBusy(true);
        notice.Raise("Old notice", "Scanning");
        notice.SetBusy(false);

        notice.Raise("New notice", "Scan complete · Old notice");
        RunDeferred();

        Assert.Equal("Ready · New notice", notice.Decorate("Ready"));
        Assert.Equal("Done", notice.Decorate("Done"));
    }

    // w1-policy review finding 4: the DLSS manifest failure lands after the scan; it must not replace the
    // scan's problems notice, whichever of the two is raised first.
    [Fact]
    public void SecondNotice_IsAddedToTheFirst_NotPutInItsPlace()
    {
        var notice = Create();
        notice.SetBusy(true);
        notice.Raise("DLSS manifest unavailable", "Scanning");

        var line = notice.Raise("Scan problems: Steam failed", "Scan complete");

        Assert.Equal("Scan complete · DLSS manifest unavailable · Scan problems: Steam failed", line);
        notice.SetBusy(false);
        RunDeferred();
        Assert.Equal("Ready", notice.Decorate("Ready"));
    }

    [Fact]
    public void NoticeRaisedAfterTheScanSettled_IsAppendedToTheProblemsLine()
    {
        var notice = Create();
        notice.SetBusy(true);
        var scanLine = notice.Raise("Scan problems: Steam failed", "Scan complete");
        notice.SetBusy(false);
        RunDeferred();

        Assert.Equal("Scan complete · Scan problems: Steam failed · DLSS manifest unavailable",
            notice.Raise("DLSS manifest unavailable", scanLine));
    }
}
