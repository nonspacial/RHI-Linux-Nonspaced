using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #309 (upstream 2.9.2 "What's New: the search box is now a small box above the version list … The arrow on the left hides
/// the versions and search … + and − change the text size. RHI remembers both"): the presenter over CHANGELOG.md.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class WhatsNewTests
{
    private const string Notes = """
        # Changelog

        Intro paragraph.

        ## In progress

        ### Wave 14 — ui

        - Status bar shows the last check.
        - OptiScaler presets.

        ## 2.8.8 — 2026-11-16: level with upstream RHI 2.8.8

        - Fixed an OptiScaler crash.

        ```
        ## not a heading inside a fence
        ```

        ## Wave 13 — 2026-11-16: available mods

        - Available mods list.
        """;

    private static WhatsNewPresenter Presenter(List<(bool, double)>? saved = null, bool hidden = false, double size = 12)
        => new(Notes, hidden, size, (h, s) => saved?.Add((h, s)));

    [Fact]
    public void Split_OneSectionPerSecondLevelHeading_NeverInsideACodeFence()
    {
        var (preamble, sections) = WhatsNewPresenter.Split(Notes);
        Assert.Equal("# Changelog\n\nIntro paragraph.", preamble);
        Assert.Equal(["In progress", "2.8.8", "Wave 13"], sections.Select(s => s.NavLabel));
        Assert.Equal("2.8.8 — 2026-11-16: level with upstream RHI 2.8.8", sections[1].Title);
        Assert.Contains("## not a heading inside a fence", sections[1].Body);
        Assert.StartsWith("## Wave 13", sections[2].Body);
    }

    [Theory]
    [InlineData("In progress", "In progress")]
    [InlineData("2.8.7.1 — 2026-10-28: .deb, .rpm and .tar.gz downloads", "2.8.7.1")]
    [InlineData("2026-10-07: upstream RHI 2.8.0–2.8.5, responsive window", "2026-10-07")]
    public void NavLabel_IsTheHeadingUpToItsDashOrColon(string title, string expected)
        => Assert.Equal(expected, WhatsNewPresenter.NavLabel(title));

    [Fact]
    public void Opens_OnTheNewest_WithTheIntroAboveIt()
    {
        var p = Presenter();
        Assert.Equal(0, p.SelectedIndex);
        Assert.StartsWith("# Changelog\n\nIntro paragraph.\n\n## In progress", p.Text);
        Assert.True(p.NavItems[0].IsSelected);
        p.ShowVersion(2);
        Assert.StartsWith("## Wave 13", p.Text);
        Assert.Equal([false, false, true], p.NavItems.Select(i => i.IsSelected));
    }

    [Fact]
    public void Search_NarrowsTheList_AndShowsEveryMatchingLineUnderItsHeading()
    {
        UiLocalizationService.Apply("en-US");
        var p = Presenter();
        p.ApplySearch("optiscaler");

        Assert.Equal(-1, p.SelectedIndex);
        Assert.Equal([true, true, false], p.NavItems.Select(i => i.IsVisible));
        Assert.Equal(["1", "1", ""], p.NavItems.Select(i => i.Count));
        Assert.Equal("2 matching line(s) in 2 version(s). Click a version on the left to read all of it.\n\n"
                     + "## In progress\n\n**Wave 14 — ui**\n\n- OptiScaler presets.\n\n"
                     + "## 2.8.8 — 2026-11-16: level with upstream RHI 2.8.8\n\n- Fixed an OptiScaler crash.\n\n", p.Text);

        p.ApplySearch("  ");
        Assert.All(p.NavItems, item => Assert.True(item.IsVisible));
        Assert.All(p.NavItems, item => Assert.Equal("", item.Count));
        Assert.Equal(0, p.SelectedIndex); // back on the version picked before the search
    }

    [Fact]
    public void Search_WithNoHit_SaysSo()
    {
        UiLocalizationService.Apply("en-US");
        var p = Presenter();
        p.ApplySearch("vulkan");
        Assert.Equal("Nothing in the notes mentions \"vulkan\".", p.Text);
        Assert.All(p.NavItems, item => Assert.False(item.IsVisible));
    }

    [Fact]
    public void TextSize_StepsByOne_Between10And20_AndIsRemembered()
    {
        var saved = new List<(bool, double)>();
        var p = Presenter(saved, size: 19);
        p.MakeBigger();
        Assert.Equal(20, p.FontSize);
        Assert.False(p.CanMakeBigger);
        p.MakeBigger(); // at the top: nothing changes, nothing saved
        Assert.Equal([(false, 20.0)], saved);
        var small = Presenter(saved, size: 4); // a stored size out of range is clamped
        Assert.Equal(LinuxAppSettings.WhatsNewMinFontSize, small.FontSize);
        Assert.False(small.CanMakeSmaller);
        small.MakeBigger();
        Assert.Equal(11, small.FontSize);
    }

    [Fact]
    public void TheArrow_HidesTheListAndSearch_AndIsRemembered()
    {
        UiLocalizationService.Apply("en-US");
        var saved = new List<(bool, double)>();
        var p = Presenter(saved);
        Assert.Equal("Hide the versions and search", p.NavToggleTip);
        p.ToggleNav();
        Assert.True(p.NavHidden);
        Assert.Equal("Show the versions and search", p.NavToggleTip);
        Assert.Equal([(true, 12.0)], saved);
    }

    [Fact]
    public void Settings_KeepTheTextSizeInRange()
    {
        Assert.Equal(20, new LinuxAppSettings { WhatsNewFontSize = 99 }.Normalize().WhatsNewFontSize);
        Assert.Equal(12, new LinuxAppSettings { WhatsNewFontSize = double.NaN }.Normalize().WhatsNewFontSize);
        Assert.Equal(12, new LinuxAppSettings().WhatsNewFontSize);
    }

    [Fact]
    public void TheView_ShowsTheNotesAndTheList_AndTheArrowHidesTheList()
    {
        var p = Presenter();
        var parts = RHI.Desktop.Views.WhatsNewView.BuildParts(p);
        Assert.Equal(3, parts.NavList.Children.Count);
        Assert.True(parts.NavPanel.IsVisible);
        Assert.NotEmpty(parts.Notes.Children);
        parts.ToggleNav.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.False(parts.NavPanel.IsVisible);
        parts.NavList.Children[2].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.Equal(2, p.SelectedIndex);
    }

    [Fact]
    public void TheRealChangelog_SplitsIntoVersions()
    {
        var (_, sections) = WhatsNewPresenter.Split(RHI.Desktop.Views.ChangelogText.Load());
        Assert.True(sections.Count > 10);
        Assert.Contains(sections, s => s.NavLabel == "2.8.8");
    }

    [Fact]
    public async Task TheShell_RemembersTheListAndTextSizeInSettings()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var p = fx.Vm.CreateWhatsNew(Notes);
        p.ToggleNav();
        p.MakeBigger();
        var stored = fx.Settings.Load();
        Assert.True(stored.WhatsNewNavHidden);
        Assert.Equal(13, stored.WhatsNewFontSize);
        Assert.True(fx.Vm.CreateWhatsNew(Notes).NavHidden);
    }
}
