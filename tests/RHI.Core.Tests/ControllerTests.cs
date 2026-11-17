using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Controller;
using RHI.Platform.Linux.Settings;
using Avalonia;

namespace RHI.Core.Tests;

/// <summary>
/// w14/layouts-b (#308): the controller in Spotlight — upstream's ControllerPollerTests (ported: the same rules over
/// <see cref="PadState"/>), SDL's reading in XInput's layout, row navigation, the footer's hints (Xbox and PlayStation
/// names), the collection cycle on the right stick, and the wait for a launched game to close.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class ControllerTests
{
    public ControllerTests() => UiLocalizationService.Apply("en-US");

    private static List<PadAction> Poll(ControllerPoller p, ushort buttons, long now, short lx = 0, short ly = 0, short ry = 0)
        => p.Update(new PadState(buttons, lx, ly, 0, ry), now, out _);

    private static void Reset(ControllerPoller p, ushort buttons) => p.Reset(new PadState(buttons, 0, 0, 0, 0));

    [Fact]
    public void Button_FiresOncePerPress()
    {
        var p = new ControllerPoller();
        Reset(p, 0);
        Assert.Equal(new[] { PadAction.Select }, Poll(p, ControllerPoller.A, 0));
        Assert.Empty(Poll(p, ControllerPoller.A, 1000));   // held: no repeat
        Assert.Empty(Poll(p, 0, 1100));                    // released
        Assert.Equal(new[] { PadAction.Select }, Poll(p, ControllerPoller.A, 1200));
    }

    [Fact]
    public void Mapping_MatchesTheButtonLayout()
    {
        var p = new ControllerPoller();
        Reset(p, 0);
        Assert.Equal(new[] { PadAction.Back }, Poll(p, ControllerPoller.B, 0)); Poll(p, 0, 1);
        Assert.Equal(new[] { PadAction.Launch }, Poll(p, ControllerPoller.Y, 2)); Poll(p, 0, 3);
        Assert.Equal(new[] { PadAction.GameMenu }, Poll(p, ControllerPoller.X, 4)); Poll(p, 0, 5);
        Assert.Equal(new[] { PadAction.PrevGame }, Poll(p, ControllerPoller.LeftShoulder, 6)); Poll(p, 0, 7);
        Assert.Equal(new[] { PadAction.NextGame }, Poll(p, ControllerPoller.RightShoulder, 8)); Poll(p, 0, 9);
        Assert.Equal(new[] { PadAction.Settings }, Poll(p, ControllerPoller.Start, 10)); Poll(p, 0, 11);
        Assert.Equal(new[] { PadAction.Filters }, Poll(p, ControllerPoller.BackBtn, 12));
    }

    [Fact]
    public void HeldWhenReset_IgnoredUntilReleased()
    {
        var p = new ControllerPoller();
        Reset(p, ControllerPoller.A | ControllerPoller.DPadDown); // pressed in a game, then alt-tab to RHI
        Assert.Empty(Poll(p, ControllerPoller.A | ControllerPoller.DPadDown, 0));
        Assert.Empty(Poll(p, ControllerPoller.A | ControllerPoller.DPadDown, 2000)); // no repeat either
        Assert.Empty(Poll(p, 0, 2100));
        Assert.Equal(new[] { PadAction.Select }, Poll(p, ControllerPoller.A, 2200));
    }

    [Fact]
    public void Direction_RepeatsAfterDelay()
    {
        var p = new ControllerPoller();
        Reset(p, 0);
        Assert.Equal(new[] { PadAction.Down }, Poll(p, ControllerPoller.DPadDown, 0));
        Assert.Empty(Poll(p, ControllerPoller.DPadDown, ControllerPoller.RepeatDelayMs - 1));
        Assert.Equal(new[] { PadAction.Down }, Poll(p, ControllerPoller.DPadDown, ControllerPoller.RepeatDelayMs));
        Assert.Empty(Poll(p, ControllerPoller.DPadDown, ControllerPoller.RepeatDelayMs + ControllerPoller.RepeatIntervalMs - 1));
        Assert.Equal(new[] { PadAction.Down }, Poll(p, ControllerPoller.DPadDown, ControllerPoller.RepeatDelayMs + ControllerPoller.RepeatIntervalMs));
    }

    [Fact]
    public void Stick_MovesPastThreshold_LargerAxisWins()
    {
        var p = new ControllerPoller();
        Reset(p, 0);
        Assert.Empty(Poll(p, 0, 0, lx: 9000));                                  // light touch
        Assert.Equal(new[] { PadAction.Right }, Poll(p, 0, 10, lx: 30000, ly: 12000));
        Assert.Empty(Poll(p, 0, 20));
        Assert.Equal(new[] { PadAction.Up }, Poll(p, 0, 30, lx: 5000, ly: 30000)); // XInput: +Y is up
    }

    [Fact]
    public void Triggers_AndStickClicks_ArePresses()
    {
        var p = new ControllerPoller();
        Reset(p, 0);
        Assert.Empty(Poll(p, ControllerPoller.WithTriggers(0, 60, 0), 0));            // resting finger
        Assert.Equal(new[] { PadAction.PageBack }, Poll(p, ControllerPoller.WithTriggers(0, 200, 0), 1));
        Assert.Empty(Poll(p, ControllerPoller.WithTriggers(0, 255, 0), 2));           // held: once
        Assert.Equal(new[] { PadAction.PageForward }, Poll(p, ControllerPoller.WithTriggers(0, 0, 255), 3));
        Poll(p, 0, 4);
        Assert.Equal(new[] { PadAction.CycleFilter }, Poll(p, ControllerPoller.LeftThumb, 5)); Poll(p, 0, 6);
        Assert.Equal(new[] { PadAction.Home }, Poll(p, ControllerPoller.RightThumb, 7));
    }

    [Fact]
    public void HeldTrigger_Repeats_OtherButtonsDont()
    {
        var p = new ControllerPoller();
        Reset(p, 0);
        ushort rt = ControllerPoller.WithTriggers(0, 0, 255);
        Assert.Equal(new[] { PadAction.PageForward }, Poll(p, rt, 0));
        Assert.Empty(Poll(p, rt, ControllerPoller.TriggerRepeatDelayMs - 1));
        Assert.Equal(new[] { PadAction.PageForward }, Poll(p, rt, ControllerPoller.TriggerRepeatDelayMs));
        Assert.Equal(new[] { PadAction.PageForward },
            Poll(p, rt, ControllerPoller.TriggerRepeatDelayMs + ControllerPoller.TriggerRepeatIntervalMs));
        Poll(p, 0, 5000);
        Assert.Equal(new[] { PadAction.Select }, Poll(p, ControllerPoller.A, 5001));
        Assert.Empty(Poll(p, ControllerPoller.A, 9000)); // A never repeats
    }

    [Fact]
    public void RightStick_Scrolls()
    {
        var p = new ControllerPoller();
        Reset(p, 0);
        p.Update(new PadState(0, 0, 0, 0, -32767), 0, out var down);
        p.Update(new PadState(0, 0, 0, 0, 2000), 1, out var none);
        Assert.True(down > 0);
        Assert.Equal(0, none);
    }

    // ---- Linux additions -----------------------------------------------------------------------------------------------

    [Fact]
    public void SdlReading_FlipsTheSticksY_ScalesTheTriggers()
    {
        var state = ControllerPoller.FromSdl(ControllerPoller.A, 100, -30000, 0, short.MinValue, 32767, 6000);
        Assert.Equal(30000, state.LeftY);            // SDL: up is negative
        Assert.Equal(short.MaxValue, state.RightY);  // -32768 stays in range
        Assert.True((state.Buttons & ControllerPoller.LeftTrigger) != 0);   // fully pressed
        Assert.True((state.Buttons & ControllerPoller.RightTrigger) == 0);  // 6000 / 32767 is a resting finger
        Assert.Equal(PadAction.Up, ControllerPoller.Direction(0, state.LeftX, state.LeftY));
    }

    [Fact]
    public void RightStickClick_CyclesTheCollections_ThenAllGames()
    {
        string[] names = ["RPGs", "Racing"];
        Assert.Equal("RPGs", ControllerPoller.NextCollection(names, null));
        Assert.Equal("Racing", ControllerPoller.NextCollection(names, "rpgs"));
        Assert.Null(ControllerPoller.NextCollection(names, "Racing"));
        Assert.Null(ControllerPoller.NextCollection([], null));
        Assert.Equal(9, ControllerPoller.Step(0, 10, 10));
        Assert.Equal(0, ControllerPoller.Step(3, -10, 10));
        Assert.Equal(-1, ControllerPoller.Step(0, 1, 0));
    }

    [Fact]
    public void RowNavigation_GoesStraightToTheNextRow_AndAlongIt()
    {
        // A row of three covers, then two tiles below; the second cover is focused.
        Rect[] rects = [new(0, 0, 100, 150), new(110, 0, 100, 150), new(220, 0, 100, 150), new(0, 200, 300, 60), new(320, 200, 300, 60)];
        Assert.Equal(2, ControllerPresenter.Next(rects, rects[1], PadAction.Right));
        Assert.Equal(0, ControllerPresenter.Next(rects, rects[1], PadAction.Left));
        Assert.Equal(3, ControllerPresenter.Next(rects, rects[1], PadAction.Down));  // the tile under it, not the nearer-centred one
        Assert.Equal(-1, ControllerPresenter.Next(rects, rects[1], PadAction.Up));
        Assert.Contains(ControllerPresenter.Next(rects, rects[3], PadAction.Up), new[] { 0, 1, 2 }); // back to the covers above it
        Assert.Equal(4, ControllerPresenter.Next(rects, rects[3], PadAction.Right));
    }

    [Fact]
    public void Hints_FollowWhereTheFocusIs_InThePadsNames()
    {
        var shelf = ControllerPresenter.Line(ControllerPresenter.Hints(PadContext.Shelf, hasCollections: true, PadStyle.Xbox));
        Assert.StartsWith("A Select   Y Launch   X Game menu   LB / RB Game", shelf);
        Assert.Contains("RS Collection", shelf);
        Assert.Contains("RS Covers", ControllerPresenter.Line(ControllerPresenter.Hints(PadContext.Page, false, PadStyle.Xbox)));
        Assert.Contains("B Covers", ControllerPresenter.Line(ControllerPresenter.Hints(PadContext.Page, false, PadStyle.Xbox)));
        Assert.Contains("○ Back", ControllerPresenter.Line(ControllerPresenter.Hints(PadContext.PanelCog, false, PadStyle.PlayStation)));
        Assert.Contains("L1 / R1 Section", ControllerPresenter.Line(ControllerPresenter.Hints(PadContext.Panel, false, PadStyle.PlayStation)));
        Assert.Equal("A Choose   B Close", ControllerPresenter.Line(ControllerPresenter.Hints(PadContext.Menu, false, PadStyle.Xbox)));
        Assert.Equal(PadStyle.PlayStation, ControllerPresenter.StyleOf("PS5 Controller"));
        Assert.Equal(PadStyle.PlayStation, ControllerPresenter.StyleOf("Sony DualSense Edge"));
        Assert.Equal(PadStyle.Xbox, ControllerPresenter.StyleOf("Steam Controller"));
        Assert.Equal(PadStyle.Xbox, ControllerPresenter.StyleOf(null));
    }

    [Fact]
    public async Task ReturnToFront_OnlyAfterTheGameRanAndClosed()
    {
        var ticks = 0;
        Task NoWait(TimeSpan _, CancellationToken __) { ticks++; return Task.CompletedTask; }
        var states = new Queue<bool>([false, false, true, true, false]);
        Assert.True(await GameExitWatcher.WaitForCloseAsync(() => states.Count > 0 && states.Dequeue(), NoWait, CancellationToken.None));
        Assert.False(await GameExitWatcher.WaitForCloseAsync(() => false, NoWait, CancellationToken.None, TimeSpan.FromSeconds(10))); // never ran
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.False(await GameExitWatcher.WaitForCloseAsync(() => true, (_, t) => Task.Delay(1, t), cancel.Token));
        Assert.True(ticks > 0);
    }
}
