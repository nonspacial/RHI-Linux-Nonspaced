using RHI.Core.Deployment;
using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

public sealed partial class Dlssg2030UnlockDeploymentServiceTests : IDisposable
{
    private const string Tag = "0.4.2";
    private const string NewerTag = "0.4.3";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-dlssg2030-tests-" + Guid.NewGuid().ToString("N"));

    public Dlssg2030UnlockDeploymentServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void UpstreamContract_ProxyNamesFollowTheModsOrder_EachWithItsOwnBuild()
    {
        // #261: the order of the mod's alternatives/README.md; winhttp.dll is no longer offered.
        Assert.Equal(new[] { "version.dll", "winmm.dll", "dbghelp.dll", "dinput8.dll", "dxgi.dll", "d3d12.dll" }, Dlssg2030UnlockDeploymentService.ProxyNames);
        Assert.Equal("version.dll", Dlssg2030UnlockDeploymentService.PackagePathFor("version.dll"));
        Assert.Equal("alternatives/winmm.dll", Dlssg2030UnlockDeploymentService.PackagePathFor("winmm.dll"));
        Assert.Equal("alternatives/dbghelp.dll", Dlssg2030UnlockDeploymentService.PackagePathFor("DBGHELP.DLL"));
        Assert.Equal("alternatives/d3d12.dll", Dlssg2030UnlockDeploymentService.PackagePathFor("d3d12.dll"));
        Assert.Null(Dlssg2030UnlockDeploymentService.PackagePathFor("winhttp.dll"));
        Assert.Equal("version.dll", Dlssg2030UnlockDeploymentService.NormalizeProxyName("winhttp.dll"));
        Assert.Contains("sdli1995/dlssg_for_sm86", Dlssg2030UnlockDeploymentService.RepositoryUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Package_WhoseBytesAreNotGitHubsDigest_IsRefused_AndNothingIsCachedOrInstalled()
    {
        using var fixture = CreateGame("digest", 0x8664, GraphicsApi.DirectX12);
        using var handler = new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x41), UpstreamIni()) { WrongDigest = true };
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);

        var result = await service.InstallAsync(fixture.Game, Dlssg2030UnlockDeploymentService.GpuGenRtx20, "version.dll");

        Assert.False(result.Success);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "cache", Tag, handler.AssetName)));
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "version.dll")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Bin, Dlssg2030UnlockDeploymentService.ModFolderName)));
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.Dlssg2030Unlock));
    }

    [Fact]
    public void Eligibility_RequiresX64Dx12InitializedPrefix()
    {
        using var fixture = CreateGame("eligibility", 0x8664, GraphicsApi.DirectX12);
        Assert.True(Dlssg2030UnlockDeploymentService.IsGameEligible(fixture.Game));

        fixture.Game.GraphicsApi = GraphicsApi.DirectX11;
        Assert.False(Dlssg2030UnlockDeploymentService.IsGameEligible(fixture.Game));
        fixture.Game.GraphicsApi = GraphicsApi.DirectX12;
        fixture.Game.ForcedArchitecture = PeArchitecture.X86;
        Assert.False(Dlssg2030UnlockDeploymentService.IsGameEligible(fixture.Game));
    }

    [Fact]
    public void PreferenceStore_RoundTripsPerGameChoice()
    {
        using var fixture = CreateGame("prefs", 0x8664, GraphicsApi.DirectX12);
        var store = new Dlssg2030PreferenceStore(Path.Combine(fixture.Root, "prefs.json"));
        store.Set(fixture.Game, new Dlssg2030Preferences { GpuGeneration = Dlssg2030UnlockDeploymentService.GpuGenRtx20, ProxyName = "winmm.dll" });
        var read = store.Get(fixture.Game);
        Assert.Equal(Dlssg2030UnlockDeploymentService.GpuGenRtx20, read.GpuGeneration);
        Assert.Equal("winmm.dll", read.ProxyName);
    }

    [Fact]
    public void NewInstallGpuChoices_ExposeOnlyRtx20LegacyRoute()
    {
        Assert.Equal(new[] { Dlssg2030UnlockDeploymentService.GpuGenRtx20 }, Dlssg2030UnlockDeploymentService.GpuGenerations);
    }

    [Fact]
    public void LegacySavedRtx30Preference_MigratesToRtx20ForNewInstalls()
    {
        using var fixture = CreateGame("legacy-pref-migrate", 0x8664, GraphicsApi.DirectX12);
        var path = Path.Combine(fixture.Root, "prefs.json");
        var key = LinuxDeploymentRecordStore.GameKey(fixture.Game);
        File.WriteAllText(path, $"{{\"{key}\":{{\"GpuGeneration\":\"RTX 30 Series\",\"ProxyName\":\"winmm.dll\"}}}}");
        var store = new Dlssg2030PreferenceStore(path);

        var read = store.Get(fixture.Game);

        Assert.Equal(Dlssg2030UnlockDeploymentService.GpuGenRtx20, read.GpuGeneration);
        Assert.Equal("winmm.dll", read.ProxyName);
    }

    [Fact]
    public void SetPreferences_Rtx30RedirectsToUniversalMfg()
    {
        using var fixture = CreateGame("redirect-prefs", 0x8664, GraphicsApi.DirectX12);
        using var http = new HttpClient(new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x28), UpstreamIni()));
        var service = CreateService(fixture, http, new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json")));

        var result = service.SetPreferences(fixture.Game, Dlssg2030UnlockDeploymentService.GpuGenRtx30, "version.dll");

        Assert.False(result.Success);
        Assert.Contains("Universal MFG", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Install_Rtx30IsBlockedBeforeNetworkAndRedirectsToUniversalMfg()
    {
        using var fixture = CreateGame("redirect-install", 0x8664, GraphicsApi.DirectX12);
        using var handler = new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x29), UpstreamIni());
        using var http = new HttpClient(handler);
        var service = CreateService(fixture, http, new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json")));

        var result = await service.InstallAsync(fixture.Game, Dlssg2030UnlockDeploymentService.GpuGenRtx30, "version.dll");

        Assert.False(result.Success);
        Assert.Contains("Universal MFG", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "version.dll")));
    }

    [Fact]
    public async Task LegacyRtx30ManagedRecord_RemainsSafelyRemovable()
    {
        using var fixture = CreateGame("legacy-rtx30", 0x8664, GraphicsApi.DirectX12);
        using var handler = new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x31), UpstreamIni());
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);

        var install = await service.InstallAsync(fixture.Game, Dlssg2030UnlockDeploymentService.GpuGenRtx20, "version.dll");
        Assert.True(install.Success, install.Message);
        var legacy = records.Find(fixture.Game, ManagedComponentKind.Dlssg2030Unlock)!;
        legacy.Metadata["FG2030.GpuGeneration"] = Dlssg2030UnlockDeploymentService.GpuGenRtx30;
        legacy.Metadata["FG2030.Router"] = "SM86";
        records.Upsert(legacy);

        var remove = await service.RemoveAsync(fixture.Game);

        Assert.True(remove.Success, remove.Message);
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "version.dll")));
        Assert.False(File.Exists(Path.Combine(fixture.Bin, Dlssg2030UnlockDeploymentService.IniFileName)));
        Assert.Null(WineDllOverrideService.TryReadOverride(
            File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "version", "Game.exe"));
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.Dlssg2030Unlock));
    }

    [Fact]
    public async Task Install_UnreadablePreferenceFile_RefusesBeforeDownloadOrOverride()
    {
        // Refs #57: the preference save comes last; an unreadable file must stop the install up front.
        using var fixture = CreateGame("unreadable-prefs", 0x8664, GraphicsApi.DirectX12);
        File.WriteAllText(Path.Combine(fixture.Root, "prefs.json"), "{ not json");
        using var handler = new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x32), UpstreamIni());
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);

        var result = await service.InstallAsync(fixture.Game, Dlssg2030UnlockDeploymentService.GpuGenRtx20, "version.dll");

        Assert.False(result.Success);
        Assert.Contains("prefs.json", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.RequestCount);
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.Dlssg2030Unlock));
        Assert.Null(WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "version", "Game.exe"));
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "version.dll")));
        Assert.Equal("{ not json", File.ReadAllText(Path.Combine(fixture.Root, "prefs.json")));
    }

    [Fact]
    public async Task Install_DeploysTheReleasePackage_TheNamesOwnBuild_AndRecordsEveryFile()
    {
        using var fixture = CreateGame("rtx20", 0x8664, GraphicsApi.DirectX12);
        var dll = CreatePe(0x8664, 320 * 1024, 0x32);
        var ini = UpstreamIni();
        var handler = new DlssgHandler(dll, ini);
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);
        var result = await service.InstallAsync(fixture.Game, Dlssg2030UnlockDeploymentService.GpuGenRtx20, "winmm.dll");
        Assert.True(result.Success, result.Message);

        // The winmm build from alternatives/, never version.dll renamed; the INI as the release ships it (no Router written).
        Assert.Equal(handler.Build("winmm.dll"), File.ReadAllBytes(Path.Combine(fixture.Bin, "winmm.dll")));
        Assert.NotEqual(handler.Build("version.dll"), handler.Build("winmm.dll"));
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "version.dll")));
        Assert.Equal(ini, File.ReadAllText(Path.Combine(fixture.Bin, Dlssg2030UnlockDeploymentService.IniFileName)));
        foreach (var (name, bytes) in handler.ModFiles)
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(fixture.Bin, "dlssg_sm86", name)));
        // Docs, readmes, the manifest and the other proxy builds stay out of the game folder.
        Assert.Equal(["Game.exe", "dlssg_sm86.ini", "winmm.dll"], Directory.GetFiles(fixture.Bin).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(["dlssg_sm86"], Directory.GetDirectories(fixture.Bin).Select(Path.GetFileName));

        var record = records.Find(fixture.Game, ManagedComponentKind.Dlssg2030Unlock)!;
        Assert.Equal(Tag, record.Version);
        Assert.Equal(Tag, record.Metadata["FG2030.ReleaseTag"]);
        Assert.Equal(Dlssg2030UnlockDeploymentService.GpuGenRtx20, record.Metadata["FG2030.GpuGeneration"]);
        Assert.Equal("winmm.dll", record.Metadata["FG2030.ProxyName"]);
        Assert.False(record.Metadata.ContainsKey("FG2030.Router"));
        Assert.Equal(2 + handler.ModFiles.Count, record.Files.Count);
        Assert.All(record.Files, file => Assert.Equal(DownloadRecordAssert.Sha256Of(File.ReadAllBytes(file.Path)), file.Sha256, ignoreCase: true));
        Assert.Equal([Dlssg2030UnlockDeploymentService.IniFileName], record.Files.Where(file => file.AllowUserChanges).Select(file => Path.GetFileName(file.Path)));
        // The package matched the digest GitHub publishes for it.
        DownloadRecordAssert.Recorded(record, DownloadRecordAssert.Sha256Of(handler.Package), VerifiedDownload.PublishedDigest);
        Assert.StartsWith(Dlssg2030UnlockDeploymentService.InstalledPrefix + Tag + " (winmm.dll).", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_RecordsTheNewReleasesDownload()
    {
        // Review w2-trust minor 6: after an update the record names the bytes it now deploys.
        using var fixture = CreateGame("update-download", 0x8664, GraphicsApi.DirectX12);
        var handler = new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x33), UpstreamIni());
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);
        Assert.True((await service.InstallAsync(fixture.Game, Dlssg2030UnlockDeploymentService.GpuGenRtx20, "winmm.dll")).Success);

        handler.Tag = NewerTag;
        handler.Dll = CreatePe(0x8664, 320 * 1024, 0x34);
        var update = await service.UpdateAsync(fixture.Game);

        Assert.True(update.Success, update.Message);
        var record = records.Find(fixture.Game, ManagedComponentKind.Dlssg2030Unlock)!;
        Assert.Equal(NewerTag, record.Metadata["FG2030.ReleaseTag"]);
        Assert.Equal(handler.Build("winmm.dll"), File.ReadAllBytes(Path.Combine(fixture.Bin, "winmm.dll")));
        DownloadRecordAssert.Recorded(record, DownloadRecordAssert.Sha256Of(handler.Package), VerifiedDownload.PublishedDigest);
    }

    [Fact]
    public async Task Install_BlocksExternalProxyBeforeNetwork()
    {
        using var fixture = CreateGame("external", 0x8664, GraphicsApi.DirectX12);
        File.WriteAllText(Path.Combine(fixture.Bin, "dxgi.dll"), "foreign");
        using var handler = new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x33), UpstreamIni());
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);
        var result = await service.InstallAsync(fixture.Game, proxyName: "dxgi.dll");
        Assert.False(result.Success);
        Assert.Contains("não sobrescreve", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);

        File.Delete(Path.Combine(fixture.Bin, "dxgi.dll"));
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(fixture.Game), GameName = fixture.Game.Name,
            Component = ManagedComponentKind.ShortFuse, BinaryDirectory = fixture.Bin
        });
        var conflict = await service.InstallAsync(fixture.Game, proxyName: "dxgi.dll");
        Assert.False(conflict.Success);
        Assert.Contains("ShortFuse", conflict.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Install_BlocksAnotherRhiManagedProxyBeforeNetwork()
    {
        using var fixture = CreateGame("owned", 0x8664, GraphicsApi.DirectX12);
        var target = Path.Combine(fixture.Bin, "dinput8.dll");
        File.WriteAllText(target, "owned");
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(fixture.Game), GameName = fixture.Game.Name,
            Component = ManagedComponentKind.ReShade, BinaryDirectory = fixture.Bin,
            Files = [new LinuxDeployedFile { Path = target, Sha256 = "deadbeef" }]
        });
        using var handler = new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x34), UpstreamIni());
        using var http = new HttpClient(handler);
        var service = CreateService(fixture, http, records);
        var result = await service.InstallAsync(fixture.Game, proxyName: "dinput8.dll");
        Assert.False(result.Success);
        Assert.Contains("outro componente", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Install_RejectsX86PayloadBeforeGameMutation()
    {
        using var fixture = CreateGame("badpayload", 0x8664, GraphicsApi.DirectX12);
        using var http = new HttpClient(new DlssgHandler(CreatePe(0x014c, 320 * 1024, 0x35), UpstreamIni()));
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);
        var result = await service.InstallAsync(fixture.Game);
        Assert.False(result.Success);
        Assert.Contains("x64 PE", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "version.dll")));
        Assert.Null(WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "version", "Game.exe"));
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.Dlssg2030Unlock));
    }

    [Fact]
    public async Task Remove_ModifiedManagedProxyBlocksBeforeOverrideTeardown()
    {
        using var fixture = CreateGame("modified", 0x8664, GraphicsApi.DirectX12);
        using var http = new HttpClient(new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x36), UpstreamIni()));
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);
        Assert.True((await service.InstallAsync(fixture.Game)).Success);
        File.AppendAllText(Path.Combine(fixture.Bin, "version.dll"), "user-change");
        var result = await service.RemoveAsync(fixture.Game);
        Assert.False(result.Success);
        Assert.Contains("modificada fora", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "version", "Game.exe"));
        Assert.NotNull(records.Find(fixture.Game, ManagedComponentKind.Dlssg2030Unlock));
    }

    [Fact]
    public async Task Remove_UserModifiedIniPreservesConfigAndCleansProxyOverride()
    {
        using var fixture = CreateGame("ini-edit", 0x8664, GraphicsApi.DirectX12);
        using var http = new HttpClient(new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x37), UpstreamIni()));
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);
        Assert.True((await service.InstallAsync(fixture.Game)).Success);
        var ini = Path.Combine(fixture.Bin, Dlssg2030UnlockDeploymentService.IniFileName);
        File.AppendAllText(ini, "\n; user tuned\nLogging.Level=2\n");
        var result = await service.RemoveAsync(fixture.Game);
        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(ini));
        Assert.Contains("user tuned", File.ReadAllText(ini), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "version.dll")));
        Assert.Null(WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "version", "Game.exe"));
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.Dlssg2030Unlock));
        var probe = ComponentProbe.Probe(fixture.Game, Directory.GetFiles(fixture.Bin));
        Assert.DoesNotContain(probe, component => component.Kind == ManagedComponentKind.Dlssg2030Unlock && component.Availability == ComponentAvailability.Installed);
    }

    // w2 wine review, minor 2 (N12): removal refused while Wine runs in the prefix, before any teardown.
    [Fact]
    public async Task Remove_WhileWineRunsInThePrefix_IsRefusedBeforeTeardown()
    {
        using var fixture = CreateGame("wine-running-remove", 0x8664, GraphicsApi.DirectX12);
        using var http = new HttpClient(new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x38), UpstreamIni()));
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        Assert.True((await CreateService(fixture, http, records).InstallAsync(fixture.Game)).Success);
        var proxy = Path.Combine(fixture.Bin, "version.dll");
        var proxyBytes = File.ReadAllBytes(proxy);
        var running = new Dlssg2030UnlockDeploymentService(http, records, new WineDllOverrideService(new WineRegistryEditor(_ => true)),
            new Dlssg2030PreferenceStore(Path.Combine(fixture.Root, "prefs.json")), Path.Combine(fixture.Root, "cache"));

        var remove = await running.RemoveAsync(fixture.Game);

        Assert.False(remove.Success);
        Assert.Equal(WineRegistryEditor.PrefixInUseRefusalPrefix + fixture.Prefix, remove.Message);
        Assert.Equal(proxyBytes, File.ReadAllBytes(proxy));
        Assert.True(File.Exists(Path.Combine(fixture.Bin, Dlssg2030UnlockDeploymentService.IniFileName)));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "version", "Game.exe"));
        Assert.NotNull(records.Find(fixture.Game, ManagedComponentKind.Dlssg2030Unlock));
    }

    // w2 wine review, minor 2 (N3): a removal failing after it restored the override puts RHI's value back (the record stays).
    [Fact]
    public async Task Remove_FailingAfterTheOverrideWasRestored_PutsRhisOverrideBack()
    {
        using var fixture = CreateGame("remove-fails", 0x8664, GraphicsApi.DirectX12);
        using var http = new HttpClient(new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x39), UpstreamIni()));
        var ledger = Path.Combine(fixture.Root, "deployments.json");
        var records = new LinuxDeploymentRecordStore(ledger);
        var service = CreateService(fixture, http, records);
        Assert.True((await service.InstallAsync(fixture.Game)).Success);
        var proxy = Path.Combine(fixture.Bin, "version.dll");
        var proxyBytes = File.ReadAllBytes(proxy);

        ComponentOperationResult remove;
        using (LedgerWriteBlocker.Block(records, ledger))
            remove = await service.RemoveAsync(fixture.Game);

        Assert.False(remove.Success);
        Assert.Equal(proxyBytes, File.ReadAllBytes(proxy));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "version", "Game.exe"));
        Assert.NotNull(records.Find(fixture.Game, ManagedComponentKind.Dlssg2030Unlock));
    }

    // w2/vmsplit review finding 1: Cancel is honoured only before the first write; afterwards the operation runs to the end.
    [Fact]
    public async Task Install_CancelAfterTheFirstWrite_RunsToCompletion()
    {
        using var fixture = CreateGame("cancel-install", 0x8664, GraphicsApi.DirectX12);
        using var http = new HttpClient(new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x34), UpstreamIni()));
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);

        var run = await CancelAtWritePhase.RunAsync(
            ct => service.InstallAsync(fixture.Game, Dlssg2030UnlockDeploymentService.GpuGenRtx20, "winmm.dll", ct), [fixture.Bin, fixture.Prefix]);

        Assert.True(run.Result.Success, run.Result.Message);
        Assert.NotNull(records.Find(fixture.Game, ManagedComponentKind.Dlssg2030Unlock));
        Assert.True(File.Exists(Path.Combine(fixture.Bin, Dlssg2030UnlockDeploymentService.IniFileName)));
    }

    // w2/vmsplit re-review, minor 2: the removal closes the Cancel gate before its first change, so a Cancel pressed
    // while it writes is refused and its real outcome is reported (it used to be accepted and read "Operation cancelled.").
    [Fact]
    public async Task Remove_EntersItsWritePhaseBeforeTheFirstChange_AndRunsToTheEnd()
    {
        using var fixture = CreateGame("cancel-remove", 0x8664, GraphicsApi.DirectX12);
        using var http = new HttpClient(new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x35), UpstreamIni()));
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);
        Assert.True((await service.InstallAsync(fixture.Game, Dlssg2030UnlockDeploymentService.GpuGenRtx20, "winmm.dll")).Success);

        var run = await CancelAtWritePhase.RunAsync(ct => service.RemoveAsync(fixture.Game, ct), [fixture.Bin, fixture.Prefix]);

        Assert.True(run.Result.Success, run.Result.Message);
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.Dlssg2030Unlock));
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "winmm.dll")));
    }

    private Dlssg2030UnlockDeploymentService CreateService(GameFixture fixture, HttpClient http, LinuxDeploymentRecordStore records)
        => new(http, records, new WineDllOverrideService(), new Dlssg2030PreferenceStore(Path.Combine(fixture.Root, "prefs.json")), Path.Combine(fixture.Root, "cache"));

    private GameFixture CreateGame(string name, ushort machine, GraphicsApi api)
    {
        var root = Path.Combine(_root, name + "-" + Guid.NewGuid().ToString("N"));
        var bin = Path.Combine(root, "game");
        var prefix = Path.Combine(root, "pfx");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(prefix);
        var exe = Path.Combine(bin, "Game.exe");
        File.WriteAllBytes(exe, CreatePe(machine, 64 * 1024, 0x11));
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\Other] 1\n\"x\"=\"y\"\n");
        return new GameFixture(root, bin, prefix, new DetectedGame
        {
            Name = "DLSSG Test " + name, InstallPath = root, Source = "Test", Runner = "Wine",
            PrefixPath = prefix, ExecutablePath = exe, BinaryDirectory = bin, GraphicsApi = api
        });
    }

    /// <summary>A 0.4.x template: no [Compatibility] Router any more (the mod picks the RTX 20 / 30 kernels itself).</summary>
    private static string UpstreamIni() => """
        ; DLSSG SM86 - frame generation for Ampere (RTX 30) and newer.
        [General]
        Enabled=1

        [FrameGeneration]
        Engine=DLSS
        MaxGeneratedFrames=3

        [Logging]
        Level=1
        Directory=dlssg_sm86\logs
        """;

    private static byte[] CreatePe(ushort machine, int size, byte marker)
    {
        var bytes = Enumerable.Repeat(marker, size).ToArray();
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
        bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E'; bytes[0x82] = 0; bytes[0x83] = 0;
        BitConverter.GetBytes(machine).CopyTo(bytes, 0x84);
        return bytes;
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed record GameFixture(string Root, string Bin, string Prefix, DetectedGame Game) : IDisposable
    { public void Dispose() { } }

    /// <summary>
    /// GitHub for sdli1995/dlssg_for_sm86: the latest release (its tag and the dlssg-release-&lt;tag&gt;-x64.zip asset with its
    /// digest) and the package, built like the real 0.4.2 one (version.dll, alternatives/, dlssg_sm86.ini, dlssg_sm86/, docs).
    /// </summary>
    internal sealed class DlssgHandler(byte[] dll, string ini) : HttpMessageHandler
    {
        public string Tag { get; set; } = "0.4.2";
        public byte[] Dll { get; set; } = dll;
        public string Ini { get; set; } = ini;

        /// <summary>The package's files under dlssg_sm86/ (path inside it → bytes); a test may change them for a newer release.</summary>
        public Dictionary<string, byte[]> ModFiles { get; set; } = new(StringComparer.Ordinal)
        {
            ["backend/sm86_backend.dll"] = Bytes(0x51, 4096),
            ["kernels/fg_kernels.dll"] = Bytes(0x52, 4096),
            ["runtime/nvngx_dlssg.dll"] = Bytes(0x53, 4096),
            ["sr/sr_kernels.dll"] = Bytes(0x54, 4096),
        };

        public int RequestCount { get; private set; }

        /// <summary>When set, GitHub publishes a digest that does not belong to the served package.</summary>
        public bool WrongDigest { get; init; }

        public string AssetName => $"dlssg-release-{Tag}-x64.zip";
        public string AssetUrl => $"https://github.com/sdli1995/dlssg_for_sm86/releases/download/{Tag}/{AssetName}";

        /// <summary>The build the package ships for a proxy name: the root DLL for version.dll, a variant of it for the others.</summary>
        public byte[] Build(string proxyName)
        {
            var bytes = (byte[])Dll.Clone();
            var index = Array.IndexOf(Dlssg2030UnlockDeploymentService.ProxyNames.ToArray(), proxyName);
            bytes[^1] = (byte)(bytes[^1] ^ (index + 1));
            return bytes;
        }

        public byte[] Package
        {
            get
            {
                using var stream = new MemoryStream();
                using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
                {
                    void Add(string name, byte[] content)
                    {
                        var entry = zip.CreateEntry(name);
                        entry.LastWriteTime = new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);
                        using var s = entry.Open();
                        s.Write(content);
                    }
                    Add("version.dll", Build("version.dll"));
                    foreach (var name in Dlssg2030UnlockDeploymentService.ProxyNames.Skip(1)) Add("alternatives/" + name, Build(name));
                    Add("alternatives/README.md", Encoding.UTF8.GetBytes("# alternatives"));
                    Add(Dlssg2030UnlockDeploymentService.IniFileName, Encoding.UTF8.GetBytes(Ini));
                    Add("README.md", Encoding.UTF8.GetBytes("# DLSSG"));
                    Add("MANUAL.html", Encoding.UTF8.GetBytes("<html></html>"));
                    Add("docs/INSTALL.md", Encoding.UTF8.GetBytes("# install"));
                    Add("manifest.json", Encoding.UTF8.GetBytes("{}"));
                    foreach (var (name, content) in ModFiles) Add("dlssg_sm86/" + name, content);
                }
                return stream.ToArray();
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var url = request.RequestUri!.ToString();
            if (url == Dlssg2030UnlockDeploymentService.LatestReleaseApiUrl)
            {
                var package = Package;
                var digest = WrongDigest ? new string('b', 64) : DownloadRecordAssert.Sha256Of(package).ToLowerInvariant();
                return Task.FromResult(JsonResponse(System.Text.Json.JsonSerializer.Serialize(new
                {
                    tag_name = Tag,
                    assets = new[] { new { name = AssetName, size = package.Length, digest = "sha256:" + digest, browser_download_url = AssetUrl } }
                })));
            }
            if (url == AssetUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Package) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static byte[] Bytes(byte marker, int size) => Enumerable.Repeat(marker, size).ToArray();

        private static HttpResponseMessage JsonResponse(string json)
            => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
