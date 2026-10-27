using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #113 / w2-launch review, minors 4 and 5: with manual Update All checking every family first, a check that cannot
/// tell (offline, no matching package, a legacy install nothing is compared against) must read as "could not be
/// checked", never "current". One row per Update All family through the app's real services (offline), plus the
/// families' other could-not-tell paths, so dropping the flag from any one of them fails here.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class UpdateCheckCouldNotCheckTests
{
    public static TheoryData<ManagedComponentKind, string> Cases()
    {
        var data = new TheoryData<ManagedComponentKind, string>();
        foreach (var kind in LinuxUpdateAllService.SupportedComponents) data.Add(kind, "offline");
        // Luma: the offline catalog finds no package for a game outside its generic Unreal rule (the default row);
        // a UE4 DX11 game gets as far as the release lookup, which fails offline; a drag/drop install has no source.
        data.Add(ManagedComponentKind.LumaFramework, "generic-ue4");
        data.Add(ManagedComponentKind.LumaFramework, "local-package");
        // DLSS5 Feeder from before the full stack: never compared against a release (#115).
        data.Add(ManagedComponentKind.Dlss5Feeder, "legacy-addon-only");
        // ReShade No Addons (#91): its own reshade.me lookup, offline here.
        data.Add(ManagedComponentKind.ReShade, "no-addons");
        // #195: a RenoDX add-on dropped on RHI (or found by the watch folder) has no source to compare with.
        data.Add(ManagedComponentKind.RenoDX, "renodx-local-package");
        // w12/mfg (#236): installed as on disk (file there, proxy name recorded); only the release lookup cannot answer.
        data.Add(ManagedComponentKind.Rtx40MfgStandalone, "installed");
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task CheckThatCannotTell_IsCouldNotBeChecked_InBothUpdateAllModes(ManagedComponentKind kind, string variant)
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = Game(fx.Root, variant);
        var record = new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = kind,
            BinaryDirectory = game.BinaryDirectory!, Version = "1.0", SourceUrl = "https://github.com/rhi-tests/x/releases/download/1.0/x.zip"
        };
        if (kind == ManagedComponentKind.Dlss5Feeder && variant != "legacy-addon-only") record.Metadata["Feeder.FullStack"] = "1";
        if (variant == "local-package") record.Metadata[LumaFrameworkDeploymentService.MetaLocalPackage] = "1";
        if (variant == "no-addons") record.Metadata["ReShade.Channel"] = nameof(ReShadeChannel.NoAddons);
        if (variant == "renodx-local-package") record.Metadata[RenoDxDeploymentService.LocalPackageMetadataKey] = "1";
        if (kind == ManagedComponentKind.DxvkHdr)
        {
            // w7 follow-up B25: an installed DX9 HDR as it is on disk (a DirectX 9 game, its d3d9.dll still there); only the
            // release lookup cannot answer. A deleted d3d9.dll or a game no longer DX9 is a failure, not "could not check".
            game.GraphicsApi = GraphicsApi.DirectX9;
            var d3d9 = Path.Combine(game.BinaryDirectory!, "d3d9.dll");
            File.WriteAllBytes(d3d9, CacheForeignFileRejectionTests.Pe(0x8664, 4096, 9));
            record.Files.Add(new LinuxDeployedFile { Path = d3d9, Sha256 = RHI.Core.Deployment.FileHash.Sha256Hex(d3d9) });
        }
        if (variant == "installed") InstalledAsOnDisk(game, record);
        fx.Records.Upsert(record);

        var check = await fx.Managed.CheckManagedUpdateAsync(game, kind, []);

        Assert.True(check.Managed, check.Message);
        Assert.False(check.UpdateAvailable, check.Message);
        Assert.False(check.Blocked, check.Message);
        Assert.True(check.CouldNotCheck, $"{kind} ({variant}) read as current: {check.Message}");

        foreach (var mode in new[] { LinuxUpdateRunMode.Manual, LinuxUpdateRunMode.Background })
        {
            // Manual mode refreshes ReShade without a check (upstream parity); every other family is checked first.
            if (mode == LinuxUpdateRunMode.Manual && kind == ManagedComponentKind.ReShade) continue;
            var result = await new LinuxUpdateAllService(fx.Records, fx.Managed, new NeverRunning()).RunAsync([game], _ => [], mode);
            Assert.Equal((0, 0, 0, 1), (result.ChangedCount, result.CurrentCount, result.FailedCount, result.UncheckedCount));
        }
    }

    /// <summary>#178: one row per Update All family left out by the game's inclusion, and one per Settings component.</summary>
    public static TheoryData<ManagedComponentKind, string> ExcludedCases()
    {
        var data = new TheoryData<ManagedComponentKind, string>();
        foreach (var kind in LinuxUpdateAllService.SupportedComponents) data.Add(kind, "this-game");
        foreach (var kind in RHI.Platform.Linux.Paths.UpdateInclusionStore.GlobalComponents) data.Add(kind, "every-game");
        return data;
    }

    // #178: a family the update inclusion leaves out is neither checked, updated nor "could not be checked", in both
    // modes and through the app's real services: it counts as left out. Dropping the exclusion for any family turns its
    // row back into "could not be checked" (offline) and fails here.
    [Theory]
    [MemberData(nameof(ExcludedCases))]
    public async Task ExcludedFamily_IsLeftOut_NotCheckedOrCouldNotCheck_InBothUpdateAllModes(ManagedComponentKind kind, string scope)
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = Game(fx.Root, "offline");
        var record = new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = kind,
            BinaryDirectory = game.BinaryDirectory!, Version = "1.0", SourceUrl = "https://github.com/rhi-tests/x/releases/download/1.0/x.zip"
        };
        if (kind == ManagedComponentKind.Dlss5Feeder) record.Metadata["Feeder.FullStack"] = "1";
        fx.Records.Upsert(record);
        if (scope == "this-game")
            fx.Vm.UpdatePolicy!.Inclusion!.SetExcluded(game, [kind], [kind]);
        else
        {
            var settings = fx.Vm.Settings.GetLinuxSettingsSnapshot().Settings;
            settings.GlobalSkipReShadeUpdates = kind == ManagedComponentKind.ReShade;
            settings.GlobalSkipRenoDxUpdates = kind == ManagedComponentKind.RenoDX;
            settings.GlobalSkipReLimiterUpdates = kind == ManagedComponentKind.ReLimiter;
            settings.GlobalSkipDisplayCommanderUpdates = kind == ManagedComponentKind.DisplayCommander;
            settings.GlobalSkipOptiScalerUpdates = kind == ManagedComponentKind.OptiScaler;
            settings.GlobalSkipReFrameworkUpdates = kind == ManagedComponentKind.REFramework;
            fx.Vm.Settings.SaveLinuxSettings(settings);
        }

        foreach (var mode in new[] { LinuxUpdateRunMode.Manual, LinuxUpdateRunMode.Background })
        {
            var result = await new LinuxUpdateAllService(fx.Records, fx.Managed, new NeverRunning()).RunAsync([game], _ => [], mode);
            Assert.Equal((0, 0, 0, 0, 1), (result.ChangedCount, result.CurrentCount, result.FailedCount, result.UncheckedCount, result.ExcludedCount));
        }
    }

    /// <summary>
    /// w11/nr-gpu #230: a "Latest" NR DLL RHI cannot tell (no recorded version, no stamp, a PE version no published entry
    /// has, or one several share) is "could not be checked" — never current, never moved — even while the add-on itself is
    /// current (the Feeder's release answers here; for ShortFuse and Feeder the add-on part alone would read as current).
    /// </summary>
    [Theory]
    [InlineData("310,7,9,0")]       // no published entry has it
    [InlineData("310,8,3,0")]       // two published entries share it (the manifest below)
    public async Task NrDllRhiCannotTell_IsCouldNotBeChecked_InBothUpdateAllModes(string peVersion)
    {
        var shared = new[] { "310.8.3 (50xx)", "310.8.3 (20/30/40/50)" };
        HttpResponseMessage? Answer(HttpRequestMessage request) => request.RequestUri!.AbsoluteUri switch
        {
            RHI.Core.Services.DlssManagerService.ManifestUrl => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(NrGpuTestKit.ManifestJson(shared)) },
            "https://api.github.com/repos/jlrouzies-fr/DLSS5-Feeder/releases?per_page=1" => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""[{"tag_name":"v1.0","assets":[{"name":"DLSS5-Feeder-1.0.zip","browser_download_url":"https://github.com/rhi-tests/feeder.zip"}]}]""")
            },
            _ => null
        };
        using var fx = await ViewModelFixture.StartAsync([], httpResponse: Answer, hdrProfiles: NrGpuTestKit.Hdr(NrGpuTestKit.Facts(RHI.Core.Services.RtxGeneration.Rtx40)),
            nvidiaGpu: () => NrGpuTestKit.Facts(RHI.Core.Services.RtxGeneration.Rtx40));
        var game = Game(fx.Root, "nr-dll-unknown");
        var nr = Path.Combine(game.BinaryDirectory!, "nvngx_dlssnr.dll");
        File.WriteAllBytes(nr, PeVersionReaderTests.BuildVersionedPe(peVersion, peVersion, size: 80_000));
        NrGpuGames.SeedOwner(fx.Records, game, ManagedComponentKind.Dlss5Feeder, nr);

        var check = await fx.Managed.CheckManagedUpdateAsync(game, ManagedComponentKind.Dlss5Feeder, []);

        Assert.True(check.CouldNotCheck, check.Message);
        Assert.False(check.UpdateAvailable, check.Message);
        foreach (var mode in new[] { LinuxUpdateRunMode.Manual, LinuxUpdateRunMode.Background })
        {
            var result = await new LinuxUpdateAllService(fx.Records, fx.Managed, new NeverRunning()).RunAsync([game], _ => [], mode);
            Assert.Equal((0, 0, 0, 1), (result.ChangedCount, result.CurrentCount, result.FailedCount, result.UncheckedCount));
        }
    }

    /// <summary>
    /// w11/nr-gpu review finding 1: the record names the 310.8.3 (50xx) build an RTX 40 should move off, but the file on
    /// disk is no longer that file (the user put another DLL there by hand): "could not be checked" in both modes — never
    /// an update the swap would refuse on every run, never a failure.
    /// </summary>
    [Fact]
    public async Task NrDllChangedOutsideRhi_IsCouldNotBeChecked_InBothUpdateAllModes()
    {
        HttpResponseMessage? Answer(HttpRequestMessage request) => request.RequestUri!.AbsoluteUri switch
        {
            RHI.Core.Services.DlssManagerService.ManifestUrl => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(NrGpuTestKit.ManifestJson()) },
            "https://api.github.com/repos/jlrouzies-fr/DLSS5-Feeder/releases?per_page=1" => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""[{"tag_name":"v1.0","assets":[{"name":"DLSS5-Feeder-1.0.zip","browser_download_url":"https://github.com/rhi-tests/feeder.zip"}]}]""")
            },
            _ => null
        };
        using var fx = await ViewModelFixture.StartAsync([], httpResponse: Answer, hdrProfiles: NrGpuTestKit.Hdr(NrGpuTestKit.Facts(RHI.Core.Services.RtxGeneration.Rtx40)),
            nvidiaGpu: () => NrGpuTestKit.Facts(RHI.Core.Services.RtxGeneration.Rtx40));
        var game = Game(fx.Root, "nr-dll-changed");
        var nr = Path.Combine(game.BinaryDirectory!, "nvngx_dlssnr.dll");
        File.WriteAllBytes(nr, NrGpuTestKit.Dll(NrGpuTestKit.For50));
        NrGpuGames.SeedOwner(fx.Records, game, ManagedComponentKind.Dlss5Feeder, nr,
            new Dictionary<string, string> { [NeuralRenderingDllSwap.MetaDllVersion] = NrGpuTestKit.For50 });
        File.WriteAllBytes(nr, NrGpuTestKit.Dll(NrGpuTestKit.ForAll)); // replaced by hand; the record keeps 310.8.3's hash

        var check = await fx.Managed.CheckManagedUpdateAsync(game, ManagedComponentKind.Dlss5Feeder, []);

        Assert.True(check.CouldNotCheck, check.Message);
        Assert.False(check.UpdateAvailable, check.Message);
        foreach (var mode in new[] { LinuxUpdateRunMode.Manual, LinuxUpdateRunMode.Background })
        {
            var result = await new LinuxUpdateAllService(fx.Records, fx.Managed, new NeverRunning()).RunAsync([game], _ => [], mode);
            Assert.Equal((0, 0, 0, 1), (result.ChangedCount, result.CurrentCount, result.FailedCount, result.UncheckedCount));
        }
        Assert.Equal(NrGpuTestKit.Dll(NrGpuTestKit.ForAll), File.ReadAllBytes(nr));
    }

    // w12/mfg: the family's own file on disk under the name its record gives (a missing one is never "could not check" by
    // lookup: it says it was removed outside RHI before any lookup).
    private static void InstalledAsOnDisk(DetectedGame game, LinuxDeploymentRecord record)
    {
        var (name, nameKey) = record.Component switch
        {
            ManagedComponentKind.MfgAdaUnlock => ("renodx-mfgunlock.addon64", null),
            ManagedComponentKind.Dlssg2030Unlock => ("winmm.dll", "FG2030.ProxyName"),
            ManagedComponentKind.UltimateAsiLoader => ("winmm.dll", "UAL.ProxyName"),
            _ => ("winmm.dll", "UniversalMFG.ProxyName")
        };
        var path = Path.Combine(game.BinaryDirectory!, name);
        File.WriteAllBytes(path, CacheForeignFileRejectionTests.Pe(0x8664, 512 * 1024, 5));
        record.Files.Add(new LinuxDeployedFile { Path = path, Sha256 = RHI.Core.Deployment.FileHash.Sha256Hex(path) });
        if (nameKey is not null) record.Metadata[nameKey] = name;
        if (record.Component == ManagedComponentKind.Dlssg2030Unlock)
            File.WriteAllText(Path.Combine(game.BinaryDirectory!, Dlssg2030UnlockDeploymentService.IniFileName), "[Compatibility]\nRouter=SM75\n");
    }

    private static DetectedGame Game(string root, string variant)
    {
        var dir = Directory.CreateDirectory(Path.Combine(root, "games", "Checked Game")).FullName;
        var exe = Path.Combine(dir, "CheckedGame.exe");
        File.WriteAllBytes(exe, CacheForeignFileRejectionTests.Pe(0x8664, 4096, 7));
        var game = new DetectedGame
        {
            Name = "Checked Game", Source = "Manual", Runner = "Manual Windows", InstallPath = dir, ExecutablePath = exe, BinaryDirectory = dir,
            GraphicsApi = GraphicsApi.DirectX11
        };
        if (variant == "generic-ue4") game.EngineHint = "Unreal Engine 4";
        return game;
    }

    private sealed class NeverRunning : IGameRunningProbe
    {
        public bool IsRunning(DetectedGame game) => false;
    }
}
