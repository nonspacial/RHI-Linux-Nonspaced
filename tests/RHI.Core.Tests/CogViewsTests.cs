using System.Xml.Linq;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// w14/ui-settings (#297): the AXAML ⚙s (RenoDX, OptiScaler, and the FG unlocks' / DLSS Enabler's / RTX Encore's shared
/// template) on the shared layout — RenoDX's and OptiScaler's compatibility settings two per row with the explanation as the
/// tooltip, as upstream (and RTX Encore's sections, upstream's compact grid); the status line inside each ⚙ shows only while
/// there is a result; OptiScaler's ⚙ opens OptiScaler.ini; notes are grouped into cards by their headings.
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class CogViewsTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument Load(params string[] path)
        => XDocument.Load(Path.Combine([LocalizationKeyUsageTests.RepoRoot(), "src", "RHI.Desktop", .. path]));

    private static XElement ItemsFor(XDocument doc, string binding)
        => doc.Descendants().Single(e => e.Name.LocalName == "ItemsControl" && (string?)e.Attribute("ItemsSource") == binding
                                         && e.Descendants().Any(d => d.Name.LocalName == "CogCompactGrid"));

    [Fact]
    public void RenoDxAndOptiScalerCompatibilitySettings_StayTwoPerRow_WithTheExplanationAsTheTooltip()
    {
        var renodx = ItemsFor(Load("Views", "Dialogs", "RenoDxSettingsDialogView.axaml"), "{Binding Rows}");
        var cell = renodx.Descendants().Single(e => e.Name.LocalName == "CogCell");
        Assert.Equal("{Binding Tooltip}", (string?)cell.Attribute("Help"));
        Assert.Equal("{l:T Cog.Compatibility}", (string?)renodx.Parent!.Attribute("Header"));

        var optiscaler = ItemsFor(Load("Views", "Dialogs", "OptiScalerSettingsDialogView.axaml"), "{Binding Settings}");
        Assert.Equal("{Binding Help}", (string?)optiscaler.Descendants().Single(e => e.Name.LocalName == "CogCell").Attribute("Help"));

        // RTX Encore's sections: the same compact grid (upstream AddCompactGrid); the others list name / option / explanation.
        var proxy = Load("App.axaml").Descendants().Single(e => e.Name.LocalName == "DataTemplate" && (string?)e.Attribute(X + "DataType") == "vm:ProxyComponentDialogViewModel");
        Assert.Equal("{Binding Compact}", (string?)ItemsFor(new XDocument(proxy), "{Binding Rows}").Attribute("IsVisible"));
        Assert.All(FgSettingsBuilder.RtxEncore(null, FgSettingsWriters.NotInstalled), section => Assert.True(section.Compact));
    }

    [Fact]
    public void EveryAxamlCog_HasItsStatusLineInside_ShownOnlyWithAResult()
    {
        var views = new[]
        {
            Load("Views", "Dialogs", "RenoDxSettingsDialogView.axaml").Root!,
            Load("Views", "Dialogs", "OptiScalerSettingsDialogView.axaml").Root!,
            Load("App.axaml").Descendants().Single(e => e.Name.LocalName == "DataTemplate" && (string?)e.Attribute(X + "DataType") == "vm:ProxyComponentDialogViewModel")
        };
        foreach (var view in views)
        {
            var status = view.Descendants().Single(e => e.Name.LocalName == "TextBlock" && (string?)e.Attribute("Name") == "CogStatus");
            Assert.Equal("{Binding Message}", (string?)status.Attribute("Text"));
            Assert.Equal("{Binding HasMessage}", (string?)status.Attribute("IsVisible"));
            Assert.Equal("cogStatus", (string?)status.Attribute("Classes"));
        }

        var renodx = new RenoDxSettingsDialogViewModel(new("/g/renodx.ini", true,
            [new RenoDxCompatibilitySetting("Set_Path", "Set_Path", "0", [new("0", "HDR"), new("1", "SDR")], false)], null), "Tunic",
            (_, _) => ComponentOperationResult.Fail("refused"));
        Assert.False(renodx.HasMessage);
        renodx.Rows[0].SelectedIndex = 1;
        Assert.True(renodx.HasMessage);
        Assert.Equal("refused", renodx.Message);
        Assert.Equal(0, renodx.Rows[0].SelectedIndex); // the failed save keeps the old value
    }

    [Fact]
    public async Task OptiScaler_OpensItsIni_AndGroupsItsSettingsIntoCards()
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = fx.Game("Os Cog Game");
        var (added, line) = await fx.Vm.ManualGames.TryAddManualGameAsync(new()
        {
            Name = game.Name, InstallPath = game.InstallPath, ExecutablePath = game.ExecutablePath, Runner = "Manual Windows"
        });
        Assert.True(added, line);
        await fx.WhenSelectionProbeIdleAsync(TimeSpan.FromSeconds(60));
        var ini = Path.Combine(game.InstallPath!, "OptiScaler.ini");
        static OptiScalerAdvancedSetting Setting(string id, string group) => new(id, group, id + " label", "a", [new("a", "A"), new("b", "B")], null);
        var snapshot = new OptiScalerAdvancedSnapshot(ini, true, [Setting("s1", "Upscalers"), Setting("s2", "Upscalers"), Setting("s3", "Frame generation")]);

        using (var missing = new OptiScalerSettingsDialogViewModel(fx.Vm, snapshot))
        {
            Assert.Equal("Open OptiScaler.ini", missing.OpenIni.Label);
            Assert.False(missing.OpenIni.Exists); // disabled while the game has none
            Assert.Equal([("Upscalers", 2), ("Frame generation", 1)], missing.IniGroups.Select(g => (g.Title, g.Settings.Count)));
        }

        File.WriteAllText(ini, "[Upscalers]\n");
        var opened = new List<string>();
        var opener = ComponentFileButton.Open;
        ComponentFileButton.Open = path => { opened.Add(path); return (false, "No supported desktop opener could be started."); };
        try
        {
            using var dialog = new OptiScalerSettingsDialogViewModel(fx.Vm, snapshot);
            Assert.True(dialog.OpenIni.Exists);
            dialog.OpenIni.OpenFileCommand.Execute(null);
            Assert.Equal([ini], opened);
            Assert.Equal(UiLocalizationService.Present("No supported desktop opener could be started."), dialog.Message); // inside the ⚙
            Assert.True(dialog.HasMessage);
        }
        finally { ComponentFileButton.Open = opener; }
    }

    [Fact]
    public void ComponentNotes_StartACardAtEachHeading_AndTheLinesBeforeItStayLoose()
    {
        var cards = ComponentLineCard.From(
        [
            ComponentDialogLine.Plain("intro"),
            ComponentDialogLine.Warning("careful"),
            ComponentDialogLine.Heading("Loaded by OptiScaler"),
            ComponentDialogLine.Plain("plugin"),
            ComponentDialogLine.Heading("XeSS frame generation (XeMFG)"),
            ComponentDialogLine.Warning("missing")
        ]);
        Assert.Equal([null, "Loaded by OptiScaler", "XeSS frame generation (XeMFG)"], cards.Select(c => c.Heading));
        Assert.True(cards[0].IsLoose);
        Assert.Equal(["intro", "careful"], cards[0].Lines.Select(l => l.Text));
        Assert.Equal("missing", Assert.Single(cards[2].Lines).Text);
    }
}
