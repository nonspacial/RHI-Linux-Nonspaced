using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

/// <summary>
/// #272 (upstream 2.9.3 OsIniEditor, OsIniLibrary, OsIniEditorTests): All Settings reads every OptiScaler.ini setting with
/// the options the build's own INI documents; upstream's 2.9.4 Nightly INI is the real sample.
/// </summary>
public sealed class OptiScalerIniCatalogTests
{
    private static readonly string[] Sample =
    [
        "; -------------------------------------------------------",
        "[Upscalers]",
        "; -------------------------------------------------------",
        "; Select Upscaler for Dx11 games",
        "; fsr22 (native DX11), xess (native DX11, Arc only), fsr31_12 (dx11on12, FSR4), dlss - Default (auto) is fsr22",
        "Dx11Upscaler=auto",
        "",
        "[FrameGen]",
        "; Enables Frame Generation",
        "; true or false - Default (auto) is false",
        "Enabled=auto",
        "",
        "; Selected FG Output",
        "; nofg, fsrfg, xefg, nukems - Default (auto) is nofg",
        "FGOutput=F5flow",
        "",
        "; Frametime source",
        "; 0: Input | 1: Time between Present calls | 2: Zero (Only for XeFG)",
        "; true or false - Default (auto) is 0",
        "FTInput=auto",
        "",
        "; Defines FG rectangle",
        "; integer value - Default (auto) is whole screen",
        "RectLeft=auto",
        "RectTop=auto",
        "",
        "; 1 to 3 - Default (auto) is 1",
        "AllowedFrameAhead=auto",
        "",
        "; 0.0 - 1.0 - Default (auto) is 0.3",
        "FPTVarianceFactor=auto",
    ];

    private static OptiScalerIniEntry Get(List<OptiScalerIniEntry> list, string key) => list.Single(entry => entry.Key == key);

    [Fact]
    public void Parse_ReadsOptionsFromComments()
    {
        var list = OptiScalerIniCatalog.Parse(Sample);

        var upscaler = Get(list, "Dx11Upscaler");
        Assert.Equal(OptiScalerIniKind.Choice, upscaler.Kind);
        Assert.Equal(["auto", "fsr22", "xess", "fsr31_12", "dlss"], upscaler.Options.Select(option => option.Value));
        Assert.Equal("xess (native DX11, Arc only)", upscaler.Options[2].Label); // a comma inside brackets is no split
        Assert.Equal("fsr22", upscaler.Default);
        var enabled = Get(list, "Enabled");
        Assert.Equal(OptiScalerIniKind.Bool, enabled.Kind);
        Assert.Equal(["auto", "true", "false"], enabled.Options.Select(option => option.Value));
        var output = Get(list, "FGOutput");
        Assert.Equal(("F5flow", "FrameGen"), (output.Value, output.Section)); // a value the build does not list is kept
    }

    [Fact]
    public void Parse_NumberedLineWinsOverWrongBoolLine()
    {
        var input = Get(OptiScalerIniCatalog.Parse(Sample), "FTInput");
        Assert.Equal(OptiScalerIniKind.Choice, input.Kind);
        Assert.Equal(["auto", "0", "1", "2"], input.Options.Select(option => option.Value));
    }

    [Fact]
    public void Parse_RangesTextAndSharedComments()
    {
        var list = OptiScalerIniCatalog.Parse(Sample);
        Assert.Equal(["auto", "1", "2", "3"], Get(list, "AllowedFrameAhead").Options.Select(option => option.Value));
        Assert.Equal(OptiScalerIniKind.Text, Get(list, "FPTVarianceFactor").Kind);
        Assert.Equal(OptiScalerIniKind.Text, Get(list, "RectLeft").Kind);
        Assert.Equal(Get(list, "RectLeft").Help, Get(list, "RectTop").Help); // keys in a row share the comment
    }

    [Fact]
    public void Merge_UsesBuildOptionsAndAddsMissingKeys()
    {
        var game = OptiScalerIniCatalog.Parse(["[FrameGen]", "; old comment", "FGOutput=fsrfg"]);
        var build = OptiScalerIniCatalog.Parse(["[FrameGen]", "; fsrfg, F5flow - Default (auto) is fsrfg", "FGOutput=auto",
            "; true or false - Default (auto) is false", "NewKey=auto"]);

        var merged = OptiScalerIniCatalog.Merge(game, build);

        var output = Get(merged, "FGOutput");
        Assert.Equal("fsrfg", output.Value);
        Assert.Contains(output.Options, option => option.Value == "F5flow");
        var added = Get(merged, "NewKey");
        Assert.False(added.InFile);
        Assert.Equal("auto", added.Value);
    }

    [Fact]
    public void UpstreamsNightlyIni_GivesEverySectionAndTheBuildsOwnOptions()
    {
        var list = OptiScalerIniCatalog.ParseFile(Path.Combine(AppContext.BaseDirectory, "Fixtures", "optiscaler-nightly-nvidia-rhi-2.9.4.ini"));

        var sections = list.Select(entry => entry.Section).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal("Upscalers", sections[0]);
        Assert.Contains("Spoofing", sections);
        Assert.Contains("Hotfix", sections);
        Assert.True(list.Count > 300, $"{list.Count} settings");
        Assert.Equal(["auto", "None", "Nukems", "Arturs", "FFX"], Get(list, "FGNvngxReplacement").Options.Select(option => option.Value));
        Assert.Equal(["auto", "nofg", "fsrfg", "xefg", "dlssg"], Get(list, "FGOutput").Options.Select(option => option.Value));
        // [FrameGen] Enabled and [DLSS] Enabled are two settings (a key is "Section|Key").
        Assert.Equal(2, list.Count(entry => entry.Key == "Enabled" && entry.Section is "FrameGen" or "DLSS"));
        Assert.Equal(OptiScalerIniKind.Text, list.Single(entry => entry.Id == "Framerate|FramerateLimit").Kind);
    }

    [Theory]
    [InlineData("  F5 super ini  ", "F5 super ini")]
    [InlineData("a/b:c*?", "abc")]
    [InlineData("name...", "name")]
    [InlineData("   ", null)]
    [InlineData("<>|", null)]
    [InlineData("../../etc/passwd", "....etcpasswd")]
    public void LibraryCleanName(string input, string? expected) => Assert.Equal(expected, OptiScalerIniLibrary.CleanName(input));

    [Theory]
    [InlineData("FrameGen", "FGOutput", "dlssg", true)]
    [InlineData("FrameGen", "FGOutput", "a\nb", false)]
    [InlineData("Frame]Gen", "FGOutput", "x", false)]
    [InlineData("FrameGen", "FG=Output", "x", false)]
    public void OnlyPlainNamesAndOneLineValues_AreWritten(string section, string key, string value, bool writable)
        => Assert.Equal(writable, OptiScalerIniCatalog.IsWritableName(section) && OptiScalerIniCatalog.IsWritableName(key) && OptiScalerIniCatalog.IsWritableValue(value));
}

public sealed partial class OptiScalerCustomBuildTests
{
    [Fact]
    public async Task AllSettings_DescribedByTheInstalledBuild_ApplyWritesWithABackup_AndAReinstallOrSwitchWritesThemAgain()
    {
        var a = (await Import(WriteBuild(Path.Combine(_root, "as-a"), os10: true, marker: "A",
            iniExtra: "[FrameGen]\n; nofg, fsrfg, F5flow - Default (auto) is nofg\nFGOutput=auto\n"), "Build A")).Build!;
        var b = (await Import(WriteBuild(Path.Combine(_root, "as-b"), os10: true, marker: "B",
            iniExtra: "[FrameGen]\n; nofg, fsrfg - Default (auto) is nofg\nFGOutput=auto\n"), "Build B")).Build!;
        var (game, store) = Game("all-settings");
        var managed = Managed(store, out var optiscaler);
        optiscaler.ReferenceIniDirectory = Path.Combine(_root, "reference");
        managed.OptiScalerIniChoices = new OptiScalerIniChoicesStore(Path.Combine(_root, "ini-choices.json"));
        Assert.True((await managed.InstallOptiScalerAsync(game, a.Variant)).Success);

        // The build's own INI lists its options (a value only Build A offers).
        var entries = managed.GetOptiScalerIniEntries(game)!;
        Assert.Contains(entries.Single(entry => entry.Id == "FrameGen|FGOutput").Options, option => option.Value == "F5flow");

        var applied = await managed.ApplyOptiScalerIniEditsAsync(game, new Dictionary<string, string> { ["FrameGen|FGOutput"] = "F5flow", ["Plugins|NewKey"] = "1" });
        Assert.True(applied.Success, applied.Message);
        Assert.StartsWith(OptiScalerDeploymentService.AllSettingsAppliedPrefix + "2" + OptiScalerDeploymentService.FreshIniBackupPrefix, applied.Message, StringComparison.Ordinal);
        var ini = Path.Combine(game.BinaryDirectory!, "OptiScaler.ini");
        Assert.Equal("F5flow", OptiScalerIniEditor.GetValueInFile(ini, "FrameGen", "FGOutput"));
        Assert.Equal("1", OptiScalerIniEditor.GetValueInFile(ini, "Plugins", "NewKey"));
        Assert.Single(Directory.GetFiles(optiscaler.FreshIniBackupDirectory, "*.ini", SearchOption.AllDirectories));
        Assert.Equal(2, managed.GetOptiScalerIniChoices(game).Edits.Count);

        // A refused value writes nothing.
        var refused = await managed.ApplyOptiScalerIniEditsAsync(game, new Dictionary<string, string> { ["FrameGen|FGOutput"] = "x\n[Evil]" });
        Assert.StartsWith(OptiScalerDeploymentService.AllSettingsValueRefusedPrefix, refused.Message, StringComparison.Ordinal);
        Assert.Equal("F5flow", OptiScalerIniEditor.GetValueInFile(ini, "FrameGen", "FGOutput"));

        // A version switch installs a fresh INI; the applied edits are written again (Build B describes FGOutput its own way).
        Assert.True((await managed.SwitchOptiScalerVersionAsync(game, b.Variant)).Success);
        Assert.Equal("F5flow", OptiScalerIniEditor.GetValueInFile(ini, "FrameGen", "FGOutput"));
        Assert.Equal("1", OptiScalerIniEditor.GetValueInFile(ini, "Plugins", "NewKey")); // Build B's INI has no NewKey: only the saved edit puts it back
        Assert.DoesNotContain(managed.GetOptiScalerIniEntries(game)!.Single(entry => entry.Id == "FrameGen|FGOutput").Options,
            option => option.Value == "F5flow"); // Build B's own options now

        // Removing OptiScaler forgets them (upstream); the next install starts from the template.
        Assert.True((await managed.RemoveAsync(game, ManagedComponentKind.OptiScaler)).Success);
        Assert.Empty(managed.GetOptiScalerIniChoices(game).Edits);
        Assert.True((await managed.InstallOptiScalerAsync(game, a.Variant)).Success);
        Assert.Equal("auto", OptiScalerIniEditor.GetValueInFile(ini, "FrameGen", "FGOutput"));
    }

    [Fact]
    public async Task SavedInis_SaveFromAGame_LoadIntoAnother_AndAreWrittenAgainAfterAReinstall()
    {
        var a = (await Import(WriteBuild(Path.Combine(_root, "si-a"), os10: true, marker: "A"), "Build A")).Build!;
        var (game, store) = Game("saved-ini");
        var managed = Managed(store, out var optiscaler);
        optiscaler.ReferenceIniDirectory = Path.Combine(_root, "reference-si");
        managed.OptiScalerIniChoices = new OptiScalerIniChoicesStore(Path.Combine(_root, "ini-choices-si.json"));
        managed.OptiScalerIniLibrary = new OptiScalerIniLibrary(Path.Combine(_root, "saved-inis"));
        Assert.True((await managed.InstallOptiScalerAsync(game, a.Variant)).Success);
        var ini = Path.Combine(game.BinaryDirectory!, "OptiScaler.ini");
        OptiScalerIniEditor.SetValuesInFile(ini, [("Plugins", "Mine", "super")]);

        Assert.Equal(LinuxManagedComponentService.SaveIniNameRequiredMessage, managed.SaveOptiScalerIni(game, " / ").Message);
        Assert.True(managed.SaveOptiScalerIni(game, "F5 super ini").Success);
        Assert.Equal(["F5 super ini"], managed.OptiScalerIniLibrary.List());
        Assert.Equal("F5 super ini", managed.GetOptiScalerIniChoices(game).SavedIni);

        // Another file is loaded; the saved one goes back in after a reinstall (the switch keeps choices; remove forgets them).
        OptiScalerIniEditor.SetValuesInFile(ini, [("Plugins", "Mine", "changed")]);
        var loaded = await managed.LoadSavedOptiScalerIniAsync(game, "F5 super ini");
        Assert.True(loaded.Success, loaded.Message);
        Assert.Equal("super", OptiScalerIniEditor.GetValueInFile(ini, "Plugins", "Mine"));
        Assert.Equal("true", OptiScalerIniEditor.GetValueInFile(ini, "Plugins", "LoadAsiPlugins")); // RHI's own setting
        Assert.StartsWith(OptiScalerDeploymentService.SavedIniMissingPrefix, (await managed.LoadSavedOptiScalerIniAsync(game, "nope")).Message, StringComparison.Ordinal);

        // A version switch installs Build B's template (no "Mine" key): the saved ini is put in again.
        var b = (await Import(WriteBuild(Path.Combine(_root, "si-b"), os10: true, marker: "B"), "Build B")).Build!;
        Assert.True((await managed.SwitchOptiScalerVersionAsync(game, b.Variant)).Success);
        Assert.Equal("super", OptiScalerIniEditor.GetValueInFile(ini, "Plugins", "Mine"));

        // Add from file: a non-INI is refused; a name never replaces a saved one by accident.
        var binary = Path.Combine(_root, "binary.ini");
        File.WriteAllBytes(binary, [1, 0, 2]);
        Assert.Throws<InvalidDataException>(() => managed.OptiScalerIniLibrary.Save("bin", binary));
        Assert.Equal("F5 super ini (2)", managed.OptiScalerIniLibrary.SuggestName(Path.Combine(_root, "F5 super ini.ini")));

        managed.DeleteSavedOptiScalerIni("F5 super ini", game);
        Assert.Empty(managed.OptiScalerIniLibrary.List());
        Assert.Null(managed.GetOptiScalerIniChoices(game).SavedIni);
    }
}
