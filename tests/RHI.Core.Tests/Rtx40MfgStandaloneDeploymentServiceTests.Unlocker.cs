using System.Net;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

/// <summary>
/// #278 (upstream 2.8.9): Universal MFG Unlocker — Intel's XeSS files in RTXMFG-XeSS beside a proxy install, the
/// OptiScaler-plugin placement with OptiScaler Nightly/F5/DLSS NR (moved in and back), and one API rule for every GPU.
/// </summary>
public sealed partial class Rtx40MfgStandaloneDeploymentServiceTests
{
    private sealed record FakeXess(IntelXessFiles Service, IntelXessFile Fg, IntelXessFile Xell, List<string> Requests);

    private static FakeXess CreateXess(GameFixture fixture, bool fail = false)
    {
        var fgBytes = CreatePe(0x8664, 4096, 0x61);
        var xellBytes = CreatePe(0x8664, 4096, 0x62);
        var fg = IntelXessFiles.LibXessFg with { Size = fgBytes.Length, Sha256 = Sha256(fgBytes).ToUpperInvariant() };
        var xell = IntelXessFiles.LibXell with { Size = xellBytes.Length, Sha256 = Sha256(xellBytes).ToUpperInvariant() };
        var requests = new List<string>();
        var http = new HttpClient(new XessHandler(request =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            if (fail) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = request };
            var bytes = request.RequestUri!.AbsolutePath.EndsWith("libxess_fg.dll", StringComparison.Ordinal) ? fgBytes : xellBytes;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes), RequestMessage = request };
        }));
        var service = new IntelXessFiles(http, Path.Combine(fixture.Root, "xess-cache")) { FrameGenerationFiles = [fg, xell] };
        return new FakeXess(service, fg, xell, requests);
    }

    private Rtx40MfgStandaloneDeploymentService CreateUnlocker(GameFixture fixture, HttpClient http, LinuxDeploymentRecordStore records, IntelXessFiles? xess)
        => new(http, records, new WineDllOverrideService(), new Rtx40MfgPreferenceStore(Path.Combine(fixture.Root, "prefs.json")),
            Path.Combine(fixture.Root, "cache")) { Xess = xess };

    private static Rtx40Handler Release(byte marker = 0x70) => Rtx40Handler.WithVersions(("1.7.0", CreateZip(("MFGUnlocker.dll", CreatePe(0x8664, 512 * 1024, marker)))));

    private static void AddOptiScaler(LinuxDeploymentRecordStore records, GameFixture fixture, OptiScalerVariant variant, bool withFolder = true)
    {
        if (withFolder) Directory.CreateDirectory(Path.Combine(fixture.Bin, "OptiScaler"));
        records.Upsert(ManagedOptiScalerRecord(fixture, variant));
    }

    [Fact]
    public void RowName_IsUniversalMfgUnlocker()
    {
        Assert.Equal("Universal MFG Unlocker", Rtx40MfgStandaloneDeploymentService.DisplayName);
        using var fixture = CreateGame("row-name", 0x8664, GraphicsApi.DirectX12);
        Assert.Equal("Universal MFG Unlocker", RHI.Core.Services.ComponentProbe.Probe(fixture.Game, [])
            .Single(c => c.Kind == ManagedComponentKind.Rtx40MfgStandalone).DisplayName);
    }

    // Upstream 2.8.9: RTX 30 is supported (RTX 20 experimental) and XeMFG runs on any GPU; there is no GPU generation
    // setting, so a saved RTX 30 choice no longer narrows the API (DX12, Vulkan experimental).
    [Fact]
    public void Eligibility_IsTheSameForEveryGpuGeneration()
    {
        using var fixture = CreateGame("any-gpu", 0x8664, GraphicsApi.Vulkan);
        Assert.True(Rtx40MfgStandaloneDeploymentService.IsGameEligible(fixture.Game, Rtx40MfgStandaloneDeploymentService.GpuGenRtx30));
        fixture.Game.GraphicsApi = GraphicsApi.DirectX11;
        Assert.False(Rtx40MfgStandaloneDeploymentService.IsGameEligible(fixture.Game, Rtx40MfgStandaloneDeploymentService.GpuGenRtx30));
        Assert.Contains("DirectX 11", Rtx40MfgStandaloneDeploymentService.EligibilityReason(fixture.Game, Rtx40MfgStandaloneDeploymentService.GpuGenRtx40), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProxyInstall_PutsIntelsFilesInRtxmfgXess_AndRemoveTakesThemAway()
    {
        using var fixture = CreateGame("xess-install", 0x8664, GraphicsApi.DirectX12);
        using var http = new HttpClient(Release());
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var xess = CreateXess(fixture);
        var service = CreateUnlocker(fixture, http, records, xess.Service);

        var installed = await service.InstallAsync(fixture.Game, "version.dll");

        Assert.True(installed.Success, installed.Message);
        var folder = Path.Combine(fixture.Bin, "RTXMFG-XeSS");
        Assert.Equal(FileHashOf(Path.Combine(folder, "libxess_fg.dll")), xess.Fg.Sha256);
        Assert.Equal(FileHashOf(Path.Combine(folder, "libxell.dll")), xess.Xell.Sha256);
        Assert.Equal(["https://raw.githubusercontent.com/intel/xess/v3.0.2/bin/libxess_fg.dll", "https://raw.githubusercontent.com/intel/xess/v3.0.2/bin/libxell.dll"], xess.Requests);
        Assert.Equal(new UniversalMfgStatus(false, true, false, "version.dll"), service.Status(fixture.Game));

        var removed = await service.RemoveAsync(fixture.Game);

        Assert.True(removed.Success, removed.Message);
        Assert.False(Directory.Exists(folder));
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "version.dll")));
    }

    // "If you put your own copies there, RHI leaves them alone" (upstream 2.8.9), on install and on removal.
    [Fact]
    public async Task UsersOwnXessCopy_IsLeftAlone()
    {
        using var fixture = CreateGame("xess-user", 0x8664, GraphicsApi.DirectX12);
        using var http = new HttpClient(Release());
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var folder = Directory.CreateDirectory(Path.Combine(fixture.Bin, "RTXMFG-XeSS")).FullName;
        File.WriteAllText(Path.Combine(folder, "libxell.dll"), "my own libxell");
        var service = CreateUnlocker(fixture, http, records, CreateXess(fixture).Service);

        var installed = await service.InstallAsync(fixture.Game, "version.dll");
        Assert.True(installed.Success, installed.Message);
        Assert.Contains(IntelXessFiles.KeptUserFilesPrefix, installed.Message, StringComparison.Ordinal);
        await service.RemoveAsync(fixture.Game);

        Assert.Equal("my own libxell", File.ReadAllText(Path.Combine(folder, "libxell.dll")));
        Assert.False(File.Exists(Path.Combine(folder, "libxess_fg.dll")));
    }

    // Existing installs get the files: the update check plans it (no new release needed) and Update adds them; a download
    // that fails at install leaves the mod installed and the next Update brings the files.
    [Fact]
    public async Task InstallFromBefore_GetsTheFilesThroughUpdate()
    {
        using var fixture = CreateGame("xess-later", 0x8664, GraphicsApi.DirectX12);
        using var http = new HttpClient(Release());
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var failing = CreateUnlocker(fixture, http, records, CreateXess(fixture, fail: true).Service);
        var installed = await failing.InstallAsync(fixture.Game, "version.dll");
        Assert.True(installed.Success, installed.Message);
        Assert.Contains(Rtx40MfgStandaloneDeploymentService.XessNotInstalledPrefix, installed.Message, StringComparison.Ordinal);

        var service = CreateUnlocker(fixture, http, records, CreateXess(fixture).Service);
        var check = await service.CheckManagedUpdateAsync(fixture.Game);
        Assert.True(check.UpdateAvailable, check.Message);
        Assert.Equal(Rtx40MfgStandaloneDeploymentService.XessDueMessage, check.Message);

        var updated = await service.UpdateAsync(fixture.Game);
        Assert.True(updated.Success, updated.Message);
        Assert.True(File.Exists(Path.Combine(fixture.Bin, "RTXMFG-XeSS", "libxell.dll")));
        Assert.False((await service.CheckManagedUpdateAsync(fixture.Game)).UpdateAvailable);
    }

    [Theory]
    [InlineData("Nightly")]
    [InlineData("F5")]
    [InlineData("DlssNr")]
    public async Task WithOptiScalerNightlyF5OrDlssNr_ItInstallsAsAPlugin(OptiScalerVariant variant)
    {
        using var fixture = CreateGame("plugin-" + variant, 0x8664, GraphicsApi.DirectX12);
        using var http = new HttpClient(Release());
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        AddOptiScaler(records, fixture, variant);
        var xess = CreateXess(fixture);
        var service = CreateUnlocker(fixture, http, records, xess.Service);

        var installed = await service.InstallAsync(fixture.Game, "version.dll");

        Assert.True(installed.Success, installed.Message);
        Assert.StartsWith(Rtx40MfgStandaloneDeploymentService.InstalledAsPluginPrefix, installed.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(fixture.Bin, "OptiScaler", "plugins", "MFGUnlocker.asi")));
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "version.dll")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Bin, "RTXMFG-XeSS")));
        Assert.Empty(xess.Requests);
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.Rtx40MfgStandalone)!.WineOverride);
        Assert.Equal(new UniversalMfgStatus(true, null, false, "OptiScaler/plugins/MFGUnlocker.asi"), service.Status(fixture.Game));

        var removed = await service.RemoveAsync(fixture.Game);
        Assert.True(removed.Success, removed.Message);
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "OptiScaler", "plugins", "MFGUnlocker.asi")));
    }

    // Stable OptiScaler is not supported by the mod: nothing changes there.
    [Fact]
    public async Task WithStableOptiScaler_ItStaysAProxy()
    {
        using var fixture = CreateGame("plugin-stable", 0x8664, GraphicsApi.DirectX12);
        using var http = new HttpClient(Release());
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        AddOptiScaler(records, fixture, OptiScalerVariant.Stable);
        var service = CreateUnlocker(fixture, http, records, CreateXess(fixture).Service);

        Assert.False(service.WouldInstallAsPlugin(fixture.Game));
        Assert.True((await service.InstallAsync(fixture.Game, "version.dll")).Success);
        Assert.True(File.Exists(Path.Combine(fixture.Bin, "version.dll")));
    }

    // "Games that already have both are moved over" and "removing OptiScaler moves the mod back" (upstream 2.8.9): the
    // proxy, its Wine override and the Intel files go when the mod moves in, and come back (preferring the old name).
    [Fact]
    public async Task MovesIntoOptiScaler_AndBackToItsOldName()
    {
        using var fixture = CreateGame("plugin-move", 0x8664, GraphicsApi.DirectX12);
        using var http = new HttpClient(Release());
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateUnlocker(fixture, http, records, CreateXess(fixture).Service);
        Assert.True((await service.InstallAsync(fixture.Game, "winmm.dll")).Success);
        Assert.Null(await service.MoveIntoOptiScalerAsync(fixture.Game)); // no OptiScaler yet: nothing to move
        Assert.Equal("native,builtin", ProxyOverride(fixture, "winmm"));
        AddOptiScaler(records, fixture, OptiScalerVariant.Nightly);

        var movedIn = await service.MoveIntoOptiScalerAsync(fixture.Game);

        Assert.Equal(Rtx40MfgStandaloneDeploymentService.MovedIntoOptiScalerMessage, movedIn);
        var plugin = Path.Combine(fixture.Bin, "OptiScaler", "plugins", "MFGUnlocker.asi");
        Assert.True(File.Exists(plugin));
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "winmm.dll")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Bin, "RTXMFG-XeSS")));
        Assert.Null(ProxyOverride(fixture, "winmm"));
        Assert.True(service.Status(fixture.Game).AsPlugin);

        var movedOut = await service.MoveOutOfOptiScalerAsync(fixture.Game);

        Assert.StartsWith(Rtx40MfgStandaloneDeploymentService.MovedOutOfOptiScalerPrefix + "winmm.dll.", movedOut, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(fixture.Bin, "winmm.dll")));
        Assert.False(File.Exists(plugin));
        Assert.Equal("native,builtin", ProxyOverride(fixture, "winmm"));
        Assert.True(File.Exists(Path.Combine(fixture.Bin, "RTXMFG-XeSS", "libxess_fg.dll")));
        var status = service.Status(fixture.Game);
        Assert.Equal((false, true, "winmm.dll"), (status.AsPlugin, status.XessInstalled, status.InstalledAs));
        Assert.True((await service.RemoveAsync(fixture.Game)).Success);
        Assert.Null(ProxyOverride(fixture, "winmm"));
    }

    // Moving back never takes a name another file already has: the next free one is used.
    [Fact]
    public async Task MovingBack_SkipsATakenName()
    {
        using var fixture = CreateGame("plugin-taken", 0x8664, GraphicsApi.DirectX12);
        using var http = new HttpClient(Release());
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        AddOptiScaler(records, fixture, OptiScalerVariant.F5);
        var service = CreateUnlocker(fixture, http, records, CreateXess(fixture).Service);
        Assert.True((await service.InstallAsync(fixture.Game)).Success);
        File.WriteAllText(Path.Combine(fixture.Bin, "version.dll"), "the game's own version.dll");

        var movedOut = await service.MoveOutOfOptiScalerAsync(fixture.Game);

        Assert.StartsWith(Rtx40MfgStandaloneDeploymentService.MovedOutOfOptiScalerPrefix + "winmm.dll.", movedOut, StringComparison.Ordinal);
        Assert.Equal("the game's own version.dll", File.ReadAllText(Path.Combine(fixture.Bin, "version.dll")));
    }

    private static string FileHashOf(string path) => RHI.Core.Deployment.FileHash.Sha256Hex(path);

    private sealed class XessHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
