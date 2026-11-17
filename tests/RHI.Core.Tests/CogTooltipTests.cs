using System.Xml.Linq;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #302 (with #297): RTX Encore's ⚙ rows carry upstream's per-setting explanations (2.9.4
/// <c>DetailPanelBuilder.Extras.RtxEncoreSettings</c>), and a greyed ⚙ control says why it is greyed — on the greyed control
/// itself (tooltips shown on disabled controls), in EN and PT-BR.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class CogTooltipTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-cog-tips-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        UiLocalizationService.Apply("en-US");
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    public void EveryRtxEncoreRow_HasUpstreamsExplanation(string language)
    {
        UiLocalizationService.Apply(language);
        var rows = FgSettingsBuilder.RtxEncore(Path.Combine(_root, "rtx-encore.jsonc"), FgSettingsWriters.Encore(Path.Combine(_root, "rtx-encore.jsonc"))).SelectMany(s => s.Rows).ToArray();
        Assert.True(rows.Length > 30);
        Assert.All(rows, row => Assert.False(string.IsNullOrWhiteSpace(row.Tip), row.Label));
        Assert.Equal(rows.Length, rows.Select(r => r.Tip).Distinct().Count());
        if (language == "en-US")
            Assert.StartsWith("Game keeps the game's own frame generation. XeFG replaces the game's DLSS frame generation", rows[0].Tip);
        // w14 final review m9: the Neural Rendering section and row are in the interface's language.
        var sections = FgSettingsBuilder.RtxEncore(Path.Combine(_root, "rtx-encore.jsonc"), FgSettingsWriters.Encore(Path.Combine(_root, "rtx-encore.jsonc")));
        var neural = UiLocalizationService.TextFor(language, "Sec.NeuralRendering");
        Assert.Contains(sections, s => s.Title == neural && s.Rows.Any(r => r.Label == neural));
        if (language == "pt-BR")
        {
            Assert.DoesNotContain(sections, s => s.Title == "Neural Rendering");
            Assert.DoesNotContain(rows, r => r.Label == "Neural Rendering");
        }
    }

    [Fact]
    public void AGreyedRow_SaysWhyItIsGreyed_ThenItsExplanation()
    {
        UiLocalizationService.Apply("en-US");
        var rows = FgSettingsBuilder.RtxEncore(Path.Combine(_root, "rtx-encore.jsonc"), FgSettingsWriters.Encore(Path.Combine(_root, "rtx-encore.jsonc"))).SelectMany(s => s.Rows).ToArray();
        foreach (var row in rows) row.Refresh();
        var multiplier = rows.Single(r => r.Label == UiLocalizationService.Text("Enc.Multiplier"));
        Assert.False(multiplier.IsEnabled); // Mode is "Game decides" in a new file
        Assert.Equal("Greyed out: only used when Mode is Fixed.\n\n" + multiplier.Tip, multiplier.ControlTip);
        var engine = rows.Single(r => r.Label == UiLocalizationService.Text("Dsg.Engine"));
        Assert.True(engine.IsEnabled);
        Assert.Equal(engine.Tip, engine.ControlTip); // enabled: just the explanation

        var notInstalled = FgSettingsBuilder.RtxEncore(null, FgSettingsWriters.NotInstalled).SelectMany(s => s.Rows).ToArray();
        foreach (var row in notInstalled) row.Refresh();
        Assert.All(notInstalled, row => Assert.StartsWith(UiLocalizationService.Text("Enc.NotInstalled"), row.ControlTip));
    }

    [Fact]
    public void TheTemplate_ShowsTheReasonOnTheGreyedControl()
    {
        var app = XDocument.Load(Path.Combine(LocalizationKeyUsageTests.RepoRoot(), "src", "RHI.Desktop", "App.axaml"));
        var controls = app.Descendants().Where(e => (string?)e.Attribute("ToolTip.Tip") == "{Binding ControlTip}").ToArray();
        Assert.Equal(4, controls.Length); // dropdown and number box, listed and two-per-row
        Assert.All(controls, c => Assert.Equal("True", (string?)c.Attribute("ToolTip.ShowOnDisabled")));
        Assert.Contains(app.Descendants(), e => (string?)e.Attribute("ToolTip.Tip") == "{Binding LockedTip}" && (string?)e.Attribute("ToolTip.ShowOnDisabled") == "True");
        Assert.Contains(app.Descendants(), e => (string?)e.Attribute("ToolTip.Tip") == "{Binding ProxyChoiceTip}" && (string?)e.Attribute("ToolTip.ShowOnDisabled") == "True");
    }
}
