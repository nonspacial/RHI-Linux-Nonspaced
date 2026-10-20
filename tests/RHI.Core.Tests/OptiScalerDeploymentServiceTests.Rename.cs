using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// w7/reshade (#196): an installed OptiScaler renamed in place — its proxy, override and recorded name move together;
// removal then follows the new name and leaves user.reg as it was; a chained ReShade (ReShade64.dll) keeps its own name.
public sealed partial class OptiScalerDeploymentServiceTests
{
    [Fact]
    public async Task Rename_MovesTheProxyOverrideAndRecordedName_AndRemovalFollowsIt()
    {
        var (game, store) = CreateGame("rename-os", GraphicsApi.DirectX12);
        var userReg = Path.Combine(game.PrefixPath!, "user.reg");
        var userRegBefore = File.ReadAllBytes(userReg);
        var service = NewService(store, CreatePackage("rename-os-package"));
        Assert.True((await service.InstallAsync(game, OptiScalerVariant.Stable, null, "dxgi.dll")).Success);
        var dxgi = Path.Combine(game.BinaryDirectory!, "dxgi.dll");
        var bytes = File.ReadAllBytes(dxgi);

        var renamed = await service.RenameProxyAsync(game, "winmm.dll", choice: null);

        Assert.True(renamed.Success, renamed.Message);
        var winmm = Path.Combine(game.BinaryDirectory!, "winmm.dll");
        Assert.False(File.Exists(dxgi));
        Assert.Equal(bytes, File.ReadAllBytes(winmm));
        var record = store.Find(game, ManagedComponentKind.OptiScaler)!;
        Assert.Equal("winmm.dll", OptiScalerDeploymentService.GetProxyName(record));
        Assert.Contains(record.Files, file => file.Path == winmm);
        Assert.DoesNotContain(record.Files, file => file.Path == dxgi);
        Assert.Equal("native,builtin", Override(game, "winmm"));
        Assert.Null(Override(game, "dxgi"));
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "OptiScaler.ini"))); // only the DLL moves

        Assert.True((await service.RemoveAsync(game)).Success);
        Assert.False(File.Exists(winmm));
        Assert.Equal(userRegBefore, File.ReadAllBytes(userReg));
    }

    [Fact]
    public async Task Managed_RenameToAutomatic_AndReset_ClearTheChoiceAndMoveOptiScalerBack()
    {
        var (game, store) = CreateGame("rename-os-managed", GraphicsApi.DirectX12);
        var (managed, names) = ManagedWithChoice(store, CreatePackage("rename-os-managed-package"), "rename-os-managed");
        names.SetOptiScaler(game, "version.dll");
        Assert.True((await managed.InstallOptiScalerAsync(game, OptiScalerVariant.Stable, nightlyBuild: null)).Success);
        Assert.True(managed.HasDllNamesToReset(game));

        var renamed = await managed.RenameOptiScalerProxyAsync(game, "winmm.dll");
        Assert.True(renamed.Success, renamed.Message);
        Assert.Equal("winmm.dll", names.GetOptiScaler(game));
        Assert.Equal("winmm.dll", managed.OptiScalerProxyNameFor(game));

        var reset = await managed.ResetDllNamesAsync(game);

        Assert.True(reset.Success, reset.Message);
        Assert.Contains("winmm.dll → dxgi.dll", reset.Message);
        Assert.Null(names.GetOptiScaler(game));
        Assert.True(BinarySignatureInspector.LooksLikeOptiScaler(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));
        Assert.Equal("dxgi.dll", OptiScalerDeploymentService.GetProxyName(store.Find(game, ManagedComponentKind.OptiScaler)!));
        Assert.False(managed.HasDllNamesToReset(game));
    }

    // #196 decision (coordinator: build upstream): OptiScaler renames freely while RHI's ReShade is chained to it as
    // ReShade64.dll. ReShade stays where OptiScaler loads it; its record keeps the name it had when the chain was made, so
    // OptiScaler's removal puts ReShade back under that name with its own override — never under OptiScaler's new name
    // (wininet.dll here, a name ReShade does not load under).
    [Fact]
    public async Task Rename_WhileRhiReShadeIsChained_KeepsTheChain_AndRemovalPutsReShadeBackUnderItsOwnName()
    {
        var (game, store) = CreateGame("rename-os-chain", GraphicsApi.DirectX12);
        SeedManagedReShade(game, store);
        var userReg = Path.Combine(game.PrefixPath!, "user.reg");
        var registryBefore = RegistryShape(userReg);
        var j = NewJournaled("rename-os-chain", store);
        var service = NewJournaledService(store, new FakePackageProvider(CreatePackage("rename-os-chain-package")), j);
        Assert.True((await service.InstallAsync(game, OptiScalerVariant.Stable, null, "dxgi.dll")).Success);
        var bin = game.BinaryDirectory!;
        var reshade64 = Path.Combine(bin, "ReShade64.dll");
        var reShadeBytes = File.ReadAllBytes(reshade64);
        Assert.Contains("wininet.dll", service.RenameOptions(game, OptiScalerDeploymentService.ProxyNames).Targets);

        var renamed = await service.RenameProxyAsync(game, "wininet.dll", choice: null);

        Assert.True(renamed.Success, renamed.Message);
        Assert.True(BinarySignatureInspector.LooksLikeOptiScaler(Path.Combine(bin, "wininet.dll")));
        Assert.False(File.Exists(Path.Combine(bin, "dxgi.dll")));
        Assert.Equal(reShadeBytes, File.ReadAllBytes(reshade64)); // the chain stays as it was
        Assert.Contains("LoadReshade=true", File.ReadAllText(Path.Combine(bin, "OptiScaler.ini")), StringComparison.OrdinalIgnoreCase);
        var reShade = store.Find(game, ManagedComponentKind.ReShade)!;
        Assert.Equal(reshade64, Assert.Single(reShade.Files).Path);
        Assert.Equal("dxgi.dll", reShade.Metadata[OptiScalerDeploymentService.ReShadeChainNameMetadataKey]);
        Assert.Equal("native,builtin", Override(game, "wininet"));
        Assert.Null(Override(game, "dxgi"));

        var removed = await service.RemoveAsync(game);

        Assert.True(removed.Success, removed.Message);
        var dxgi = Path.Combine(bin, "dxgi.dll");
        Assert.Equal(reShadeBytes, File.ReadAllBytes(dxgi));
        Assert.False(File.Exists(reshade64));
        Assert.False(File.Exists(Path.Combine(bin, "wininet.dll")));
        reShade = store.Find(game, ManagedComponentKind.ReShade)!;
        Assert.Equal(dxgi, Assert.Single(reShade.Files).Path);
        Assert.Equal("dxgi", reShade.WineOverride!.DllName);
        Assert.False(reShade.Metadata.ContainsKey(OptiScalerDeploymentService.ReShadeChainNameMetadataKey));
        Assert.Equal("native,builtin", Override(game, "dxgi"));
        Assert.Null(Override(game, "wininet"));
        Assert.Equal(registryBefore, RegistryShape(userReg)); // the overrides are as before OptiScaler
        Assert.Empty(Directory.GetFiles(j.Dir));
    }

    [Fact]
    public async Task Rename_WhileChained_CrashAtTheRecordSave_RecoveryPutsOptiScalerBackAndReShadesRecordAsItWas()
    {
        // One journaled operation: OptiScaler's record and ReShade's (its chain name) are saved in one ledger write.
        var (game, store) = CreateGame("rename-os-chain-crash", GraphicsApi.DirectX12);
        SeedManagedReShade(game, store);
        var j = NewJournaled("rename-os-chain-crash", store);
        var service = NewJournaledService(store, new FakePackageProvider(CreatePackage("rename-os-chain-crash-package")), j);
        Assert.True((await service.InstallAsync(game, OptiScalerVariant.Stable, null, "dxgi.dll")).Success);
        var dxgi = Path.Combine(game.BinaryDirectory!, "dxgi.dll");
        var optiBytes = File.ReadAllBytes(dxgi);
        var ledgerBefore = File.ReadAllText(j.Ledger);

        using (var death = new SimulatedDeath(j.Journal))
        {
            store.SaveFaultForTesting = death.Die;
            var (died, _) = await death.RunUntilDeathAsync(() => service.RenameProxyAsync(game, "wininet.dll", choice: null));
            Assert.True(died);

            await SimulatedDeath.RecoverAsync(j.Dir, j.Ledger);

            Assert.Equal(optiBytes, File.ReadAllBytes(dxgi));
            Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "wininet.dll")));
            Assert.Equal(ledgerBefore, File.ReadAllText(j.Ledger));
            Assert.Equal("native,builtin", Override(game, "dxgi"));
            Assert.Null(Override(game, "wininet"));
        }
    }

    [Fact]
    public async Task Remove_AfterAChainedRename_ReShadesNameTaken_IsRefusedBeforeAnyChange()
    {
        var (game, store) = CreateGame("rename-os-chain-taken", GraphicsApi.DirectX12);
        SeedManagedReShade(game, store);
        var j = NewJournaled("rename-os-chain-taken", store);
        var service = NewJournaledService(store, new FakePackageProvider(CreatePackage("rename-os-chain-taken-package")), j);
        Assert.True((await service.InstallAsync(game, OptiScalerVariant.Stable, null, "dxgi.dll")).Success);
        Assert.True((await service.RenameProxyAsync(game, "winmm.dll", choice: null)).Success);
        File.WriteAllBytes(Path.Combine(game.BinaryDirectory!, "DXGI.dll"), [0x4D, 0x5A, 1, 2]); // someone took ReShade's name
        var watched = new[] { game.BinaryDirectory!, game.PrefixPath!, Path.GetDirectoryName(j.Ledger)! };
        var before = CancelAtWritePhase.Snapshot(watched);

        var removed = await service.RemoveAsync(game);

        Assert.False(removed.Success);
        Assert.Equal(OptiScalerDeploymentService.ChainNameTakenPrefix + "dxgi.dll", removed.Message);
        CancelAtWritePhase.AssertUnchanged(before, watched);
    }

    [Fact]
    public async Task Remove_AfterAChainedRename_LedgerBlocked_RollsBackTheChainAndBothOverrides()
    {
        var (game, store) = CreateGame("rename-os-chain-blocked", GraphicsApi.DirectX12);
        SeedManagedReShade(game, store);
        var j = NewJournaled("rename-os-chain-blocked", store);
        var service = NewJournaledService(store, new FakePackageProvider(CreatePackage("rename-os-chain-blocked-package")), j);
        Assert.True((await service.InstallAsync(game, OptiScalerVariant.Stable, null, "dxgi.dll")).Success);
        Assert.True((await service.RenameProxyAsync(game, "wininet.dll", choice: null)).Success);
        var bin = game.BinaryDirectory!;
        var files = Directory.GetFiles(bin, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        var registryBefore = RegistryShape(Path.Combine(game.PrefixPath!, "user.reg"));
        var saves = 0;
        store.SaveFaultForTesting = () => ++saves == 2 ? new IOException("disk full (test)") : null; // the commit write

        var removed = await service.RemoveAsync(game);

        Assert.False(removed.Success);
        Assert.StartsWith(OptiScalerDeploymentService.RemovalFailedPrefix, removed.Message);
        Assert.Equal(files.Keys.Order(), Directory.GetFiles(bin, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, bytes) in files) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(registryBefore, RegistryShape(Path.Combine(game.PrefixPath!, "user.reg")));
        Assert.Equal("dxgi.dll", store.Find(game, ManagedComponentKind.ReShade)!.Metadata[OptiScalerDeploymentService.ReShadeChainNameMetadataKey]);
    }

    // user.reg without Wine's section timestamps (a section RHI removed and created again carries a new one).
    private static string RegistryShape(string userReg)
        => string.Join("\n", File.ReadAllText(userReg).Split('\n').Where(line => !line.StartsWith("#time=", StringComparison.Ordinal))
            .Select(line => System.Text.RegularExpressions.Regex.Replace(line, @"^(\[.*\]) \d+$", "$1")));

    [Fact]
    public async Task RenameOptions_LeaveOutNamesAnotherFileUses()
    {
        var (game, store) = CreateGame("rename-os-options", GraphicsApi.DirectX12);
        var service = NewService(store, CreatePackage("rename-os-options-package"));
        Assert.True((await service.InstallAsync(game, OptiScalerVariant.Stable, null, "dxgi.dll")).Success);
        File.WriteAllBytes(Path.Combine(game.BinaryDirectory!, "version.dll"), [0x4D, 0x5A]);

        var (targets, refusal) = service.RenameOptions(game, OptiScalerDeploymentService.ProxyNames);

        Assert.Null(refusal);
        Assert.Contains("winmm.dll", targets);
        Assert.DoesNotContain("version.dll", targets);
        Assert.DoesNotContain("dxgi.dll", targets);
    }
}
