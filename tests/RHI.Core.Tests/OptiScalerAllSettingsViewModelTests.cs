using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

/// <summary>#272: the ⚙'s All Settings and Save ini / Load saved ini, as the dialog drives them (upstream 2.9.3).</summary>
[Collection(UiLanguageCollection.Name)]
public sealed class OptiScalerAllSettingsViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rhi-os-all-vm-{Guid.NewGuid():N}");

    public OptiScalerAllSettingsViewModelTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private const string Ini = "[Upscalers]\n; xess, fsr22, dlss - Default (auto) is xess\nDx12Upscaler=auto\n\n[FrameGen]\n; true or false - Default (auto) is false\nEnabled=auto\n"
                               + "; nofg, fsrfg - Default (auto) is nofg\nFGOutput=auto\n\n[Plugins]\n; text\nPath=plugins\n";

    [Fact]
    public async Task AllSettings_NothingIsWrittenBeforeApply_ApplyWritesAndReopens_RevertAndForget()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = await AddManagedGameAsync(fx, "All");
        using var dialog = new OptiScalerSettingsDialogViewModel(fx.Vm, fx.Vm.GetOptiScalerAdvancedSettings());
        var all = Assert.IsType<OptiScalerAllSettingsPresenter>(dialog.AllSettings);
        var reopened = 0;
        dialog.ReopenRequested += () => reopened++;

        Assert.False(all.IsOpen);
        all.Toggle();
        Assert.Equal(["Upscalers", "FrameGen", "Plugins"], all.Sections);
        Assert.Equal("Upscalers", all.SelectedSection);
        var row = Assert.Single(all.Rows);
        Assert.Equal(["auto (default: xess)", "xess", "fsr22", "dlss"], row.Options);

        all.SelectedSection = "FrameGen";
        all.Rows.Single(r => r.Entry.Key == "FGOutput").SelectedIndex = 2; // fsrfg
        all.Rows.Single(r => r.Entry.Key == "Enabled").SelectedIndex = 1; // true
        Assert.Equal("Apply 2 change(s)", all.ApplyText);
        var ini = Path.Combine(game.BinaryDirectory!, "OptiScaler.ini");
        Assert.Equal("auto", OptiScalerIniEditor.GetValueInFile(ini, "FrameGen", "FGOutput")); // nothing written yet

        // Search across sections (up to 60), the section picker is off meanwhile; a pending value shows in the results.
        all.SearchText = "fgout";
        Assert.False(all.CanChooseSection);
        Assert.Equal("1 match(es).", all.Note);
        Assert.Equal("FGOutput (FrameGen)", Assert.Single(all.Rows).Label);
        Assert.Equal(2, all.Rows[0].SelectedIndex);
        all.SearchText = "";

        await all.ApplyAsync();
        Assert.Equal("fsrfg", OptiScalerIniEditor.GetValueInFile(ini, "FrameGen", "FGOutput"));
        Assert.Equal("true", OptiScalerIniEditor.GetValueInFile(ini, "FrameGen", "Enabled"));
        Assert.Equal("Applied 2 change(s) to OptiScaler.ini.", dialog.Message);
        Assert.Equal(1, reopened);
        Assert.Equal(2, fx.Managed.GetOptiScalerIniChoices(game).Edits.Count);
        Assert.True(all.HasSavedEdits);
        Assert.StartsWith("2 applied edit(s) saved", all.InfoText, StringComparison.Ordinal);

        // A text setting; Revert drops what was not applied.
        all.SelectedSection = "Plugins";
        all.Rows.Single().Text = " other ";
        Assert.True(all.CanApply);
        all.Revert();
        Assert.False(all.CanApply);
        Assert.Equal("plugins", all.Rows.Single().Text);

        all.ForgetSavedEdits();
        Assert.False(all.HasSavedEdits);
        Assert.Equal("fsrfg", OptiScalerIniEditor.GetValueInFile(ini, "FrameGen", "FGOutput")); // the file stays
    }

    [Fact]
    public async Task SavedInis_SaveUseDeleteAndAdd()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        fx.Managed.OptiScalerIniLibrary = new OptiScalerIniLibrary(Path.Combine(_root, "saved"));
        var game = await AddManagedGameAsync(fx, "Saver");
        using var dialog = new OptiScalerSettingsDialogViewModel(fx.Vm, fx.Vm.GetOptiScalerAdvancedSettings());
        var library = Assert.IsType<OptiScalerIniLibraryPresenter>(dialog.IniLibrary);
        var ini = Path.Combine(game.BinaryDirectory!, "OptiScaler.ini");

        library.SaveName = "";
        library.Save();
        Assert.Equal("Type a name first.", library.Message);
        library.SaveName = "F5 super ini";
        library.Save();
        Assert.Equal("Saved as \"F5 super ini\". Load it into any game with Load saved ini.", library.Message);
        Assert.Equal(["F5 super ini"], library.Saved);
        Assert.Equal("Replaces your saved ini \"F5 super ini\".", library.SaveNote);

        OptiScalerIniEditor.SetValuesInFile(ini, [("Plugins", "Path", "changed")]);
        var reopened = 0;
        dialog.ReopenRequested += () => reopened++;
        await library.UseAsync("F5 super ini");
        Assert.Equal("plugins", OptiScalerIniEditor.GetValueInFile(ini, "Plugins", "Path"));
        Assert.Equal(1, reopened);
        Assert.Equal("F5 super ini", library.Current);

        var shared = Path.Combine(_root, "Shared", "OptiScaler.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(shared)!);
        File.WriteAllText(shared, Ini);
        library.AddFromFile(shared);
        Assert.Equal("Added \"Shared\" to your saved inis.", library.Message);
        Assert.Equal(["F5 super ini", "Shared"], library.Saved);

        library.Delete("F5 super ini");
        Assert.Equal(["Shared"], library.Saved);
        Assert.Null(library.Current);
    }

    [Fact]
    public void PortugueseUi_HasTheAllSettingsLines()
    {
        UiLocalizationService.Apply("pt-BR");
        try
        {
            Assert.Equal("Mostrar todas as configurações", UiLocalizationService.Text("Osa.Show"));
            Assert.Equal("O ini salvo não foi encontrado: x", UiLocalizationService.Present(OptiScalerDeploymentService.SavedIniMissingPrefix + "x"));
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    private static async Task<DetectedGame> AddManagedGameAsync(ViewModelFixture fx, string name)
    {
        var game = fx.Game(name);
        PeArchitectureDetectorTests.WritePe(game.ExecutablePath!, 0x8664);
        File.WriteAllText(Path.Combine(game.BinaryDirectory!, "OptiScaler.ini"), Ini);
        var (added, message) = await fx.Vm.ManualGames.TryAddManualGameAsync(new()
        {
            Name = game.Name, InstallPath = game.InstallPath, ExecutablePath = game.ExecutablePath, Runner = "Manual Windows"
        });
        Assert.True(added, message);
        var card = fx.Vm.SelectedGame!;
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(card), GameName = card.Name, Component = ManagedComponentKind.OptiScaler,
            BinaryDirectory = card.BinaryDirectory!, Version = "1.0",
            Metadata = new(StringComparer.OrdinalIgnoreCase) { ["OptiScaler.Variant"] = "Nightly" },
            Files = [new LinuxDeployedFile { Path = Path.Combine(card.BinaryDirectory!, "OptiScaler.ini"), AllowUserChanges = true }]
        });
        return card;
    }
}
