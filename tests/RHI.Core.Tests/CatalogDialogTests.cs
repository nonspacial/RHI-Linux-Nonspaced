using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// The RenoDX ⚙, Shaders/Add-ons and shader-pack Files dialogs as view models (epic #89: were built in the window's
/// code-behind): what each shows for a snapshot, what a choice writes, and what a refusal puts back or keeps open.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class CatalogDialogTests
{
    private static RenoDxCompatibilitySetting Setting(string key, string current, params string[] values)
        => new(key, key + " label", current, values.Select(value => new RenoDxCompatibilityOption(value, "Option " + value)).ToArray(), false);

    [Fact]
    public void RenoDx_WithoutItsSection_SaysWhy_AndShowsNoRows()
    {
        UiLocalizationService.Apply("en-US");
        var noSection = new RenoDxSettingsDialogViewModel(new(null, false, [Setting("Set_Path", "0", "0", "1")], null), "Tunic", (_, _) => throw new InvalidOperationException());
        Assert.Equal(UiLocalizationService.Text("Win.RunGameOnceRenoDXInstalledGenerate"), noSection.Message);
        Assert.False(noSection.HasRows);
        Assert.False(noSection.HasWarning);
        Assert.Equal(UiLocalizationService.Format("Win.RenoDxCompatibilityTitle", "Tunic"), noSection.WindowTitle);

        var warned = new RenoDxSettingsDialogViewModel(new("/g/renodx.ini", false, [], "unreadable"), null, (_, _) => throw new InvalidOperationException());
        Assert.Equal("unreadable", warned.Message);
        Assert.True(warned.HasWarning);
        Assert.Equal("/g/renodx.ini", warned.IniPath);

        var empty = new RenoDxSettingsDialogViewModel(new("/g/renodx.ini", true, [], null), "Tunic", (_, _) => throw new InvalidOperationException());
        Assert.Equal(UiLocalizationService.Text("Win.RenodxSectionExistsButNoCompatibility"), empty.Message);
        Assert.False(empty.HasRows);
    }

    [Fact]
    public void RenoDx_AChoiceIsWrittenAtOnce_AndARefusedOneGoesBack()
    {
        UiLocalizationService.Apply("en-US");
        var writes = new List<(string Key, string Value)>();
        var refuse = false;
        var dialog = new RenoDxSettingsDialogViewModel(
            new("/g/renodx.ini", true, [Setting("Upgrade_R10G10B10A2_UNORM", "1", "0", "1", "2"), Setting("Unknown", "zz", "a", "b")], null), "Tunic",
            (key, value) => { writes.Add((key, value)); return refuse ? ComponentOperationResult.Fail("refused") : ComponentOperationResult.Ok("saved"); });

        var row = dialog.Rows[0];
        Assert.Equal(1, row.SelectedIndex); // the value in the file
        Assert.Equal(0, dialog.Rows[1].SelectedIndex); // not among the options: the first
        Assert.Equal(["Option 0", "Option 1", "Option 2"], row.Options);
        Assert.Contains("R10G10B10A2_UNORM", row.Tooltip, StringComparison.Ordinal);
        Assert.Null(dialog.Message);

        row.SelectedIndex = 2;
        Assert.Equal(("Upgrade_R10G10B10A2_UNORM", "2"), writes.Single());
        Assert.Equal("saved", dialog.Message);
        Assert.Equal(2, row.SelectedIndex);

        refuse = true;
        row.SelectedIndex = 0;
        Assert.Equal(("Upgrade_R10G10B10A2_UNORM", "0"), writes[^1]);
        Assert.Equal("refused", dialog.Message);
        Assert.Equal(2, row.SelectedIndex); // back to the last accepted value
        Assert.Equal(2, writes.Count); // putting it back writes nothing
    }

    private static ShaderAddonManagerUiSnapshot Catalog() => new(
        [
            new ReShadeShaderPackInfo("Zeta", "zeta pack", "", "Extra"),
            new ReShadeShaderPackInfo("Alpha", "alpha pack", "Alpha shaders", "Essential", Requires: ["Zeta"]),
            new ReShadeShaderPackInfo("Mine", "my pack", "", "Custom", IsCustom: true),
            new ReShadeShaderPackInfo("Beta", "Beta pack", "", "Extra"),
        ],
        ["alpha"],
        [
            new ReShadeAddonCatalogItem("managed", "Managed one", "", null, false, true, false, true),
            new ReShadeAddonCatalogItem("b", "b addon", "", null, true, false, true, true),
            new ReShadeAddonCatalogItem("a", "A addon", "", null, false, false, true, false),
        ],
        ["managed", "b"],
        "/custom/shaders",
        "/custom/addons");

    [Fact]
    public async Task Shaders_ListsByCategory_AndAppliesTheTickedOnes()
    {
        UiLocalizationService.Apply("en-US");
        (IReadOnlyList<string> Shaders, IReadOnlyList<string> Addons)? applied = null;
        var dialog = new ShaderAddonsDialogViewModel(Catalog(), "Tunic",
            (shaders, addons) => { applied = (shaders, addons); return Task.FromResult(ComponentOperationResult.Ok("done")); },
            _ => (true, null), _ => Task.CompletedTask, _ => { });

        Assert.Equal("Shaders/Add-ons — Tunic", dialog.WindowTitle);
        Assert.Equal(UiLocalizationService.Format("Win.CatalogManagedShaderPackSLocal", 3, 3), dialog.Status);
        // Categories in catalogue order (upper case), packs by name within each.
        Assert.Equal(["EXTRA", "Beta pack", "zeta pack", "ESSENTIAL", "alpha pack", "CUSTOM", "my pack"],
            dialog.ShaderItems.Select(item => item switch { CatalogHeading h => h.Text, ShaderPackRow r => r.DisplayName, _ => "?" }));
        Assert.Equal(["Alpha"], dialog.TickedShaderIds());
        var alpha = dialog.ShaderItems.OfType<ShaderPackRow>().Single(row => row.Id == "Alpha");
        Assert.Equal($"Alpha shaders{UiLocalizationService.Format("Win.Requires", "Zeta")}\nID: Alpha", alpha.Tooltip);
        // Add-ons: other components' last and locked; custom ones say so.
        Assert.Equal(["a", "b", "managed"], dialog.Addons.Select(row => row.Id));
        Assert.False(dialog.Addons[2].IsEnabled);
        Assert.Equal(UiLocalizationService.Format("Win.Custom2", "b addon"), dialog.Addons[1].Label);
        Assert.Equal(UiLocalizationService.Format("Win.ArchitecturesID", UiLocalizationService.Format("Win.ReShadeAdd", "A addon"), "x86", "a"), dialog.Addons[0].Tooltip);

        dialog.TickShaders(["zeta", "BETA"]);
        await dialog.ApplyCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["Beta", "Zeta"], applied!.Value.Shaders.Order(StringComparer.Ordinal));
        Assert.Equal(["b"], applied.Value.Addons); // the managed add-on is never sent
        Assert.Equal("done", dialog.Status);
        Assert.True(dialog.CanApply);
    }

    [Fact]
    public async Task Shaders_FolderAndFilesFailures_AreReported()
    {
        UiLocalizationService.Apply("en-US");
        var opened = new List<string>();
        var mainStatus = "";
        var dialog = new ShaderAddonsDialogViewModel(Catalog(), "Tunic", (_, _) => Task.FromResult(ComponentOperationResult.Ok("")),
            folder => { opened.Add(folder); return (false, "no handler"); },
            _ => throw new IOException("index gone"), line => mainStatus = line);

        dialog.OpenCustomShadersCommand.Execute(null);
        Assert.Equal(ErrorLineText.CustomShadersNotOpened("no handler"), dialog.Status);
        dialog.OpenCustomAddonsCommand.Execute(null);
        Assert.Equal(ErrorLineText.CustomAddonsNotOpened("no handler"), dialog.Status);
        Assert.Equal(["/custom/shaders", "/custom/addons"], opened);

        await dialog.ShaderItems.OfType<ShaderPackRow>().First().FilesCommand.ExecuteAsync(null);
        Assert.Equal(UiLocalizationService.Format("Win.CouldNotOpenFiles", "Beta pack", UiLocalizationService.Present("index gone")), mainStatus);
    }

    [Fact]
    public void Files_SaveStoresTheUnticked_AndARefusalKeepsTheDialogOpen()
    {
        UiLocalizationService.Apply("en-US");
        var pack = new ReShadeShaderPackInfo("Alpha", "alpha pack", "");
        ReShadeShaderPackFileInfo File(string path, bool texture, bool excluded) => new("Alpha", "sel:" + path, path, texture, excluded);
        string? refusal = "not saved";
        IReadOnlyList<string>? stored = null;
        var dialog = new ShaderPackFilesDialogViewModel(pack,
            [File("b.fx", false, false), File("t.png", true, true), File("a.fx", false, true)],
            (id, excluded) => { Assert.Equal("Alpha", id); stored = excluded; return refusal; });
        var closes = 0;
        dialog.CloseRequested += () => closes++;

        Assert.Equal(["Shaders", "a.fx", "b.fx", UiLocalizationService.Text("Win.Textures"), "t.png"],
            dialog.Items.Select(item => item switch { CatalogHeading h => h.Text, ShaderPackFileRow r => r.DisplayPath, _ => "?" }));
        Assert.True(dialog.CanSave);
        Assert.Equal(UiLocalizationService.Format("Win.ShaderTexturesTitle", "alpha pack"), dialog.WindowTitle);

        dialog.SaveCommand.Execute(null);
        Assert.Equal(["sel:a.fx", "sel:t.png"], stored);
        Assert.Equal("not saved", dialog.Status);
        Assert.Equal(0, closes);

        refusal = null;
        dialog.Items.OfType<ShaderPackFileRow>().Single(row => row.DisplayPath == "t.png").IsChecked = true;
        dialog.SaveCommand.Execute(null);
        Assert.Equal(["sel:a.fx"], stored);
        Assert.Equal(UiLocalizationService.Format("Win.SelectionSavedActiveFileS", 2, 3), dialog.Status);
        Assert.Equal(1, closes);

        var none = new ShaderPackFilesDialogViewModel(pack, [], (_, _) => null);
        Assert.False(none.CanSave);
        Assert.Equal(UiLocalizationService.Text("Win.NoUsableShaderTextureWasFound"), none.Note);
    }
}
