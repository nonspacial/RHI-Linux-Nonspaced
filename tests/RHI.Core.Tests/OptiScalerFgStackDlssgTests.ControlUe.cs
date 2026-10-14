using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// w3/dllnames phase B (#136): Control UE's optional frame-generation step (upstream 2.8.1 step 6/7, pinned to nightly
// 20261004 by 2.8.7). OptiScaler Nightly pinned to that build as winmm.dll, its Streamline/DLSS Enabler stack and
// upstream's FG settings; FgSlHdr10 under [renodx-preset1] owned on the RenoDX record. Each step stands alone and never
// fails or changes the RenoDX install.
public sealed partial class OptiScalerFgStackDlssgTests
{
    private sealed class PinnedOnlyProvider(string root, bool fail = false) : IOptiScalerPackageProvider
    {
        public List<string> Calls { get; } = [];

        public Task<OptiScalerPackage> GetLatestAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Control UE frame generation must never install the latest build.");

        public Task<OptiScalerPackage> GetLatestAsync(OptiScalerVariant variant, CancellationToken cancellationToken = default)
            => GetLatestAsync(cancellationToken);

        public Task<OptiScalerPackage> GetNightlyBuildAsync(string build, CancellationToken cancellationToken = default)
        {
            Calls.Add(build);
            if (fail) throw new HttpRequestException("nightly.link is down (test)");
            Directory.CreateDirectory(root);
            var dll = Enumerable.Repeat((byte)0x20, 4096).ToArray();
            dll[0] = (byte)'M'; dll[1] = (byte)'Z';
            Encoding.ASCII.GetBytes("optiscaler/OptiScaler nightly " + build).CopyTo(dll, 128);
            File.WriteAllBytes(Path.Combine(root, "OptiScaler.dll"), dll);
            File.WriteAllText(Path.Combine(root, "OptiScaler.ini"), "[Upscalers]\nDx12Upscaler=auto\n[FrameGen]\nEnabled=auto\nFGInput=auto\nFGOutput=auto\nFGNvngxReplacement=auto\n[fakenvapi]\nForceReflex=auto\n[DLSSG]\nUseGamesReflexMarkers=auto\n[Plugins]\nLoadReshade=auto\n");
            return Task.FromResult(new OptiScalerPackage(build, "https://github.com/optiscaler/OptiScaler-nightly/releases/nightly-" + build, root, OptiScalerVariant.Nightly));
        }
    }

    private static LinuxDeploymentRecord SeedRenoDx(Fixture fx, string reshadeIni)
    {
        File.WriteAllText(Path.Combine(fx.Bin, "reshade.ini"), reshadeIni);
        var addon = Path.Combine(fx.Bin, "renodx-control-rr.addon64");
        File.WriteAllText(addon, "MZ renodx control");
        var record = new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(fx.Game),
            GameName = fx.Game.Name,
            Component = ManagedComponentKind.RenoDX,
            BinaryDirectory = fx.Bin,
            Version = "Control RR",
            Files = [new LinuxDeployedFile { Path = addon, Sha256 = FileHash.Sha256Hex(addon) }]
        };
        fx.Store.Upsert(record);
        return record;
    }

    private static string? GameOverride(Fixture fx, string dll)
        => WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(fx.Game.PrefixPath!, "user.reg")), dll, "Game.exe");

    [Fact]
    public async Task ControlUeFrameGeneration_InstallsThePinnedBuildAsWinmm_WithTheStackAndUpstreamSettings_AndOwnsFgSlHdr10()
    {
        var provider = new PinnedOnlyProvider(Path.Combine(_root, "cue-pkg"));
        var fx = CreateFixture("cue-fg", packages: provider, seedOptiScaler: false);
        var renoDx = SeedRenoDx(fx, "[GENERAL]\nEffectSearchPaths=.\\reshade-shaders\\Shaders\n");
        var service = new ControlUeFrameGenerationService(fx.Service, fx.Store);

        var result = await service.InstallAsync(fx.Game, useHdr: true);

        Assert.True(result.Success, result.Message);
        Assert.StartsWith(ControlUeFrameGenerationService.SetUpPrefix + "20261004.", result.Message);
        Assert.Contains(ControlUeFrameGenerationService.HdrSetPrefix + "1", result.Message);
        Assert.Equal(["20261004"], provider.Calls);
        Assert.True(BinarySignatureInspector.LooksLikeOptiScaler(Path.Combine(fx.Bin, "winmm.dll")));
        Assert.False(File.Exists(Path.Combine(fx.Bin, "dxgi.dll")));
        Assert.Equal("native,builtin", GameOverride(fx, "winmm"));
        var record = fx.Store.Find(fx.Game, ManagedComponentKind.OptiScaler)!;
        Assert.Equal("20261004", record.Version); // shown as installed
        Assert.Equal("20261004", OptiScalerDeploymentService.GetPinnedNightlyBuild(record));
        Assert.Equal("winmm.dll", OptiScalerDeploymentService.GetProxyName(record));
        Assert.Equal("1", record.Metadata[OptiScalerDeploymentService.FgStackEnabledMetadataKey]);
        Assert.True(File.Exists(Path.Combine(fx.Bin, "OptiScaler", "Streamline", "nvngx_dlssg.dll")));
        Assert.True(File.Exists(Path.Combine(fx.Bin, "OptiScaler", DlssEnablerFile)));
        var ini = File.ReadAllText(Path.Combine(fx.Bin, "OptiScaler.ini"));
        foreach (var line in new[] { "Dx12Upscaler=dlss", "Enabled=true", "FGInput=upscaler", "FGOutput=dlssg", "FGNvngxReplacement=None", "ForceReflex=2", "UseGamesReflexMarkers=false" })
            Assert.Contains(line, ini);
        var reshadeIni = File.ReadAllText(Path.Combine(fx.Bin, "reshade.ini"));
        Assert.Contains("[renodx-preset1]\nFgSlHdr10=1\n", reshadeIni);
        var renoDxAfter = fx.Store.Find(fx.Game, ManagedComponentKind.RenoDX)!;
        Assert.True(renoDxAfter.Metadata.ContainsKey(ControlUeFrameGenerationService.HdrMetadataKey));
        Assert.Equal(renoDx.Files[0].Sha256, renoDxAfter.Files[0].Sha256);

        // Update All: the pin keeps it current, never an update.
        var check = await fx.Service.CheckUpdateAsync(fx.Game);
        Assert.False(check.UpdateAvailable);
        Assert.False(check.CouldNotCheck);
    }

    [Fact]
    public async Task ControlUeFrameGeneration_OptiScalerDownloadFails_ReportsIt_StillWritesTheHdrKey_AndLeavesRenoDxAsItIs()
    {
        var fx = CreateFixture("cue-fail", packages: new PinnedOnlyProvider(Path.Combine(_root, "cue-fail-pkg"), fail: true), seedOptiScaler: false);
        SeedRenoDx(fx, "[GENERAL]\n");
        var ledgerAddon = File.ReadAllText(Path.Combine(fx.Bin, "renodx-control-rr.addon64"));
        var service = new ControlUeFrameGenerationService(fx.Service, fx.Store);

        var result = await service.InstallAsync(fx.Game, useHdr: false);

        Assert.False(result.Success);
        Assert.StartsWith(ControlUeFrameGenerationService.NotSetUpPrefix, result.Message);
        Assert.Contains(ControlUeFrameGenerationService.HdrSetPrefix + "0", result.Message);
        Assert.Null(fx.Store.Find(fx.Game, ManagedComponentKind.OptiScaler));
        Assert.False(File.Exists(Path.Combine(fx.Bin, "winmm.dll")));
        Assert.NotNull(fx.Store.Find(fx.Game, ManagedComponentKind.RenoDX));
        Assert.Equal(ledgerAddon, File.ReadAllText(Path.Combine(fx.Bin, "renodx-control-rr.addon64")));
        Assert.Contains("[renodx-preset1]\nFgSlHdr10=0\n", File.ReadAllText(Path.Combine(fx.Bin, "reshade.ini")));
    }

    [Fact]
    public async Task ControlUeFrameGeneration_OptiScalerAlreadyInstalledOnAnotherBuild_IsLeftAlone()
    {
        var provider = new PinnedOnlyProvider(Path.Combine(_root, "cue-other-pkg"));
        var fx = CreateFixture("cue-other", packages: provider); // RHI's OptiScaler Nightly (Latest) as dxgi.dll
        SeedRenoDx(fx, "[GENERAL]\n");
        var before = File.ReadAllText(fx.RecordsPath);
        var iniBefore = File.ReadAllText(fx.Ini);
        var service = new ControlUeFrameGenerationService(fx.Service, fx.Store);

        var result = await service.InstallAsync(fx.Game, useHdr: false);

        Assert.False(result.Success);
        Assert.Contains(ControlUeFrameGenerationService.OptiScalerElsewherePrefix, result.Message);
        Assert.Empty(provider.Calls);
        Assert.False(File.Exists(Path.Combine(fx.Bin, "winmm.dll")));
        Assert.Equal(iniBefore, File.ReadAllText(fx.Ini));
        var record = fx.Store.Find(fx.Game, ManagedComponentKind.OptiScaler)!;
        Assert.Equal(new LinuxDeploymentRecordStore(fx.RecordsPath).Find(fx.Game, ManagedComponentKind.OptiScaler)!.Version, record.Version);
        Assert.Null(OptiScalerDeploymentService.GetPinnedNightlyBuild(record));
        Assert.NotEqual(before, File.ReadAllText(fx.RecordsPath)); // only the RenoDX record gained the FgSlHdr10 state
    }

    [Fact]
    public async Task ControlUeFrameGeneration_RenoDxRemoval_PutsFgSlHdr10Back_AndKeepsAUserValue()
    {
        var provider = new PinnedOnlyProvider(Path.Combine(_root, "cue-remove-pkg"));
        var fx = CreateFixture("cue-remove", packages: provider, seedOptiScaler: false);
        var original = "[GENERAL]\nX=1\n";
        SeedRenoDx(fx, original);
        var service = new ControlUeFrameGenerationService(fx.Service, fx.Store);
        Assert.True((await service.InstallAsync(fx.Game, useHdr: true)).Success);
        using var http = new HttpClient(new FixtureHandler(true));
        var renodx = new RenoDxDeploymentService(http, fx.Store, new RenoDxCatalogService(http), Path.Combine(_root, "cue-remove-renodx"));

        var removed = await renodx.RemoveAsync(fx.Game);

        Assert.True(removed.Success, removed.Message);
        Assert.Equal(original, File.ReadAllText(Path.Combine(fx.Bin, "reshade.ini")));
        // OptiScaler stays a normally managed component (removed on its own).
        Assert.NotNull(fx.Store.Find(fx.Game, ManagedComponentKind.OptiScaler));

        // A value the user changed is kept on removal.
        var fx2 = CreateFixture("cue-remove-user", packages: new PinnedOnlyProvider(Path.Combine(_root, "cue-remove-user-pkg")), seedOptiScaler: false);
        SeedRenoDx(fx2, original);
        Assert.True((await new ControlUeFrameGenerationService(fx2.Service, fx2.Store).InstallAsync(fx2.Game, useHdr: true)).Success);
        var path = Path.Combine(fx2.Bin, "reshade.ini");
        File.WriteAllText(path, File.ReadAllText(path).Replace("FgSlHdr10=1", "FgSlHdr10=0"));
        var renodx2 = new RenoDxDeploymentService(http, fx2.Store, new RenoDxCatalogService(http), Path.Combine(_root, "cue-remove-user-renodx"));
        Assert.True((await renodx2.RemoveAsync(fx2.Game)).Success);
        Assert.Contains("FgSlHdr10=0", File.ReadAllText(path));
    }
}
