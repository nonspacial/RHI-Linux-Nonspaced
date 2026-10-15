using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// #92: system.reg (HKLM) edits through WineRegistryEditor, the registry value service and its journal steps, and crash
// recovery of a Vulkan-layer install.
public sealed partial class ReShadeChannelTests
{
    private const string LayerKey = @"Software\Khronos\Vulkan\ImplicitLayers";
    private const string LayerName = @"C:\ProgramData\RHI-Linux\ReShadeVulkan\game-1\ReShade64.json";

    private string NewPrefix(string name)
    {
        var prefix = Path.Combine(_root, name, "pfx");
        Directory.CreateDirectory(prefix);
        File.WriteAllText(Path.Combine(prefix, "system.reg"), SystemRegText);
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n");
        return prefix;
    }

    [Fact]
    public void SystemHive_Edit_IsRefusedWhileWineRuns_AndBackedUpUnderItsOwnName()
    {
        var prefix = NewPrefix("hklm-editor");
        var hive = Path.Combine(prefix, "system.reg");
        var before = File.ReadAllBytes(hive);

        var refused = Assert.Throws<WineRegistryEditRefusedException>(() =>
            new WineRegistryValueService(new WineRegistryEditor(_ => true)).Ensure(prefix, WineRegistryValueService.SystemHive, LayerKey, LayerName, "dword:00000000"));
        Assert.True(refused.PrefixInUse);
        Assert.Equal(before, File.ReadAllBytes(hive));

        new WineRegistryValueService(new WineRegistryEditor(_ => false)).Ensure(prefix, WineRegistryValueService.SystemHive, LayerKey, LayerName, "dword:00000000");

        var backup = Assert.Single(WineRegistryEditor.Backups(hive));
        Assert.StartsWith(hive + WineRegistryEditor.BackupMarker, backup, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(backup));
        Assert.True(File.Exists(hive + WineRegistryEditor.LockSuffix));
        Assert.Empty(WineRegistryEditor.Backups(Path.Combine(prefix, "user.reg")));
        Assert.Equal("WINE REGISTRY Version 2\n", File.ReadAllText(Path.Combine(prefix, "user.reg")));
    }

    [Fact]
    public void SystemHive_MissingFile_IsRefusedWithItsOwnMessage()
    {
        var prefix = NewPrefix("hklm-missing");
        File.Delete(Path.Combine(prefix, "system.reg"));
        var error = Assert.Throws<FileNotFoundException>(() =>
            new WineRegistryValueService(new WineRegistryEditor(_ => false)).Ensure(prefix, WineRegistryValueService.SystemHive, LayerKey, LayerName, "dword:00000000"));
        Assert.Equal(WineRegistryEditor.MissingSystemHiveMessage, error.Message);
    }

    [Fact]
    public void RegistryValue_EscapesTheWindowsPathAsWineWritesIt_AndRestoresAnEarlierValueOfAnotherType()
    {
        var prefix = NewPrefix("hklm-escape");
        var hive = Path.Combine(prefix, "system.reg");
        var name = "C:\\Prog \"x\"\\caf\u00e9.json";
        File.WriteAllText(hive, SystemRegText + "\n[Software\\\\Khronos\\\\Vulkan\\\\ImplicitLayers] 1\n\"C:\\\\Prog \\\"x\\\"\\\\caf\\xe9.json\"=\"old\"\n\"Other\"=dword:00000001\n");
        var before = File.ReadAllBytes(hive);
        var values = new WineRegistryValueService(new WineRegistryEditor(_ => false));

        var record = values.Ensure(prefix, WineRegistryValueService.SystemHive, LayerKey, name, "dword:00000000");

        Assert.Equal("\"old\"", record.PreviousValue);
        Assert.False(record.SectionCreatedByRhi);
        Assert.Contains("\"C:\\\\Prog \\\"x\\\"\\\\caf\\xe9.json\"=dword:00000000", File.ReadAllText(hive), StringComparison.Ordinal);
        Assert.Equal(WineOverrideRestoreOutcome.Restored, values.Restore(prefix, record).Outcome);
        Assert.Equal(before, File.ReadAllBytes(hive));
        Assert.Equal(WineOverrideRestoreOutcome.AlreadyRestored, values.Restore(prefix, record).Outcome);
    }

    [Fact]
    public void RegistryValue_Restore_LeavesASectionItDidNotCreate_AndKeepsOtherValues()
    {
        var prefix = NewPrefix("hklm-section");
        var hive = Path.Combine(prefix, "system.reg");
        File.WriteAllText(hive, SystemRegText + "\n[Software\\\\Khronos\\\\Vulkan\\\\ImplicitLayers] 1\n\"C:\\\\other.json\"=dword:00000000\n");
        var before = File.ReadAllBytes(hive);
        var values = new WineRegistryValueService(new WineRegistryEditor(_ => false));

        var record = values.Ensure(prefix, WineRegistryValueService.SystemHive, LayerKey, LayerName, "dword:00000000");
        values.Restore(prefix, record);

        Assert.Equal(before, File.ReadAllBytes(hive));
    }

    [Fact]
    public void RegistryValueCompensator_UndoesRhisValue_DefersWhileWineRuns_AndKeepsALaterOne()
    {
        var prefix = NewPrefix("hklm-compensator");
        var hive = Path.Combine(prefix, "system.reg");
        var before = File.ReadAllBytes(hive);
        var values = new WineRegistryValueService(new WineRegistryEditor(_ => false));
        var plan = values.Plan(prefix, WineRegistryValueService.SystemHive, LayerKey, LayerName, "dword:00000000");
        var step = WineRegistryValueCompensator.Describe(prefix, plan);

        // Never written (the edit was refused): done at once.
        Assert.Equal(CompensationStatus.Done, new WineRegistryValueCompensator(values).Compensate(step).Status);

        values.Ensure(prefix, WineRegistryValueService.SystemHive, LayerKey, LayerName, "dword:00000000");
        Assert.Equal(CompensationStatus.Deferred, new WineRegistryValueCompensator(values, _ => true).Compensate(step).Status);
        Assert.Equal(CompensationStatus.Done, new WineRegistryValueCompensator(values, _ => false).Compensate(step).Status);
        Assert.Equal(before, File.ReadAllBytes(hive));

        values.Ensure(prefix, WineRegistryValueService.SystemHive, LayerKey, LayerName, "dword:00000001");
        Assert.Equal(CompensationStatus.KeptLaterChange, new WineRegistryValueCompensator(values, _ => false).Compensate(step).Status);
        Assert.Contains("=dword:00000001", File.ReadAllText(hive), StringComparison.Ordinal);
    }

    [Fact]
    public void RegistryValueCompensator_LeavesTheValueToALaterRecordThatHoldsIt()
    {
        var prefix = NewPrefix("hklm-superseded");
        var values = new WineRegistryValueService(new WineRegistryEditor(_ => false));
        var plan = values.Plan(prefix, WineRegistryValueService.SystemHive, LayerKey, LayerName, "dword:00000000");
        var step = WineRegistryValueCompensator.Describe(prefix, plan, DateTime.UtcNow.AddMinutes(-1));
        var written = values.Ensure(prefix, WineRegistryValueService.SystemHive, LayerKey, LayerName, "dword:00000000");
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "hklm-superseded", "records.json"));
        // The user installed again while the undo waited; that install found the value already there.
        var adopter = new WineRegistryValueRecord { Hive = written.Hive, Key = written.Key, Name = written.Name, ChangedByRhi = false };
        store.Upsert(new LinuxDeploymentRecord { GameKey = "steam:1", Component = ManagedComponentKind.ReShade, RegistryValues = [adopter] });

        Assert.Equal(CompensationStatus.Done, new WineRegistryValueCompensator(values, _ => false, store).Compensate(step).Status);

        Assert.Contains("=dword:00000000", File.ReadAllText(Path.Combine(prefix, "system.reg")), StringComparison.Ordinal);
        var adopted = Assert.Single(store.All().Single().RegistryValues);
        Assert.True(adopted.ChangedByRhi);
        Assert.Equal("dword:00000000", adopted.WrittenValue);
    }

    [Fact]
    public void RegistryValueReapplyCompensator_PutsRhisValueBack_OnlyWhileTheRestoreLeftItSo()
    {
        var prefix = NewPrefix("hklm-reapply");
        var hive = Path.Combine(prefix, "system.reg");
        var values = new WineRegistryValueService(new WineRegistryEditor(_ => false));
        var record = values.Ensure(prefix, WineRegistryValueService.SystemHive, LayerKey, LayerName, "dword:00000000");
        var installed = File.ReadAllBytes(hive);
        values.Restore(prefix, record);
        var step = WineRegistryValueReapplyCompensator.Describe(prefix, record);

        Assert.Equal(CompensationStatus.Deferred, new WineRegistryValueReapplyCompensator(values, _ => true).Compensate(step).Status);
        Assert.Equal(CompensationStatus.Done, new WineRegistryValueReapplyCompensator(values, _ => false).Compensate(step).Status);
        Assert.Contains("=dword:00000000", File.ReadAllText(hive), StringComparison.Ordinal);
        Assert.Equal(installed.Length, File.ReadAllBytes(hive).Length);
    }

    [Fact]
    public async Task VulkanLayer_RepairAfterMove_RebasesTheLayerFiles_AndTheRegistrationStillHolds()
    {
        var (game, store) = CreateVulkanGame("vk-move");
        var service = NewVulkanService(store, new LunarGHandler(), Path.Combine(_root, "vk-move-cache"));
        var systemBefore = File.ReadAllBytes(Path.Combine(game.PrefixPath!, "system.reg"));
        Assert.True((await service.InstallAsync(game, LayerOptions())).Success);
        var registration = File.ReadAllText(Path.Combine(game.PrefixPath!, "system.reg"));

        // The whole library moves (game and prefix together, as a Steam library does).
        var moved = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(moved);
        Directory.Move(Path.Combine(_root, "vk-move"), Path.Combine(moved, "vk-move"));
        string Rebase(string path) => path.Replace(Path.Combine(_root, "vk-move"), Path.Combine(moved, "vk-move"), StringComparison.Ordinal);
        game.InstallPath = Rebase(game.InstallPath);
        game.BinaryDirectory = Rebase(game.BinaryDirectory!);
        game.ExecutablePath = Rebase(game.ExecutablePath!);
        game.PrefixPath = Rebase(game.PrefixPath!);
        var movedStore = new LinuxDeploymentRecordStore(Path.Combine(moved, "vk-move", "records.json"));

        var repair = new DeploymentRelocation(movedStore).Repair(game, [], out _);

        Assert.True(repair.Success, repair.Message);
        var record = movedStore.Find(game, ManagedComponentKind.ReShade)!;
        Assert.All(record.Files, file => Assert.StartsWith(Path.Combine(moved, "vk-move"), file.Path, StringComparison.Ordinal));
        // The registration names a C:\ path inside the prefix: unchanged by the move and still pointing at the layer.
        Assert.Equal(registration, File.ReadAllText(Path.Combine(game.PrefixPath!, "system.reg")));
        var removed = await NewVulkanService(movedStore, new LunarGHandler(), Path.Combine(_root, "vk-move-cache")).RemoveAsync(game);
        Assert.True(removed.Success, removed.Message);
        Assert.Equal(systemBefore, File.ReadAllBytes(Path.Combine(game.PrefixPath!, "system.reg")));
    }

    [Fact]
    public async Task VulkanLayer_CrashBeforeTheRecord_IsRolledBackOnNextStart_HivesIncluded()
    {
        var (game, store) = CreateVulkanGame("vk-crash");
        var journalDir = Path.Combine(_root, "vk-crash-journal");
        var (journal, overrides) = SimulatedDeath.NewJournal(journalDir, store);
        var service = NewVulkanService(store, new LunarGHandler(), Path.Combine(_root, "vk-crash-cache"), overrides, journal);
        var systemBefore = File.ReadAllBytes(Path.Combine(game.PrefixPath!, "system.reg"));
        var userBefore = File.ReadAllBytes(Path.Combine(game.PrefixPath!, "user.reg"));
        using var death = new SimulatedDeath(journal);
        store.SaveFaultForTesting = death.Die;

        var (died, _) = await death.RunUntilDeathAsync(() => service.InstallAsync(game, LayerOptions()));
        Assert.True(died);
        Assert.Contains("ImplicitLayers", File.ReadAllText(Path.Combine(game.PrefixPath!, "system.reg")), StringComparison.Ordinal);

        var report = await SimulatedDeath.RecoverAsync(journalDir, Path.Combine(_root, "vk-crash", "records.json"));

        Assert.Equal(1, report!.RolledBack);
        Assert.Equal(systemBefore, File.ReadAllBytes(Path.Combine(game.PrefixPath!, "system.reg")));
        Assert.Equal(userBefore, File.ReadAllBytes(Path.Combine(game.PrefixPath!, "user.reg")));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "vulkan-1.dll")));
        Assert.False(File.Exists(Path.Combine(LayerFolder(game), "ReShade64.dll")));
    }

    [Fact]
    public async Task VulkanLayer_CrashDuringRemoval_PutsTheRegistrationBackWithTheFiles()
    {
        var (game, store) = CreateVulkanGame("vk-crash-remove");
        var journalDir = Path.Combine(_root, "vk-crash-remove-journal");
        var (journal, overrides) = SimulatedDeath.NewJournal(journalDir, store);
        var service = NewVulkanService(store, new LunarGHandler(), Path.Combine(_root, "vk-crash-remove-cache"), overrides, journal);
        Assert.True((await service.InstallAsync(game, LayerOptions())).Success);
        var systemInstalled = File.ReadAllText(Path.Combine(game.PrefixPath!, "system.reg"));
        using var death = new SimulatedDeath(journal);
        var saves = 0;
        // The removal stamps the record (save 1), then takes it away at its commit point (save 2): die there.
        store.SaveFaultForTesting = () => ++saves == 2 ? death.Die() : null;

        var (died, _) = await death.RunUntilDeathAsync(() => service.RemoveAsync(game));
        Assert.True(died);
        Assert.DoesNotContain("ImplicitLayers", File.ReadAllText(Path.Combine(game.PrefixPath!, "system.reg")), StringComparison.Ordinal);

        var report = await SimulatedDeath.RecoverAsync(journalDir, Path.Combine(_root, "vk-crash-remove", "records.json"));

        Assert.Equal(1, report!.RolledBack);
        Assert.Contains("ImplicitLayers", File.ReadAllText(Path.Combine(game.PrefixPath!, "system.reg")), StringComparison.Ordinal);
        Assert.Equal(systemInstalled.Length, File.ReadAllText(Path.Combine(game.PrefixPath!, "system.reg")).Length);
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "vulkan-1.dll")));
        Assert.True(File.Exists(Path.Combine(LayerFolder(game), "ReShade64.dll")));
    }
}
