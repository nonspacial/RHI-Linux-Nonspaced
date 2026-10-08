using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

public sealed class Dlssg2030UnlockDeploymentServiceTests : IDisposable
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-dlssg2030-tests-" + Guid.NewGuid().ToString("N"));

    public Dlssg2030UnlockDeploymentServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void UpstreamContract_ProxyAndGpuRoutesMatchCurrentContract()
    {
        Assert.Equal(new[] { "version.dll", "winmm.dll", "dinput8.dll", "winhttp.dll", "dxgi.dll" }, Dlssg2030UnlockDeploymentService.ProxyNames);
        Assert.Equal("SM86", Dlssg2030UnlockDeploymentService.RouterFor(Dlssg2030UnlockDeploymentService.GpuGenRtx30));
        Assert.Equal("SM75", Dlssg2030UnlockDeploymentService.RouterFor(Dlssg2030UnlockDeploymentService.GpuGenRtx20));
        Assert.Contains("sdli1995/dlssg_for_sm86", Dlssg2030UnlockDeploymentService.RepositoryUrl, StringComparison.Ordinal);
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
    public void RouterWriter_MapsAmpereToSm86AndTuringToSm75()
    {
        var template = UpstreamIni("SM86");
        Assert.Contains("Router=SM86", Dlssg2030UnlockDeploymentService.WriteRouter(template, "SM86"), StringComparison.Ordinal);
        Assert.Contains("Router=SM75", Dlssg2030UnlockDeploymentService.WriteRouter(template, "SM75"), StringComparison.Ordinal);
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
        using var http = new HttpClient(new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x28), UpstreamIni("SM86")));
        var service = CreateService(fixture, http, new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json")));

        var result = service.SetPreferences(fixture.Game, Dlssg2030UnlockDeploymentService.GpuGenRtx30, "version.dll");

        Assert.False(result.Success);
        Assert.Contains("Universal MFG", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Install_Rtx30IsBlockedBeforeNetworkAndRedirectsToUniversalMfg()
    {
        using var fixture = CreateGame("redirect-install", 0x8664, GraphicsApi.DirectX12);
        using var handler = new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x29), UpstreamIni("SM86"));
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
        using var handler = new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x31), UpstreamIni("SM86"));
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
        using var handler = new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x32), UpstreamIni("SM75"));
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
    public async Task Install_Rtx20_WritesSm75RouterAndMetadata()
    {
        using var fixture = CreateGame("rtx20", 0x8664, GraphicsApi.DirectX12);
        using var http = new HttpClient(new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x32), UpstreamIni("SM86")));
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);
        var result = await service.InstallAsync(fixture.Game, Dlssg2030UnlockDeploymentService.GpuGenRtx20, "winmm.dll");
        Assert.True(result.Success, result.Message);
        Assert.Contains("Router=SM75", File.ReadAllText(Path.Combine(fixture.Bin, Dlssg2030UnlockDeploymentService.IniFileName)), StringComparison.Ordinal);
        var record = records.Find(fixture.Game, ManagedComponentKind.Dlssg2030Unlock)!;
        Assert.Equal("SM75", record.Metadata["FG2030.Router"]);
        Assert.Equal(Dlssg2030UnlockDeploymentService.GpuGenRtx20, record.Metadata["FG2030.GpuGeneration"]);
        Assert.Equal("winmm.dll", record.Metadata["FG2030.ProxyName"]);
    }

    [Fact]
    public async Task Install_BlocksExternalProxyBeforeNetwork()
    {
        using var fixture = CreateGame("external", 0x8664, GraphicsApi.DirectX12);
        File.WriteAllText(Path.Combine(fixture.Bin, "dxgi.dll"), "foreign");
        using var handler = new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x33), UpstreamIni("SM86"));
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
        var target = Path.Combine(fixture.Bin, "winhttp.dll");
        File.WriteAllText(target, "owned");
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(fixture.Game), GameName = fixture.Game.Name,
            Component = ManagedComponentKind.ReShade, BinaryDirectory = fixture.Bin,
            Files = [new LinuxDeployedFile { Path = target, Sha256 = "deadbeef" }]
        });
        using var handler = new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x34), UpstreamIni("SM86"));
        using var http = new HttpClient(handler);
        var service = CreateService(fixture, http, records);
        var result = await service.InstallAsync(fixture.Game, proxyName: "winhttp.dll");
        Assert.False(result.Success);
        Assert.Contains("outro componente", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Install_RejectsX86PayloadBeforeGameMutation()
    {
        using var fixture = CreateGame("badpayload", 0x8664, GraphicsApi.DirectX12);
        using var http = new HttpClient(new DlssgHandler(CreatePe(0x014c, 320 * 1024, 0x35), UpstreamIni("SM86")));
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
        using var http = new HttpClient(new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x36), UpstreamIni("SM86")));
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
        using var http = new HttpClient(new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x37), UpstreamIni("SM86")));
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
        using var http = new HttpClient(new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x38), UpstreamIni("SM86")));
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
        using var http = new HttpClient(new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x39), UpstreamIni("SM86")));
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

    private static string UpstreamIni(string router) => $"""
        ; Native 0.2.4. Restart the game after changing this file.
        [Compatibility]
        Router={router}
        KernelImage=PTX
        HardwareBilinear=0

        [FrameGeneration]
        MaxGeneratedFrames=3

        [Logging]
        Level=1
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

    private sealed class DlssgHandler(byte[] dll, string ini) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var url = request.RequestUri!.ToString();
            if (url == Dlssg2030UnlockDeploymentService.CommitsApiUrl)
                return Task.FromResult(JsonResponse($"[{{\"sha\":\"{Commit}\"}}]"));
            if (url == Dlssg2030UnlockDeploymentService.RawUrl(Commit, Dlssg2030UnlockDeploymentService.DefaultProxyName))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(dll) });
            if (url == Dlssg2030UnlockDeploymentService.RawUrl(Commit, Dlssg2030UnlockDeploymentService.IniFileName))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ini, Encoding.UTF8, "text/plain") });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse(string json)
            => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
