using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #187 (parity T-12): four global OptiScaler preset slots (upstream <c>OsPresetService</c>/<c>OsPreset</c>: first run
/// seeds "DLSS Enabler"; Save captures the ⚙ settings; a slot applies them to the selected game through the ⚙ path) and
/// the global overlay key (upstream <c>ApplyOsHotkey_Click</c>: written into every template and every installed game).
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class OptiScalerPresetsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rhi-os-presets-{Guid.NewGuid():N}");

    public OptiScalerPresetsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var file in Directory.Exists(_root) ? Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories) : [])
            try { File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch (IOException) { }
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    // ---- store -----------------------------------------------------------------------------------------------------

    [Fact]
    public void FirstRun_HasUpstreamsDlssEnablerSlot_AndWritesNothing_ThenKeepsExactlyFourSlots()
    {
        var store = new OptiScalerPresetStore(Path.Combine(_root, "optiscaler-presets.json"));

        var slots = store.Load();

        Assert.Equal(4, slots.Slots.Count);
        Assert.Equal("DLSS Enabler", slots.Slots[0]!.Name);
        Assert.Equal("Arturs", slots.Slots[0]!.Settings["FGNvngxReplacement"]);
        Assert.Equal("true", slots.Slots[0]!.Settings[OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId]);
        Assert.All(slots.Slots.Skip(1), Assert.Null);
        Assert.False(File.Exists(store.FilePath));

        store.Save(new OptiScalerPresetSlots { Slots = [null, new() { Name = " Mine ", Settings = new() { ["FGInput"] = "upscaler" } }, null, null, new()] });
        var saved = store.Load();
        Assert.Equal(4, saved.Slots.Count);
        Assert.Null(saved.Slots[0]); // the user's file is what loads now
        Assert.Equal("Mine", saved.Slots[1]!.Name);
    }

    [Fact]
    public void ApplyOrder_PutsTheStreamlineStackAndItsVersionFirst()
    {
        var preset = new OptiScalerPreset
        {
            Settings = new(StringComparer.Ordinal)
            {
                ["FGInput"] = "upscaler", ["StreamlineVersion"] = "2.12.0",
                [OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId] = "true", ["SrPreset"] = "3"
            }
        };

        var order = OptiScalerPresetsPresenter.ApplyOrder(preset).Select(pair => pair.Key).ToArray();

        Assert.Equal([OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId, "StreamlineVersion"], order[..2]);
    }

    // ---- presets through the view model ----------------------------------------------------------------------------

    [Fact]
    public async Task SaveCapturesTheGamesSettings_AndApplyChangesOnlyWhatThisGameOffers()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = await AddManagedOptiScalerGameAsync(fx, "Alpha", "[FrameGen]\nFGInput=auto\nFGOutput=auto\n[DLSS]\nRenderPresetOverride=false\n[Menu]\nShortcutKey=auto\n");
        var presets = fx.Vm.OptiScalerPresets;
        presets.Store = new OptiScalerPresetStore(Path.Combine(_root, "presets.json"));

        Assert.True(OptiScalerPresetsPresenter.AppliesTo(fx.Vm.GetOptiScalerAdvancedSettings()));
        // A change made in the ⚙ after it opened is what Save captures (read at the moment of saving).
        Assert.True((await fx.Vm.SetOptiScalerAdvancedSettingAsync("FGOutput", "dlssg")).Success);
        Assert.StartsWith("Preset \"Slot 2\" saved with ", presets.SaveSlot(1));
        Assert.Null(presets.Rename(1, " Quiet "));
        var saved = presets.Store.Load().Slots[1]!;
        Assert.Equal("Quiet", saved.Name);
        Assert.Equal("auto", saved.Settings["FGInput"]);
        Assert.Equal("dlssg", saved.Settings["FGOutput"]);
        Assert.DoesNotContain(OptiScalerDeploymentService.MenuShortcutKeySettingId, saved.Settings.Keys); // the global key row owns it

        Assert.Null(presets.Rename(2, "Mixed"));
        var store = presets.Store.Load();
        store.Slots[2]!.Settings = new(StringComparer.Ordinal)
        {
            ["FGInput"] = "upscaler", ["FGOutput"] = "dlssg", ["SrPreset"] = "3", ["NotASetting"] = "x", ["FramerateLimit"] = "999"
        };
        presets.Store.Save(store);
        presets.Store = new OptiScalerPresetStore(presets.Store.FilePath); // read the edited file again

        var line = await presets.ApplyAsync(2);

        Assert.Equal("Preset \"Mixed\" applied: 2 setting(s) changed. 2 do not apply to this game and were left out.", line);
        var bin = game.BinaryDirectory!;
        var ini = File.ReadAllText(Path.Combine(bin, "OptiScaler.ini"));
        Assert.Contains("FGInput=upscaler", ini);
        Assert.Contains("FGOutput=dlssg", ini);
        Assert.Contains("RenderPresetForAll=3", ini);
        Assert.DoesNotContain("999", ini);

        Assert.Equal("Preset \"Mixed\" applied: 0 setting(s) changed. 2 do not apply to this game and were left out.", await presets.ApplyAsync(2));
        Assert.Equal(UiLocalizationService.Text("Shell.PresetEmpty"), await presets.ApplyAsync(3));
    }

    [Fact]
    public async Task PresetsAreOfferedOnlyWhereUpstreamOffersThem_TheNightlyFamily()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        await AddManagedOptiScalerGameAsync(fx, "Stable", "[FrameGen]\nFGInput=auto\n", OptiScalerVariant.Stable);
        Assert.False(OptiScalerPresetsPresenter.AppliesTo(fx.Vm.GetOptiScalerAdvancedSettings()));
    }

    // ---- global overlay key ----------------------------------------------------------------------------------------

    [Fact]
    public void Templates_CarryTheGlobalKey_WhenSeededAndWhenApplied_AndKeepEverythingElse()
    {
        var folder = Path.Combine(_root, "inis");
        var choice = new OptiScalerTemplateChoice(OptiScalerGpuType.Nvidia, true, "0x24");
        var templates = new OptiScalerIniTemplates(folder, () => choice);

        var seeded = templates.EnsureSeeded(OptiScalerVariant.Nightly);
        Assert.Contains("\nShortcutKey=0x24", File.ReadAllText(seeded).Replace("\r\n", "\n"));

        var own = Path.Combine(folder, OptiScalerIniTemplates.FileName(OptiScalerVariant.Stable, choice));
        File.WriteAllText(own, "; mine\n[Menu]\nShortcutKey=auto\n[Upscalers]\nDx12Upscaler=xess\n");
        File.SetUnixFileMode(own, UnixFileMode.UserRead); // the user locked it

        var written = templates.ApplyMenuKey("0x74");
        Assert.Equal(2, written.Updated);
        Assert.Empty(written.Failures);

        Assert.Equal("; mine\n[Menu]\nShortcutKey=0x74\n[Upscalers]\nDx12Upscaler=xess\n", File.ReadAllText(own));
        Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(own)); // still locked
        Assert.Contains("ShortcutKey=0x74", File.ReadAllText(seeded));

        // Without a global key the template keeps its own.
        var keep = new OptiScalerIniTemplates(folder, () => new OptiScalerTemplateChoice(OptiScalerGpuType.Nvidia, true));
        keep.EnsureSeeded(OptiScalerVariant.Nightly);
        Assert.Contains("ShortcutKey=0x74", File.ReadAllText(seeded));
    }

    [Fact]
    public void Templates_AFileThatCannotBeWritten_IsCounted_AndTheRestStillGetTheKey()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "inis-broken")).FullName;
        var broken = Path.Combine(folder, "OptiScaler.a-broken.ini"); // sorts first
        File.WriteAllText(broken, "[Menu]\nShortcutKey=auto\n");
        File.SetUnixFileMode(broken, UnixFileMode.None); // neither readable nor writable
        var later = Path.Combine(folder, "OptiScaler.b.ini");
        File.WriteAllText(later, "[Menu]\nShortcutKey=auto\n");
        var last = Path.Combine(folder, "OptiScaler_nightly.c.ini");
        File.WriteAllText(last, "[Menu]\nShortcutKey=auto\n");

        var written = new OptiScalerIniTemplates(folder, () => new OptiScalerTemplateChoice(OptiScalerGpuType.Nvidia, true)).ApplyMenuKey("0x74");

        Assert.Equal(2, written.Updated);
        Assert.Equal("OptiScaler.a-broken.ini", Assert.Single(written.Failures).File);
        Assert.Contains("ShortcutKey=0x74", File.ReadAllText(later));
        Assert.Contains("ShortcutKey=0x74", File.ReadAllText(last));
    }

    [Fact]
    public async Task ApplyToAllGames_NamesATemplateThatKeptItsFile()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var templates = Directory.CreateDirectory(Path.Combine(fx.ConfigDir, "inis")).FullName;
        var broken = Path.Combine(templates, "OptiScaler.a-broken.ini");
        File.WriteAllText(broken, "[Menu]\nShortcutKey=auto\n");
        File.SetUnixFileMode(broken, UnixFileMode.None);
        File.WriteAllText(Path.Combine(templates, "OptiScaler_nightly.nvidia.ini"), "[Menu]\nShortcutKey=auto\n");
        try
        {
            var line = await fx.Vm.OptiScalerPresets.ApplyMenuKeyToAllAsync("0x74");

            Assert.StartsWith("OptiScaler menu key written to 0 game(s) and 1 INI template(s). 1 kept their file; first: OptiScaler.a-broken.ini — ", line);
        }
        finally { File.SetUnixFileMode(broken, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    }

    [Fact]
    public async Task APresetSaveThatIsRefused_ChangesNoSlot_AndALaterSaveDoesNotCarryIt()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var presets = fx.Vm.OptiScalerPresets;
        var file = Path.Combine(_root, "presets-refused.json");
        File.WriteAllText(file, "{ not json");
        presets.Store = new OptiScalerPresetStore(file);

        var refused = presets.Rename(1, "Mine");

        Assert.NotNull(refused); // the unreadable file is never saved over
        Assert.Equal(UiLocalizationService.Format("Shell.PresetSlot", 2), presets.Label(1)); // nothing adopted
        Assert.Null(presets.Slot(1));
        Assert.Equal("{ not json", File.ReadAllText(file));

        File.Delete(file); // the user removed the broken file
        Assert.Null(presets.Rename(3, "Other"));
        var saved = new OptiScalerPresetStore(file).Load();
        Assert.Equal("Other", saved.Slots[3]!.Name);
        Assert.Null(saved.Slots[1]); // the refused rename is not carried by the later save
    }

    [Fact]
    public async Task ApplyToAllGames_WritesEveryManagedGameAndTemplate_CountsRefusals_AndLeavesOthersAlone()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var alpha = await AddManagedOptiScalerGameAsync(fx, "Alpha", "[Menu]\nShortcutKey=auto\n");
        var beta = await AddManagedOptiScalerGameAsync(fx, "Beta", "[Menu]\nShortcutKey=auto\n");
        var plain = fx.Game("Plain");
        File.WriteAllText(Path.Combine(plain.BinaryDirectory!, "OptiScaler.ini"), "[Menu]\nShortcutKey=auto\n");
        Assert.True((await fx.Vm.ManualGames.TryAddManualGameAsync(new() { Name = "Plain", InstallPath = plain.InstallPath, ExecutablePath = plain.ExecutablePath })).Added);
        // A game without RHI's OptiScaler is not part of the batch, even one whose folder would be refused.
        fx.Vm.Games.Single(row => row.Game.Name == "Plain").Game.GameRootNotAllowed = true;
        var templates = Path.Combine(fx.ConfigDir, "inis");
        Directory.CreateDirectory(templates);
        File.WriteAllText(Path.Combine(templates, "OptiScaler_nightly.nvidia.ini"), "[Menu]\nShortcutKey=auto\n");
        // Beta's folder is no longer a confirmed patch target: refused and counted, its file kept.
        var betaCard = fx.Vm.Games.Single(row => row.Game.Name == "Beta").Game;
        betaCard.GameRootNotAllowed = true;

        var line = await fx.Vm.OptiScalerPresets.ApplyMenuKeyToAllAsync("0x74");

        Assert.StartsWith("OptiScaler menu key written to 1 game(s) and 1 INI template(s). 1 kept their file; first: Beta — ", line);
        Assert.Contains("ShortcutKey=0x74", File.ReadAllText(Path.Combine(alpha.BinaryDirectory!, "OptiScaler.ini")));
        Assert.Contains("ShortcutKey=auto", File.ReadAllText(Path.Combine(beta.BinaryDirectory!, "OptiScaler.ini")));
        Assert.Contains("ShortcutKey=auto", File.ReadAllText(Path.Combine(plain.BinaryDirectory!, "OptiScaler.ini")));
        Assert.Contains("ShortcutKey=0x74", File.ReadAllText(Path.Combine(templates, "OptiScaler_nightly.nvidia.ini")));
    }

    [Fact]
    public async Task ApplyToAllGames_SavesTheKey_AndIsRefusedWhileAnotherOperationRuns()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        Assert.Null(fx.Vm.Settings.SaveOptiScalerMenuKey("0x2E"));
        Assert.Equal("0x2E", fx.Settings.Load().OptiScalerMenuKey);
        Assert.Equal("0x2E", OptiScalerIniTemplates.ChoiceFrom(fx.Settings.Load()).MenuKey);

        using (fx.Vm.Operations.TryBegin("test"))
            Assert.Equal(UiLocalizationService.Text("Vm.AnotherRHIOperationAlreadyRunning"), await fx.Vm.OptiScalerPresets.ApplyMenuKeyToAllAsync("0x2E"));
        Assert.Equal(UiLocalizationService.Text("Shell.MenuKeyChooseFirst"), await fx.Vm.OptiScalerPresets.ApplyMenuKeyToAllAsync(""));
    }

    [Theory]
    [InlineData("0x2d", "0x2D")]
    [InlineData(" auto ", "auto")]
    [InlineData("-1", "-1")]
    [InlineData("0x00", "")]
    [InlineData("0x100", "")]
    [InlineData("Insert", "")]
    [InlineData(null, "")]
    public void MenuKeySetting_KeepsOnlyKeysOptiScalerReads(string? stored, string expected)
        => Assert.Equal(expected, new LinuxAppSettings { OptiScalerMenuKey = stored! }.Normalize().OptiScalerMenuKey);

    [Fact]
    public void MenuKeyList_IsUpstreamsKeysAsVirtualKeyCodes()
    {
        var values = OptiScalerDeploymentService.MenuKeyOptions.Select(option => (option.Value, option.Name)).ToArray();
        Assert.Contains(("0x2D", "Insert"), values);
        Assert.Contains(("0x2E", "Delete"), values);
        Assert.Contains(("0x21", "Page Up"), values);
        Assert.Contains(("0x70", "F1"), values);
        Assert.Contains(("0x7B", "F12"), values);
        Assert.Equal("", OptionLists.OptiScalerMenuKey.Fallback);
    }

    [Fact]
    public void PortugueseUi_HasThePresetLines()
    {
        UiLocalizationService.Apply("pt-BR");
        try
        {
            Assert.Equal("Espaço 2", UiLocalizationService.Format("Shell.PresetSlot", 2));
            Assert.Equal("Como no modelo de INI", OptionLists.OptiScalerMenuKey.Display(""));
            Assert.Equal("Page Up", UiLocalizationService.Present("Page Up"));
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    private static async Task<DetectedGame> AddManagedOptiScalerGameAsync(ViewModelFixture fx, string name, string ini,
        OptiScalerVariant variant = OptiScalerVariant.Nightly)
    {
        var game = fx.Game(name);
        PeArchitectureDetectorTests.WritePe(game.ExecutablePath!, 0x8664);
        File.WriteAllText(Path.Combine(game.BinaryDirectory!, "OptiScaler.ini"), ini);
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
            Metadata = new(StringComparer.OrdinalIgnoreCase) { ["OptiScaler.Variant"] = variant.ToString() },
            Files = [new LinuxDeployedFile { Path = Path.Combine(card.BinaryDirectory!, "OptiScaler.ini"), AllowUserChanges = true }]
        });
        return card;
    }
}
