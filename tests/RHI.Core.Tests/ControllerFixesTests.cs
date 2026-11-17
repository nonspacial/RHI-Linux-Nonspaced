using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Controller;
using Avalonia;
using Silk.NET.SDL;

namespace RHI.Core.Tests;

/// <summary>
/// w14 controller fixes (owner's Xbox Elite Series 2 test on the integration build): the right stick click's full Home
/// (upstream 2.9.4), the left stick click's All / Installed / Favourites cycle, the controller in Settings and in RHI's
/// other windows, the right stick sideways on the covers, the Elite's paddles, and the controller taking control back
/// after the mouse. Driven by synthetic pad states and router decisions, as the window applies them.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class ControllerFixesTests
{
    public ControllerFixesTests() => UiLocalizationService.Apply("en-US");

    // ---- 1. right stick click (upstream PadAction.Home) and left stick click (CycleMainFilter) ----------------------

    [Fact]
    public void Home_OnTheCoversWithCollections_GoesToTheNextCollection()
    {
        Assert.Equal(PadHomeRoute.NextCollection, PadRouter.Home(panelOpen: false, menuOpen: false, collections: 2));
    }

    [Fact]
    public void Home_WithoutCollections_GoesBackToTheCover()
    {
        // The owner's report: with no collections the click did nothing.
        Assert.Equal(PadHomeRoute.BackToCover, PadRouter.Home(panelOpen: false, menuOpen: false, collections: 0));
    }

    [Fact]
    public void Home_WithThePanelOpen_GoesBackToTheCover_EvenWithCollections()
    {
        Assert.Equal(PadHomeRoute.BackToCover, PadRouter.Home(panelOpen: true, menuOpen: false, collections: 3));
    }

    [Fact]
    public void Home_InAMenu_DoesNothing()
    {
        Assert.Equal(PadHomeRoute.None, PadRouter.Home(panelOpen: false, menuOpen: true, collections: 3));
        Assert.Equal(PadHomeRoute.None, PadRouter.Home(panelOpen: true, menuOpen: true, collections: 0));
    }

    [Fact]
    public void Home_InSettings_ClosesIt_InADialog_DoesNothing()
    {
        Assert.Equal(PadWindowCommand.Close, PadRouter.InWindow(PadWindowKind.Settings, PadAction.Home, dropDownOpen: false));
        Assert.Equal(PadWindowCommand.None, PadRouter.InWindow(PadWindowKind.Dialog, PadAction.Home, dropDownOpen: false));
        Assert.Equal(PadWindowCommand.None, PadRouter.InWindow(PadWindowKind.Settings, PadAction.Home, dropDownOpen: true));
    }

    [Fact]
    public void CollectionCycle_AfterTheLast_IsAllGames()
    {
        string[] names = ["RPGs", "Racing"];
        Assert.Equal("RPGs", ControllerPoller.NextCollection(names, null));
        Assert.Equal("Racing", ControllerPoller.NextCollection(names, "RPGs"));
        Assert.Null(ControllerPoller.NextCollection(names, "Racing")); // null: the All games filter (upstream Filter_Click("Detected"))
    }

    [Fact]
    public void LeftStickClick_CyclesAllInstalledFavourites_NotHidden()
    {
        // LibraryMainFilters: 0 All Games, 1 Installed, 2 Favourites, 3 Hidden.
        Assert.Equal(1, PadRouter.NextMainFilter(0, inCollection: false));
        Assert.Equal(2, PadRouter.NextMainFilter(1, inCollection: false));
        Assert.Equal(0, PadRouter.NextMainFilter(2, inCollection: false)); // Favourites → All (upstream: no Hidden)
        Assert.Equal(0, PadRouter.NextMainFilter(3, inCollection: false)); // from Hidden: All
        Assert.Equal(0, PadRouter.NextMainFilter(-1, inCollection: false)); // from an engine filter: All
    }

    [Fact]
    public void LeftStickClick_FromACollection_FirstGoesToAll()
    {
        Assert.Equal(0, PadRouter.NextMainFilter(-1, inCollection: true));
        Assert.Equal(0, PadRouter.NextMainFilter(1, inCollection: true));
    }

    // ---- 2. the controller in Settings and RHI's other windows -------------------------------------------------------

    [Fact]
    public void Input_GoesToTheActiveRhiWindow_AndNowhereWhenRhiIsNotActive()
    {
        Assert.Equal(0, PadRouter.Target([true, false]));
        Assert.Equal(1, PadRouter.Target([false, true]));      // Settings (or a dialog) in front
        Assert.Equal(-1, PadRouter.Target([false, false]));    // another app is active
        Assert.Equal(-1, PadRouter.Target([]));
    }

    [Fact]
    public void Settings_MenuAndB_CloseIt()
    {
        Assert.Equal(PadWindowCommand.Close, PadRouter.InWindow(PadWindowKind.Settings, PadAction.Settings, dropDownOpen: false));
        Assert.Equal(PadWindowCommand.Close, PadRouter.InWindow(PadWindowKind.Settings, PadAction.Back, dropDownOpen: false));
    }

    [Fact]
    public void Settings_LbRb_JumpSections_DPadMoves_ASelects()
    {
        Assert.Equal(PadWindowCommand.PrevSection, PadRouter.InWindow(PadWindowKind.Settings, PadAction.PrevGame, false));
        Assert.Equal(PadWindowCommand.NextSection, PadRouter.InWindow(PadWindowKind.Settings, PadAction.NextGame, false));
        foreach (var d in new[] { PadAction.Up, PadAction.Down, PadAction.Left, PadAction.Right })
            Assert.Equal(PadWindowCommand.Move, PadRouter.InWindow(PadWindowKind.Settings, d, false));
        Assert.Equal(PadWindowCommand.Select, PadRouter.InWindow(PadWindowKind.Settings, PadAction.Select, false));
        Assert.Equal(PadWindowCommand.None, PadRouter.InWindow(PadWindowKind.Settings, PadAction.Launch, false));
        Assert.Equal(PadWindowCommand.None, PadRouter.InWindow(PadWindowKind.Settings, PadAction.PageForward, false));
    }

    [Fact]
    public void OpenDropDown_GetsTheKeys_BClosesItUnchanged_MenuDoesNothing()
    {
        foreach (var kind in new[] { PadWindowKind.Settings, PadWindowKind.Dialog })
        {
            Assert.Equal(PadWindowCommand.DropDownKey, PadRouter.InWindow(kind, PadAction.Down, dropDownOpen: true));
            Assert.Equal(PadWindowCommand.DropDownKey, PadRouter.InWindow(kind, PadAction.Select, dropDownOpen: true));
            Assert.Equal(PadWindowCommand.DropDownKey, PadRouter.InWindow(kind, PadAction.Back, dropDownOpen: true)); // Esc: unchanged
            Assert.Equal(PadWindowCommand.None, PadRouter.InWindow(kind, PadAction.Settings, dropDownOpen: true));
            Assert.Equal(PadWindowCommand.None, PadRouter.InWindow(kind, PadAction.NextGame, dropDownOpen: true));
        }
    }

    [Fact]
    public void Dialog_BCloses_MenuAndShouldersDoNothing()
    {
        Assert.Equal(PadWindowCommand.Close, PadRouter.InWindow(PadWindowKind.Dialog, PadAction.Back, false));
        Assert.Equal(PadWindowCommand.Select, PadRouter.InWindow(PadWindowKind.Dialog, PadAction.Select, false));
        Assert.Equal(PadWindowCommand.Move, PadRouter.InWindow(PadWindowKind.Dialog, PadAction.Left, false));
        Assert.Equal(PadWindowCommand.None, PadRouter.InWindow(PadWindowKind.Dialog, PadAction.Settings, false));
        Assert.Equal(PadWindowCommand.None, PadRouter.InWindow(PadWindowKind.Dialog, PadAction.NextGame, false));
    }

    [Fact]
    public void SettingsSections_LbRb_StopAtTheEnds()
    {
        // Upstream PadJumpSection: clamped, from the highlighted section (the first when none is).
        string[] keys = ["General", "Display", "Accounts"];
        Assert.Equal("Display", PadRouter.StepSection(keys, "General", 1));
        Assert.Equal("Accounts", PadRouter.StepSection(keys, "Accounts", 1));
        Assert.Equal("General", PadRouter.StepSection(keys, "General", -1));
        Assert.Equal("General", PadRouter.StepSection(keys, null, 1));
        Assert.Null(PadRouter.StepSection([], "General", 1));
    }

    [Fact]
    public void Settings_DownGoesToTheNextRow_ItsDropDownOnTheRight_NotAButtonFurtherDown()
    {
        // A button row, then a setting's row (label, not focusable; its drop-down on the right), then another button row.
        Rect[] rects = [new(0, 0, 200, 30), new(600, 50, 200, 30), new(0, 200, 200, 30), new(220, 200, 200, 30)];
        Assert.Equal(1, ControllerPresenter.Next(rects, rects[0], PadAction.Down));
        Assert.Equal(3, ControllerPresenter.Next(rects, rects[1], PadAction.Down)); // the next row, its control nearest across
        Assert.Equal(1, ControllerPresenter.Next(rects, rects[3], PadAction.Up));
        Assert.Equal(0, ControllerPresenter.Next(rects, rects[1], PadAction.Up));
        Assert.Equal(-1, ControllerPresenter.Next(rects, rects[2], PadAction.Down));
    }

    // ---- 3. the right stick sideways on the covers -------------------------------------------------------------------

    [Fact]
    public void RightStickX_PastTheDeadZone_ScrollsSideways_AtTheVerticalSpeed()
    {
        var p = new ControllerPoller();
        p.Reset(PadState.None);
        p.Update(new PadState(0, 0, 0, 32767, 0), 0, out var y, out var x);
        Assert.Equal(0, y);
        Assert.Equal(ControllerPoller.ScrollPixelsPerTick, x, 3);  // right is positive
        p.Update(new PadState(0, 0, 0, -32767, 32767), 1, out var up, out var left);
        Assert.Equal(-ControllerPoller.ScrollPixelsPerTick, left, 3);
        Assert.Equal(-ControllerPoller.ScrollPixelsPerTick, up, 3);   // same speed both ways
        p.Update(new PadState(0, 0, 0, ControllerPoller.ScrollDeadZone, 0), 2, out _, out var rest);
        Assert.Equal(0, rest);                                        // the same dead zone
    }

    [Fact]
    public void RightStickX_ScrollsTheShelfOnlyOnTheCovers()
    {
        Assert.Equal((12.0, 5.0), PadRouter.Scroll(onShelf: true, x: 12, y: 5));
        Assert.Equal((0.0, 5.0), PadRouter.Scroll(onShelf: false, x: 12, y: 5));
    }

    // ---- 4. Xbox Elite Series 2 --------------------------------------------------------------------------------------

    [Fact]
    public void ElitePaddles_AndTheGuideButton_AreNotMapped()
    {
        GameControllerButton[] ignored =
        [
            GameControllerButton.Paddle1, GameControllerButton.Paddle2, GameControllerButton.Paddle3, GameControllerButton.Paddle4,
            GameControllerButton.Misc1, GameControllerButton.Guide, GameControllerButton.Touchpad,
        ];
        foreach (var button in ignored)
            Assert.Equal(0, SdlGamepad.ButtonsFrom(b => b == button));
        Assert.Equal(ControllerPoller.A, SdlGamepad.ButtonsFrom(b => b == GameControllerButton.A));
        Assert.Equal(14, SdlGamepad.ButtonMap.Count); // XInput's 14 buttons, nothing else
        // Everything held, paddles too: exactly the standard buttons.
        ushort all = 0;
        foreach (var (_, bit) in SdlGamepad.ButtonMap) all |= bit;
        Assert.Equal(all, SdlGamepad.ButtonsFrom(_ => true));
    }

    [Fact]
    public void Elite2_ShowsXboxNames()
    {
        foreach (var name in new[] { "Xbox One Elite 2 Controller", "Xbox Elite Wireless Controller Series 2", "Microsoft X-Box One Elite 2 pad" })
            Assert.Equal(PadStyle.Xbox, ControllerPresenter.StyleOf(name));
        var line = ControllerPresenter.Line(ControllerPresenter.Hints(PadContext.Shelf, false, ControllerPresenter.StyleOf("Xbox One Elite 2 Controller")));
        Assert.Contains("A Select", line);
        Assert.Contains("LB / RB", line);
    }

    // ---- 5. the controller takes control back after the mouse -------------------------------------------------------

    private sealed class Element(string name)
    {
        public string Name { get; } = name;
        public bool Attached { get; set; } = true;
        public string Scope { get; set; } = "page";
        public override string ToString() => Name;
    }

    private static bool Live(Element e) => e.Attached;

    [Fact]
    public void AfterAMouseClick_TheNextPadInput_GoesBackToTheLastPadFocus()
    {
        var memory = new PadFocusMemory<Element>();
        var cover = new Element("cover");
        var tile = new Element("tile");
        var clicked = new Element("filters button");
        memory.Remember(tile);                         // the pad moved to a tile
        Assert.True(memory.PointerUsed());             // a click elsewhere: hints off (first use only)
        Assert.False(memory.PointerUsed());
        Assert.True(memory.PointerInUse);
        _ = clicked;
        Assert.Same(tile, memory.TakeBack(Live, cover)); // the next pad input: back to the tile
        Assert.False(memory.PointerInUse);
        Assert.Null(memory.TakeBack(Live, cover));     // the pad has control: nothing to restore
    }

    [Fact]
    public void WhenThePadFocusWasRemoved_TheCoverIsTheFallback()
    {
        var memory = new PadFocusMemory<Element>();
        var cover = new Element("cover");
        var tile = new Element("tile");
        memory.Remember(tile);
        memory.PointerUsed();
        tile.Attached = false;                          // the tile was rebuilt (another game)
        Assert.Same(cover, memory.TakeBack(Live, cover));
    }

    [Fact]
    public void InTheSidePanel_TheTargetMustBeInThePanel_ElseThePanelsFirstControl()
    {
        var memory = new PadFocusMemory<Element>();
        var shelfCover = new Element("cover") { Scope = "shelf" };
        var panelFirst = new Element("panel first") { Scope = "panel" };
        var panelRow = new Element("panel row") { Scope = "panel" };
        memory.Remember(panelRow);
        memory.PointerUsed();
        Assert.Same(panelRow, memory.TakeBack(e => e.Attached && e.Scope == "panel", panelFirst));
        memory.Remember(shelfCover);                   // the panel opened with the mouse after the pad was on the covers
        memory.PointerUsed();
        Assert.Same(panelFirst, memory.TakeBack(e => e.Attached && e.Scope == "panel", panelFirst));
    }

    [Fact]
    public void InSettings_TheTargetIsTheLastRow_ElseTheSectionList()
    {
        var memory = new PadFocusMemory<Element>();
        var sectionButton = new Element("General");
        var row = new Element("Controller support");
        memory.Remember(row);
        memory.PointerUsed();
        Assert.Same(row, memory.TakeBack(Live, sectionButton));
        memory.PointerUsed();
        row.Attached = false;                           // Settings was rebuilt / the row hidden by Find a setting
        Assert.Same(sectionButton, memory.TakeBack(Live, sectionButton));
    }

    [Fact]
    public void WithoutAnyPadFocus_TheHomeIsUsed()
    {
        var memory = new PadFocusMemory<Element>();
        var cover = new Element("cover");
        memory.PointerUsed();
        Assert.Same(cover, memory.TakeBack(Live, cover));
    }

    [Fact]
    public void PadSequence_MovesClickMoves_KeepsWorking()
    {
        // Pad moves (the poller), a pointer click, then more pad input: the poller still turns presses into actions.
        var poller = new ControllerPoller();
        var memory = new PadFocusMemory<Element>();
        var cover = new Element("cover");
        var tile = new Element("tile");
        poller.Reset(PadState.None);
        Assert.Equal([PadAction.Down], poller.Update(new PadState(ControllerPoller.DPadDown, 0, 0, 0, 0), 0, out _));
        memory.Remember(tile);
        poller.Update(PadState.None, 20, out _);
        memory.PointerUsed();                           // mouse click on another control
        var actions = poller.Update(new PadState(ControllerPoller.DPadRight, 0, 0, 0, 0), 40, out _);
        Assert.Equal([PadAction.Right], actions);
        Assert.Same(tile, memory.TakeBack(Live, cover));
        Assert.Equal([PadAction.Select], poller.Update(new PadState(ControllerPoller.A, 0, 0, 0, 0), 60, out _));
    }

    [Fact]
    public void TheMouseIsOnlyNoticedPastTheSlack()
    {
        Assert.False(PadFocusMemory<Element>.Moved(100, 100, 102, 97));   // jitter / the shield appearing
        Assert.True(PadFocusMemory<Element>.Moved(100, 100, 104, 100));
        Assert.True(PadFocusMemory<Element>.Moved(100, 100, 100, 96));
    }
}
