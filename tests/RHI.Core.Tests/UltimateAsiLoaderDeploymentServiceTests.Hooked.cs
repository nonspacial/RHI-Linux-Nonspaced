using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #284 (upstream 2.9.0): Hooked names for extras beside ASI Loader. Installing ASI Loader under an extra's name moves the
/// extra to the Hooked name and its record (and Wine override) follow; removing ASI Loader moves it back; the extras'
/// name lists offer the Hooked name and mark names other components use; a Hooked name needs ASI Loader under its base.
/// </summary>
public sealed partial class UltimateAsiLoaderDeploymentServiceTests
{
    [Fact]
    public async Task Install_OverAnExtrasName_MovesTheExtraToHooked_AndRemoveMovesItBack()
    {
        using var fixture = CreateGame("follow", 0x8664);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var overrides = new WineDllOverrideService();
        var extra = await SeedDlssEnablerAsync(fixture, records, overrides, "version.dll");
        var extraBytes = File.ReadAllBytes(extra);
        using var http = new HttpClient(CreateReleaseHandler(CreateUalZip(0x8664, 0x51), "9.7.4"));
        var service = new UltimateAsiLoaderDeploymentService(http, records, overrides, Path.Combine(fixture.Root, "cache"));

        var install = await service.InstallAsync(fixture.Game, "version.dll");

        Assert.True(install.Success, install.Message);
        Assert.Contains(UltimateAsiLoaderDeploymentService.FollowedExtraPrefix + "DLSS Enabler (Standalone): version.dll → versionHooked.dll", install.Message);
        var hooked = Path.Combine(fixture.Bin, "versionHooked.dll");
        Assert.Equal(extraBytes, File.ReadAllBytes(hooked));
        var moved = records.Find(fixture.Game, ManagedComponentKind.DlssEnablerStandalone)!;
        Assert.Equal(hooked, ExtraDllNames.ProxyOf(moved)!.Path);
        Assert.Equal("versionHooked.dll", moved.Metadata["DLSSEnabler.ProxyName"]);
        Assert.Null(moved.WineOverride);
        var ual = records.Find(fixture.Game, ManagedComponentKind.UltimateAsiLoader)!;
        Assert.Equal("0", ual.Metadata["UAL.HadOriginal"]); // the extra is not the game's original: never put back over the proxy
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(Registry(fixture), "version", "Game.exe"));

        var remove = await service.RemoveAsync(fixture.Game);

        Assert.True(remove.Success, remove.Message);
        Assert.Contains(UltimateAsiLoaderDeploymentService.ReleasedExtraPrefix + "DLSS Enabler (Standalone): versionHooked.dll → version.dll", remove.Message);
        Assert.Equal(extraBytes, File.ReadAllBytes(extra));
        Assert.False(File.Exists(hooked));
        var back = records.Find(fixture.Game, ManagedComponentKind.DlssEnablerStandalone)!;
        Assert.Equal(extra, ExtraDllNames.ProxyOf(back)!.Path);
        Assert.Equal("version.dll", back.Metadata["DLSSEnabler.ProxyName"]);
        Assert.Equal("version", back.WineOverride!.DllName);
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(Registry(fixture), "version", "Game.exe")); // the extra's own again
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.UltimateAsiLoader));
    }

    [Fact]
    public async Task Install_OverAnExtrasName_HookedNameTaken_IsRefusedAndChangesNothing()
    {
        using var fixture = CreateGame("follow-taken", 0x8664);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var overrides = new WineDllOverrideService();
        var extra = await SeedDlssEnablerAsync(fixture, records, overrides, "version.dll");
        File.WriteAllText(Path.Combine(fixture.Bin, "VERSIONHOOKED.dll"), "game's own");
        var ledger = File.ReadAllText(Path.Combine(fixture.Root, "deployments.json"));
        var registry = Registry(fixture);
        using var http = new HttpClient(CreateReleaseHandler(CreateUalZip(0x8664, 0x52), "9.7.4"));
        var service = new UltimateAsiLoaderDeploymentService(http, records, overrides, Path.Combine(fixture.Root, "cache"));

        var install = await service.InstallAsync(fixture.Game, "version.dll");

        Assert.False(install.Success);
        Assert.StartsWith(UltimateAsiLoaderDeploymentService.CannotChainPrefix, install.Message);
        Assert.True(File.Exists(extra));
        Assert.Equal(ledger, File.ReadAllText(Path.Combine(fixture.Root, "deployments.json")));
        Assert.Equal(registry, Registry(fixture));
    }

    [Fact]
    public async Task Install_OverAnExtrasName_CrashBeforeTheRecordSave_RecoveryPutsTheExtraBack()
    {
        using var fixture = CreateGame("follow-crash", 0x8664);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var (journal, overrides) = SimulatedDeath.NewJournal(Path.Combine(fixture.Root, "journal"), records);
        var extra = await SeedDlssEnablerAsync(fixture, records, overrides, "version.dll");
        var extraBytes = File.ReadAllBytes(extra);
        var ledger = File.ReadAllText(Path.Combine(fixture.Root, "deployments.json"));
        using var http = new HttpClient(CreateReleaseHandler(CreateUalZip(0x8664, 0x53), "9.7.4"));
        var service = new UltimateAsiLoaderDeploymentService(http, records, overrides, Path.Combine(fixture.Root, "cache"), journal);

        using (var death = new SimulatedDeath(journal))
        {
            records.SaveFaultForTesting = death.Die;
            var (died, _) = await death.RunUntilDeathAsync(() => service.InstallAsync(fixture.Game, "version.dll"));
            Assert.True(died);
            Assert.True(File.Exists(Path.Combine(fixture.Bin, "versionHooked.dll")));

            var report = await SimulatedDeath.RecoverAsync(Path.Combine(fixture.Root, "journal"), Path.Combine(fixture.Root, "deployments.json"));

            Assert.Equal(1, report!.RolledBack);
        }
        Assert.Equal(extraBytes, File.ReadAllBytes(extra));
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "versionHooked.dll")));
        Assert.Equal(ledger, File.ReadAllText(Path.Combine(fixture.Root, "deployments.json")));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(Registry(fixture), "version", "Game.exe"));
    }

    [Fact]
    public async Task ExtraNameOptions_OfferTheHookedNameAfterAsiLoaders_AndMarkNamesInUse()
    {
        using var fixture = CreateGame("options", 0x8664);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        using var http = new HttpClient(CreateReleaseHandler(CreateUalZip(0x8664, 0x54), "9.7.4"));
        var service = new UltimateAsiLoaderDeploymentService(http, records, new WineDllOverrideService(), Path.Combine(fixture.Root, "cache"));
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);
        records.Upsert(Record(fixture, ManagedComponentKind.Rtx40MfgStandalone, Path.Combine(fixture.Bin, "dxgi.dll"),
            ("UniversalMFG.ProxyName", "dxgi.dll")));

        var options = ExtraDllNames.Options(records, fixture.Game, ManagedComponentKind.DlssEnablerStandalone,
            StandaloneDlssEnablerDeploymentService.ProxyNames, current: null);

        Assert.Equal(["version.dll", "dxgi.dll", "winmm.dll", "winmmHooked.dll", "dbghelp.dll", "psapi.dll", "winhttp.dll"], options.Select(o => o.Name));
        Assert.Equal("Ultimate ASI Loader", options.Single(o => o.Name == "winmm.dll").BlockedBy);
        Assert.Equal("Universal MFG Unlocker", options.Single(o => o.Name == "dxgi.dll").BlockedBy);
        var hooked = options.Single(o => o.Name == "winmmHooked.dll");
        Assert.True(hooked.IsHooked);
        Assert.True(hooked.IsFree);

        // The extra's own name is never "in use"; the game's own file under the Hooked name is.
        Assert.True(ExtraDllNames.Options(records, fixture.Game, ManagedComponentKind.Rtx40MfgStandalone,
            Rtx40MfgStandaloneDeploymentService.SelectableProxyNames, current: "dxgi.dll").Single(o => o.Name == "dxgi.dll").IsFree);
        File.WriteAllText(Path.Combine(fixture.Bin, "winmmHooked.dll"), "game");
        Assert.Equal(ExtraDllNames.HookedTakenByGamePrefix + "winmm.dll", ExtraDllNames.Options(records, fixture.Game,
            ManagedComponentKind.DlssEnablerStandalone, StandaloneDlssEnablerDeploymentService.ProxyNames, null).Single(o => o.Name == "winmmHooked.dll").BlockedBy);
    }

    [Fact]
    public void HookedNames_AreKeptByTheExtrasNameRules_AndNeedAsiLoaderUnderTheBase()
    {
        Assert.Equal("versionHooked.dll", StandaloneDlssEnablerDeploymentService.NormalizeProxyName("VersionHooked.dll"));
        Assert.Equal("winmmHooked.dll", Dlssg2030UnlockDeploymentService.NormalizeProxyName("winmmHooked.dll"));
        Assert.Equal("alternatives/winmm.dll", Dlssg2030UnlockDeploymentService.PackagePathFor("winmmHooked.dll")); // the base's build
        Assert.Equal("versionHooked.dll", Rtx40MfgStandaloneDeploymentService.NormalizeProxyName("versionHooked.dll"));
        Assert.Equal("version.dll", StandaloneDlssEnablerDeploymentService.NormalizeProxyName("d3d8Hooked.dll")); // not an offered base
        Assert.Equal("versionHooked.dll", RtxEncoreNames.CanonicalOrHooked("versionhooked.dll"));
        Assert.Null(RtxEncoreNames.CanonicalOrHooked("binkw64Hooked.dll")); // Bink names already use Hooked for the game's file
        Assert.Null(HookedDllNames.Canonical("../versionHooked.dll", ["version.dll"]));

        using var fixture = CreateGame("hooked-refusal", 0x8664);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        Assert.StartsWith(ExtraDllNames.HookedNeedsUalPrefix, ExtraDllNames.HookedRefusal(records, fixture.Game, "versionHooked.dll"));
        Assert.Null(ExtraDllNames.HookedRefusal(records, fixture.Game, "version.dll"));
        records.Upsert(Record(fixture, ManagedComponentKind.UltimateAsiLoader, Path.Combine(fixture.Bin, "version.dll"), ("UAL.ProxyName", "version.dll")));
        Assert.Null(ExtraDllNames.HookedRefusal(records, fixture.Game, "versionHooked.dll"));
        Assert.NotNull(ExtraDllNames.HookedRefusal(records, fixture.Game, "winmmHooked.dll"));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────────────

    private static string Registry(GameFixture fixture) => File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg"));

    private static LinuxDeploymentRecord Record(GameFixture fixture, ManagedComponentKind kind, string file, params (string Key, string Value)[] metadata)
        => new()
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(fixture.Game), GameName = fixture.Game.Name, Component = kind,
            BinaryDirectory = fixture.Bin, Version = "1",
            Files = [new LinuxDeployedFile { Path = file, Sha256 = File.Exists(file) ? FileHash.Sha256Hex(file) : null }],
            Metadata = metadata.ToDictionary(m => m.Key, m => m.Value, StringComparer.OrdinalIgnoreCase)
        };

    // A DLSS Enabler as its install leaves it: the proxy under the name, its record and its native,builtin override.
    private static async Task<string> SeedDlssEnablerAsync(GameFixture fixture, LinuxDeploymentRecordStore records, WineDllOverrideService overrides, string name)
    {
        var path = Path.Combine(fixture.Bin, name);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("dlss-enabler-" + Guid.NewGuid().ToString("N")));
        var wine = await overrides.EnsureNativeBuiltinAsync(null, fixture.Prefix, name, "Game.exe");
        var record = Record(fixture, ManagedComponentKind.DlssEnablerStandalone, path, ("DLSSEnabler.ProxyName", name));
        record.WineOverride = wine;
        records.Upsert(record);
        return path;
    }
}
