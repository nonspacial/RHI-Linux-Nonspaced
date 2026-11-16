using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #281 (upstream 2.8.9): 20/30 FG Unlock's ⚙ — the mod's six main settings as dropdowns in dlssg_sm86.ini (values pinned
/// against the 0.4.2 package's INI), XeSS capped at 4X, kept on update, Open ini / Open log, and no GPU generation.
/// </summary>
public sealed partial class Dlssg2030UnlockDeploymentServiceTests
{
    [Fact]
    public void Settings_ReadThePackagesIni_AsItsDocumentedDefaults()
    {
        var lines = IniLines.Read(Fixture("dlssg_sm86-0.4.2.ini"));

        Assert.Equal([0, 0, 2, 0, 1, 0], Dlssg2030Ini.All.Select(setting => Dlssg2030Ini.IndexOf(setting, lines)));
        Assert.Equal(Dlssg2030Ini.All.Select(setting => setting.DefaultIndex), Dlssg2030Ini.All.Select(setting => Dlssg2030Ini.IndexOf(setting, [])));
        Assert.Equal(("DLSSG", "Optimized", "1"), (Dlssg2030Ini.SpeedTier.Section, Dlssg2030Ini.SpeedTier.Key, Dlssg2030Ini.SpeedTier.Default));
        // An invalid value shows the default, never a value the mod would treat as "off".
        Assert.Equal(Dlssg2030Ini.MaxFrames.DefaultIndex, Dlssg2030Ini.IndexOf(Dlssg2030Ini.MaxFrames, ["[FrameGeneration]", "MaxGeneratedFrames=9"]));
    }

    // Upstream: XeSS goes up to 4X — choosing XeSS at 5X/6X moves Max Frames to 4X; 5X/6X are refused under XeSS.
    [Fact]
    public void Settings_XeSsIsCappedAtFourX()
    {
        var lines = IniLines.Read(Fixture("dlssg_sm86-0.4.2.ini"));
        Assert.Null(Dlssg2030Ini.Apply(lines, Dlssg2030Ini.MaxFrames, 4));
        Assert.Equal("5", IniLines.Get(lines, "FrameGeneration", "MaxGeneratedFrames"));

        Assert.Null(Dlssg2030Ini.Apply(lines, Dlssg2030Ini.Engine, 1));
        Assert.Equal(("XeSS", "3"), (IniLines.Get(lines, "FrameGeneration", "Engine"), IniLines.Get(lines, "FrameGeneration", "MaxGeneratedFrames")));
        Assert.Equal(Dlssg2030Ini.XeSsMaxFourMessage, Dlssg2030Ini.Apply(lines, Dlssg2030Ini.MaxFrames, 3));
        Assert.Equal("3", IniLines.Get(lines, "FrameGeneration", "MaxGeneratedFrames"));
        Assert.Contains(lines, line => line.StartsWith("; Engine: DLSS = NVIDIA", StringComparison.Ordinal)); // comments kept
    }

    // The ⚙ keeps RHI's ownership of the INI: an update carries its settings into the new release's INI, and removal
    // deletes it (upstream). An INI the user changed by hand stays the user's.
    [Fact]
    public async Task SettingsWritten_AreCarriedIntoTheNewReleasesIni_AndRemovalTakesIt()
    {
        var (fixture, handler, service, records) = await InstalledAsync("settings-update");
        var ini = Path.Combine(fixture.Bin, Dlssg2030UnlockDeploymentService.IniFileName);

        Assert.Null(service.SetIniSetting(fixture.Game, Dlssg2030Ini.Preset, 2));
        Assert.Null(service.SetIniSetting(fixture.Game, Dlssg2030Ini.SuperResolution, 1));
        var record = records.Find(fixture.Game, ManagedComponentKind.Dlssg2030Unlock)!;
        Assert.Equal(RHI.Core.Deployment.FileHash.Sha256Hex(ini), record.Files.Single(file => file.Path == ini).Sha256);

        handler.Tag = NewerTag;
        handler.Ini = UpstreamIni().Replace("Level=1", "Level=2", StringComparison.Ordinal);
        var update = await service.UpdateAsync(fixture.Game);

        Assert.True(update.Success, update.Message);
        var lines = IniLines.Read(ini);
        Assert.Equal(("C", "M-INT8", "2"), (IniLines.Get(lines, "DLSSG", "Preset"), IniLines.Get(lines, "SuperResolution", "Mode"), IniLines.Get(lines, "Logging", "Level")));
        Assert.True((await service.RemoveAsync(fixture.Game)).Success);
        Assert.False(File.Exists(ini));
    }

    [Fact]
    public async Task Settings_RefusedWhenNotInstalled()
    {
        var (fixture, _, service, _) = await InstalledAsync("settings-removed");
        Assert.True((await service.RemoveAsync(fixture.Game)).Success);
        Assert.Equal(Dlssg2030Ini.NotInstalledMessage, service.SetIniSetting(fixture.Game, Dlssg2030Ini.Enabled, 1));
        Assert.Null(service.InstalledIniLines(fixture.Game));
    }

    // The ⚙ rows: greyed out with upstream's warning until installed; Engine moving Max Frames shows at once; a refused
    // choice goes back and says why.
    [Fact]
    public void SettingsRows_FollowTheIni()
    {
        var lines = IniLines.Read(Fixture("dlssg_sm86-0.4.2.ini")).ToList();
        string? shown = null;
        (string?, IReadOnlyList<string>?) Write(Dlssg2030Setting setting, int index)
            => Dlssg2030Ini.Apply(lines, setting, index) is { } refusal ? (refusal, null) : (null, lines);
        var content = FgSettingsBuilder.Dlssg2030(lines, "/game/bin", Write, line => shown = line);
        var rows = content.Sections.Single().Rows;
        foreach (var row in rows) { row.Changed = line => shown = line; row.Refresh(); }
        var (engine, maxFrames) = (rows[1], rows[2]);

        Assert.Equal(6, rows.Count);
        Assert.All(rows, row => Assert.True(row.IsEnabled));
        Assert.Equal(["2X", "3X", UiLocalizationService.Text("Dsg.FourXDefault"), "5X", "6X", UiLocalizationService.Text("Dsg.GameDecides")], maxFrames.Options);
        Assert.Equal(["/game/bin/dlssg_sm86.ini", "/game/bin/dlssg_sm86/logs/dlssg_sm86.log"], content.Files.Select(file => file.Path));

        maxFrames.SelectedIndex = 4; // 6X
        engine.SelectedIndex = 1;    // XeSS
        Assert.Equal(2, maxFrames.SelectedIndex);
        Assert.Equal(UiLocalizationService.Format("Dsg.Saved", engine.Label), shown);
        maxFrames.SelectedIndex = 3; // 5X under XeSS
        Assert.Equal(2, maxFrames.SelectedIndex);
        Assert.Equal(UiLocalizationService.Present(Dlssg2030Ini.XeSsMaxFourMessage), shown);

        var notInstalled = FgSettingsBuilder.Dlssg2030(null, null, Write, _ => { });
        foreach (var row in notInstalled.Sections.Single().Rows) row.Refresh();
        Assert.All(notInstalled.Sections.Single().Rows, row => Assert.False(row.IsEnabled));
        Assert.Equal(ComponentDialogLine.Warning(UiLocalizationService.Present(Dlssg2030Ini.NotInstalledMessage)), Assert.Single(notInstalled.Lines));
        Assert.Empty(notInstalled.Files);
    }
}
