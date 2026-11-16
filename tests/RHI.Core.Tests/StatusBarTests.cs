using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Core.Tests;

/// <summary>
/// #298 (upstream 2.9.2 <c>MainWindow.StatusBar</c>, <c>SettingsHandler.UpdateCountdown</c>): the status bar shows what RHI
/// is doing in the background and, when idle, when it last checked for updates and when the next check is due.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class StatusBarTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null, "Not yet")]
    [InlineData(0, "Just now")]
    [InlineData(-59, "Just now")]
    [InlineData(30, "Just now")] // the clock went back: a stamp in the future
    [InlineData(-60, "1m ago")]
    [InlineData(-18 * 60, "18m ago")]
    [InlineData(-(78 * 60 + 5), "1h 18m ago")]
    [InlineData(-(2 * 3600 + 5 * 60), "2h 05m ago")]
    [InlineData(-(51 * 3600), "2d 3h ago")]
    public void LastChecked_ReadsLikeUpstream(int? secondsFromNow, string expected)
    {
        UiLocalizationService.Apply("en-US");
        DateTimeOffset? last = secondsFromNow is { } s ? Noon.AddSeconds(s) : null;
        Assert.Equal(expected, StatusBarText.LastChecked(last, Noon));
    }

    [Theory]
    [InlineData(0, "under a minute")]
    [InlineData(60, "under a minute")]
    [InlineData(61, "0h 01m")]
    [InlineData(2 * 3600 + 41 * 60 + 20, "2h 41m")]
    [InlineData(4 * 3600, "4h 00m")]
    public void NextCheckIn_ReadsLikeUpstream(int secondsFromNow, string expected)
    {
        UiLocalizationService.Apply("en-US");
        Assert.Equal(expected, StatusBarText.NextCheckIn(Noon.AddSeconds(secondsFromNow), Noon));
    }

    [Theory]
    [InlineData(null, "After startup")]
    [InlineData(0, "Any moment")]
    [InlineData(-5, "Any moment")]
    [InlineData(2 * 3600 + 41 * 60 + 20, "2h 41m 20s")]
    public void Countdown_ForSettings_ReachesZeroWhenDue(int? secondsFromNow, string expected)
    {
        UiLocalizationService.Apply("en-US");
        DateTimeOffset? next = secondsFromNow is { } s ? Noon.AddSeconds(s) : null;
        Assert.Equal(expected, StatusBarText.Countdown(next, Noon));
    }

    [Fact]
    public void Countdown_RoundsPartSecondsUp()
    {
        UiLocalizationService.Apply("en-US");
        Assert.Equal("0h 00m 01s", StatusBarText.Countdown(Noon.AddMilliseconds(200), Noon));
    }

    [Fact]
    public void Idle_SaysWhenLastCheckedAndWhenTheNextCheckIs()
    {
        UiLocalizationService.Apply("en-US");
        Assert.Equal("Last checked: 18m ago  ·  Next check in 3h 41m",
            StatusBarText.Idle(Noon.AddMinutes(-18), Noon.AddMinutes(221), Noon));
        Assert.Equal("Last checked: just now  ·  Next check in 4h 00m", StatusBarText.Idle(Noon, Noon.AddHours(4), Noon));
        Assert.Equal("Last checked: not yet", StatusBarText.Idle(null, null, Noon)); // automatic updates off: no next check
    }

    [Fact]
    public void Idle_InPortuguese()
    {
        UiLocalizationService.Apply("pt-BR");
        try
        {
            Assert.Equal("Última verificação: há 18 min  ·  Próxima verificação em 3h 41min",
                StatusBarText.Idle(Noon.AddMinutes(-18), Noon.AddMinutes(221), Noon));
            Assert.Equal("Depois da inicialização", StatusBarText.Countdown(null, Noon));
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    [Fact]
    public void Activity_ShowsTheNewestStillRunning()
    {
        var activity = new BackgroundActivity();
        var changes = 0;
        activity.Changed += () => changes++;
        Assert.Equal("", activity.Current);
        var scan = activity.Begin("Scanning");
        var check = activity.Begin("Checking");
        Assert.Equal("Checking", activity.Current);
        check.Dispose();
        Assert.Equal("Scanning", activity.Current);
        check.Dispose(); // twice: nothing
        scan.Dispose();
        Assert.Equal("", activity.Current);
        Assert.Equal(4, changes);
    }

    private sealed class Clock
    {
        public DateTimeOffset Now { get; set; } = Noon;
    }

    private static (StatusBarPresenter Bar, Clock Clock, BackgroundActivity Activity) Presenter(
        DateTimeOffset? last = null, DateTimeOffset? next = null)
    {
        var clock = new Clock();
        var activity = new BackgroundActivity();
        var bar = new StatusBarPresenter(() => clock.Now, () => last, () => next, activity, action => action());
        return (bar, clock, activity);
    }

    [Fact]
    public void Presenter_WhenIdle_ShowsTheLastAndNextCheck()
    {
        UiLocalizationService.Apply("en-US");
        var (bar, _, _) = Presenter(Noon.AddMinutes(-18), Noon.AddMinutes(221));
        Assert.Equal("Last checked: 18m ago  ·  Next check in 3h 41m", bar.Text);
        Assert.Equal(bar.Text, bar.ToolTip);
    }

    [Fact]
    public void Presenter_ShowsAStatusLineForAWhile_ThenGoesBackToIdle_KeepingItInTheTooltip()
    {
        UiLocalizationService.Apply("en-US");
        var (bar, clock, _) = Presenter(Noon.AddMinutes(-18), Noon.AddMinutes(221));
        bar.ShowMessage("Installed ReShade");
        Assert.Equal("Installed ReShade", bar.Text);
        clock.Now = Noon + StatusBarPresenter.MessageDuration - TimeSpan.FromSeconds(1);
        bar.Refresh();
        Assert.Equal("Installed ReShade", bar.Text);
        clock.Now = Noon + StatusBarPresenter.MessageDuration;
        bar.Refresh();
        Assert.StartsWith("Last checked: ", bar.Text);
        Assert.Equal(bar.Text + "\nLast message: Installed ReShade", bar.ToolTip);
    }

    [Fact]
    public void Presenter_WhileAnOperationRuns_KeepsItsStatusLine()
    {
        UiLocalizationService.Apply("en-US");
        var (bar, clock, _) = Presenter();
        bar.SetBusy(true);
        bar.ShowMessage("Updating 3 games...");
        clock.Now = Noon.AddMinutes(5);
        bar.Refresh();
        Assert.Equal("Updating 3 games...", bar.Text);
        bar.SetBusy(false);
        Assert.Equal("Last checked: not yet", bar.Text);
    }

    [Fact]
    public void Presenter_ABackgroundPassHoldingTheGate_ShowsItsActivity_NotAnOldStatusLine()
    {
        UiLocalizationService.Apply("en-US");
        var (bar, clock, activity) = Presenter();
        bar.ShowMessage("Ready — Alpha");
        clock.Now = Noon.AddMinutes(30);
        using (activity.Begin("Checking for updates..."))
        {
            bar.SetBusy(true); // the background pass claims the busy gate after its activity began
            Assert.Equal("Checking for updates...", bar.Text);
        }
    }

    [Fact]
    public void Presenter_ShowsBackgroundActivity_OverTheIdleLine_AndAMessageOverBoth()
    {
        UiLocalizationService.Apply("en-US");
        var (bar, clock, activity) = Presenter(Noon.AddHours(-1), Noon.AddHours(3));
        using (activity.Begin("Checking for updates..."))
        {
            Assert.Equal("Checking for updates...", bar.Text);
            bar.ShowMessage("Copied the report");
            Assert.Equal("Copied the report", bar.Text);
            clock.Now = Noon.AddMinutes(1);
            bar.Refresh();
            Assert.Equal("Checking for updates...", bar.Text);
        }
        Assert.StartsWith("Last checked: 1h 01m ago", bar.Text);
    }

    [Fact]
    public async Task Shell_AStatusLineShowsOnTheBar_AndAFinishedScanLeavesNoActivity()
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await ViewModelFixture.StartAsync([]);
        Assert.Equal("", fx.Vm.Activity.Current); // the start-up scan's activity ended with it
        fx.Vm.StatusText = "Copied the report";
        Assert.StartsWith("Copied the report", fx.Vm.StatusBar.Text);
        Assert.Equal(fx.Vm.StatusText, fx.Vm.StatusBar.Text); // with any pending notice the line carries
        using (fx.Vm.BeginActivity("Sb.ActCheckingUpdates"))
            Assert.Equal("Checking for updates...", fx.Vm.Activity.Current);
        Assert.Equal("", fx.Vm.Activity.Current);
    }

    [Fact]
    public async Task Shell_IdleLine_ReadsTheUpdateCadence()
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await ViewModelFixture.StartAsync([]);
        var now = Noon;
        fx.Vm.Updates.Now = () => now;
        fx.Vm.StatusText = "Ready";
        now = Noon + StatusBarPresenter.MessageDuration;
        fx.Vm.StatusBar.Refresh();
        Assert.Equal(StatusBarText.Idle(fx.Vm.Updates.LastPass, fx.Vm.Updates.NextPass, now), fx.Vm.StatusBar.Text);
        Assert.StartsWith("Last checked: ", fx.Vm.StatusBar.Text);
    }
}
