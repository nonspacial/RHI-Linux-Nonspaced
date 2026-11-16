using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Desktop.Views;

namespace RHI.Core.Tests;

/// <summary>
/// w14/ui-settings (#297; upstream 2.8.9/2.9.0 <c>CogDialog</c>, end state 2.9.4): the one layout every component ⚙ uses —
/// cards, the setting name left with the option beside it and the explanation under it, the compact two-per-row grid, the
/// status line inside the ⚙, save-on-change dropdowns that go back when refused, "no settings" for an empty ⚙, Open-file
/// buttons, and Close (Apply / Cancel only for slider dialogs).
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class CogDialogLayoutTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-cog-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly Func<string, (bool, string?)> _opener = ComponentFileButton.Open;

    public CogDialogLayoutTests() => UiLocalizationService.Apply("en-US");

    public void Dispose()
    {
        ComponentFileButton.Open = _opener;
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    internal static IEnumerable<T> All<T>(Control root) => root.GetLogicalDescendants().OfType<T>();

    internal static IEnumerable<string> Texts(Control root) => All<TextBlock>(root).Where(t => t.IsVisible).Select(t => t.Text ?? "");

    internal static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [Fact]
    public void ACogWithNothingToSet_SaysSo_AndOneWithSettingsDoesNot()
    {
        var empty = new CogDialog("DOF Fix", "Alpha");
        var body = empty.Prepare();
        Assert.Equal("DOF Fix Settings", empty.Title);
        var line = Assert.Single(All<TextBlock>(body), t => t.Classes.Contains("cogEmpty"));
        Assert.Equal("This component has no settings.", line.Text);

        var luma = new CogDialog("Luma");
        luma.AddEmpty(UiLocalizationService.Text("Cog.LumaNotUnreal"));
        Assert.Equal(UiLocalizationService.Text("Cog.LumaNotUnreal"), Assert.Single(All<TextBlock>(luma.Prepare()), t => t.Classes.Contains("cogEmpty")).Text);

        var full = new CogDialog("ReShade");
        full.AddSection("Files");
        full.AddButtons(CogDialog.StyledButton("Deploy ReShade.ini", CogButtonKind.Primary));
        Assert.DoesNotContain(All<TextBlock>(full.Prepare()), t => t.Classes.Contains("cogEmpty"));
    }

    [Fact]
    public void ARow_PutsTheNameLeft_TheOptionRight_AndTheExplanationUnderIt_InASectionCard()
    {
        var cog = new CogDialog("RenoDX");
        cog.AddIntro("Intro between cards.");
        var loose = cog.AddRow("UE-Extended", new ComboBox(), "Use the UE-Extended mod.");
        var card = cog.AddSection("Compatibility");
        var row = cog.AddRow("Upgrade Path", new ComboBox { Name = "Path" }, "HDR uses the game's own HDR output.");
        cog.Prepare();

        // Text sits between cards; a setting before the first section goes in an untitled card.
        Assert.IsType<TextBlock>(cog.Body.Children[0]);
        var untitled = Assert.IsType<CogCard>(cog.Body.Children[1]);
        Assert.False(untitled.HeadingBlock.IsVisible);
        Assert.Contains(loose, untitled.Children);
        Assert.Same(card, cog.Body.Children[2]);
        Assert.Equal("COMPATIBILITY", card.HeadingBlock.Text);
        Assert.Contains(row, card.Children);

        var grid = row.GetLogicalChildren().OfType<StackPanel>().Single().Children.OfType<Grid>().Single();
        Assert.Equal(CogLayout.LabelColumnWidth, grid.ColumnDefinitions[0].Width.Value);
        Assert.Equal(0, Grid.GetColumn(row.LabelBlock));
        Assert.Equal(1, Grid.GetColumn(row.Setting!));
        Assert.True(row.HelpBlock.IsVisible);
        Assert.Equal("HDR uses the game's own HDR output.", row.HelpBlock.Text);
        Assert.Contains("cogHelp", row.HelpBlock.Classes);
        Assert.Equal("HDR uses the game's own HDR output.", ToolTip.GetTip(row.LabelBlock));
        Assert.Equal("HDR uses the game's own HDR output.", ToolTip.GetTip(row.Setting!));
    }

    [Fact]
    public void TheCompactGrid_PutsTwoSettingsPerRow_WithTheExplanationAsTheirTooltip()
    {
        var cog = new CogDialog("RTX Encore");
        cog.AddSection("Frame generation");
        var grid = cog.AddCompactGrid(
            cog.CompactCombo("Engine", "Which FG engine.", ["A", "B"], 0, _ => Task.FromResult(true)),
            cog.CompactCombo("Mode", "FG mode.", ["A", "B"], 0, _ => Task.FromResult(true)),
            cog.CompactCombo("Preset", "Preset.", ["A", "B"], 0, _ => Task.FromResult(true)),
            null);
        Assert.Equal(2, grid.Columns);
        Assert.Equal(4, grid.Children.Count);
        var cell = Assert.IsType<CogCell>(grid.Children[0]);
        Assert.Equal("Engine", cell.LabelBlock.Text);
        Assert.Equal("Which FG engine.", ToolTip.GetTip(cell.LabelBlock));
        Assert.Equal("Which FG engine.", ToolTip.GetTip(cell.Setting!));
    }

    [Fact]
    public void ADropdown_SavesTheMomentItChanges_AndARefusedOrFailedSave_GoesBack_WithTheMessageInsideTheCog()
    {
        var cog = new CogDialog("Display Commander");
        var calls = new List<int>();
        var answer = true;
        var combo = cog.AddCombo("Window Mode", "How the window is shown.", ["No changes", "Borderless", "Windowed"], 0, i =>
        {
            calls.Add(i);
            if (i == 2) throw new IOException("disk full");
            cog.SetResult(answer, answer ? $"Window Mode: {i}." : "Refused.");
            return answer;
        });
        cog.Prepare();
        Assert.Empty(calls); // nothing is written while the ⚙ is built

        combo.SelectedIndex = 1;
        Assert.Equal([1], calls);
        Assert.Equal(1, combo.SelectedIndex);
        Assert.Equal("Window Mode: 1.", cog.StatusText);
        Assert.Equal(CogStatusKind.Success, cog.StatusKind);

        answer = false;
        combo.SelectedIndex = 0;
        Assert.Equal(1, combo.SelectedIndex); // refused: back to what is saved
        Assert.Equal(CogStatusKind.Error, cog.StatusKind);

        combo.SelectedIndex = 2;
        Assert.Equal(1, combo.SelectedIndex);
        Assert.Equal("Couldn't change Window Mode: disk full", cog.StatusText);
        Assert.Equal([1, 0, 2], calls);

        // The status line is the ⚙'s own last line, inside the scrolling body.
        var status = Assert.IsType<TextBlock>(cog.Body.Children[^1]);
        Assert.True(status.IsVisible);
        Assert.Equal("Couldn't change Window Mode: disk full", status.Text);
        Assert.Contains("error", status.Classes);
    }

    [Fact]
    public void AnOpenFileButton_IsDisabledWhileTheFileIsMissing_OpensIt_AndAFailureShowsInsideTheCog()
    {
        var ini = Path.Combine(_root, "relimiter.ini");
        var cog = new CogDialog("ReLimiter");
        var missing = cog.MakeOpenFileButton("Open relimiter.ini", ini);
        Assert.False(missing.IsEnabled);
        Assert.Contains("secondary", missing.Classes);

        File.WriteAllText(ini, "[FrameLimiter]\n");
        var opened = new List<string>();
        ComponentFileButton.Open = path => { opened.Add(path); return (opened.Count == 1, "No supported desktop opener could be started."); };
        var open = cog.MakeOpenFileButton("Open relimiter.ini", ini);
        Assert.True(open.IsEnabled);
        Assert.Equal(ini, ToolTip.GetTip(open));
        Click(open);
        Assert.Equal([ini], opened);
        Assert.Equal("", cog.StatusText);

        Click(open);
        Assert.Equal(CogStatusKind.Error, cog.StatusKind);
        Assert.Equal(UiLocalizationService.Present("No supported desktop opener could be started."), cog.StatusText);

        File.Delete(ini);
        Click(open);
        Assert.Equal("relimiter.ini wasn't found.", cog.StatusText);
    }

    [Fact]
    public void TheWindowFrame_HasOnlyClose_AndApplyCancelOnlyForSliderDialogs()
    {
        var plain = CogWindow.Frame("ReShade Settings", new StackPanel(), null, null, _ => { });
        var buttons = All<Button>(plain).ToArray();
        Assert.Equal(["CogClose"], buttons.Select(b => b.Name ?? ""));
        Assert.Equal("Close", buttons[0].Content);
        Assert.Contains("ReShade Settings", Texts(plain));

        var closed = new List<bool>();
        var sliders = CogWindow.Frame("RTX HDR Settings", new StackPanel(), null, () => Task.FromResult(true), closed.Add);
        var names = All<Button>(sliders).Select(b => b.Name ?? "").ToArray();
        Assert.Equal(["CogApply", "CogClose"], names);
        Assert.Equal("Cancel", All<Button>(sliders).Single(b => b.Name == "CogClose").Content);
        Click(All<Button>(sliders).Single(b => b.Name == "CogApply"));
        Assert.Equal([true], closed);
    }
}
