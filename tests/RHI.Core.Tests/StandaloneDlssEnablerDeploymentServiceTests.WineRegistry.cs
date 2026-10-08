using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

// The proxy services (DLSS Enabler, RTX 20 FG Unlock, Universal MFG share this shape): refused while Wine runs (#24),
// override journaled with the record as commit point (#23), removal keeps a later value and a failed removal puts
// RHI's value back only where it restored it (#30).
public sealed partial class StandaloneDlssEnablerDeploymentServiceTests
{
    private static string UserReg(GameFixture fixture) => File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg"));

    [Fact]
    public async Task Install_WhileWineRunsInThePrefix_IsRefusedBeforeTheDownload()
    {
        using var fixture = CreateGame("wine-running", 0x8664, GraphicsApi.DirectX12);
        using var handler = DlssEnablerHandler.WithVersions(("4.10.0", CreateZip(("version.dll", CreatePe(0x8664, 256 * 1024, 0x52)))));
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var before = UserReg(fixture);
        var service = new StandaloneDlssEnablerDeploymentService(http, records, new WineDllOverrideService(new WineRegistryEditor(_ => true)),
            new StandaloneDlssEnablerPreferenceStore(Path.Combine(fixture.Root, "prefs.json")), Path.Combine(fixture.Root, "cache"));

        var install = await service.InstallAsync(fixture.Game, "winmm.dll");

        Assert.False(install.Success);
        Assert.Equal(WineRegistryEditor.PrefixInUseRefusalPrefix + fixture.Prefix, install.Message);
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "winmm.dll")));
        Assert.Equal(before, UserReg(fixture));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "cache")));
    }

    [Fact]
    public async Task JournaledInstall_ThenRemoveAfterTheUserChangedTheOverride()
    {
        using var fixture = CreateGame("journaled", 0x8664, GraphicsApi.DirectX12);
        using var handler = DlssEnablerHandler.WithVersions(("4.10.0", CreateZip(("version.dll", CreatePe(0x8664, 256 * 1024, 0x53)))));
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var overrides = new WineDllOverrideService();
        var journalDir = Path.Combine(fixture.Root, "journal");
        var journal = LinuxDeploymentRecovery.CreateJournal(overrides, journalDir);
        string? operationId = null;
        journal.OperationStartedForTesting = operation => operationId = operation.Id;
        var service = new StandaloneDlssEnablerDeploymentService(http, records, overrides,
            new StandaloneDlssEnablerPreferenceStore(Path.Combine(fixture.Root, "prefs.json")), Path.Combine(fixture.Root, "cache"), journal);

        var install = await service.InstallAsync(fixture.Game, "winmm.dll");

        Assert.True(install.Success, install.Message);
        Assert.Equal(operationId, records.Find(fixture.Game, ManagedComponentKind.DlssEnablerStandalone)!.Metadata[LinuxDeploymentRecovery.OperationIdMetadataKey]);
        Assert.Empty(Directory.GetFiles(journalDir));

        var reg = Path.Combine(fixture.Prefix, "user.reg");
        File.WriteAllText(reg, WineDllOverrideService.SetOverride(File.ReadAllText(reg), "winmm", "builtin", "Game.exe"));
        var remove = await service.RemoveAsync(fixture.Game);

        Assert.True(remove.Success, remove.Message);
        Assert.Equal("builtin", WineDllOverrideService.TryReadOverride(UserReg(fixture), "winmm", "Game.exe"));
        Assert.EndsWith(WineDllOverrideService.KeptUserValueNotePrefix + "winmm = 'builtin'.", remove.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoveFailingAfterTheRestore_PutsRhisValueBack()
    {
        using var fixture = CreateGame("remove-fails", 0x8664, GraphicsApi.DirectX12);
        using var handler = DlssEnablerHandler.WithVersions(("4.10.0", CreateZip(("version.dll", CreatePe(0x8664, 256 * 1024, 0x54)))));
        using var http = new HttpClient(handler);
        var ledger = Path.Combine(fixture.Root, "deployments.json");
        var records = new LinuxDeploymentRecordStore(ledger);
        var service = CreateService(fixture, http, records);
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);

        // The record removal fails once (the rollback's own save then goes through).
        records.SaveFaultForTesting = () =>
        {
            records.SaveFaultForTesting = null;
            return new IOException("Simulated ledger failure");
        };
        var remove = await service.RemoveAsync(fixture.Game);

        Assert.False(remove.Success);
        Assert.True(File.Exists(Path.Combine(fixture.Bin, "winmm.dll")));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(UserReg(fixture), "winmm", "Game.exe"));
        Assert.NotNull(records.Find(fixture.Game, ManagedComponentKind.DlssEnablerStandalone));
    }
}
