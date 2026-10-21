using System.Text.Json;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Launch;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #211: per-game NVIDIA DLSS settings → dxvk-nvapi launch variables (presets, render scale, RTX 50 frame generation,
/// the NGX updater), merged into the launch plan for every HDR mode; store hand-offs get them in the copyable line and
/// RHI applies nothing; hidden where they cannot apply. Nothing here starts a game or a launcher.
/// </summary>
public sealed class NvidiaLaunchSettingsTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-nv-" + Guid.NewGuid().ToString("N"))).FullName;

    private static readonly NvidiaDlssFamilies AllFiles = new(true, true, true);
    private static readonly LinuxHdrHostSnapshot Rtx40 = Host(new NvidiaGpuFacts(true, false, "NVIDIA GeForce RTX 4090"));
    private static readonly LinuxHdrHostSnapshot Rtx50 = Host(new NvidiaGpuFacts(true, true, "NVIDIA GeForce RTX 5090"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---- one variable per setting ---------------------------------------------------------------------------------

    public static TheoryData<string, uint, string, string> PresetCases => new()
    {
        { "SR", 11, "DXVK_NVAPI_DRS_NGX_DLSS_SR_OVERRIDE_RENDER_PRESET_SELECTION", "11" },
        { "SR", DlssPresetCatalogue.Recommended, "DXVK_NVAPI_DRS_NGX_DLSS_SR_OVERRIDE_RENDER_PRESET_SELECTION", "0xffffff" },
        { "RR", 5, "DXVK_NVAPI_DRS_NGX_DLSS_RR_OVERRIDE_RENDER_PRESET_SELECTION", "5" },
        { "FG", 3, "DXVK_NVAPI_DRS_NGX_DLSS_FG_OVERRIDE_RENDER_PRESET_SELECTION", "3" },
        { "FG", DlssPresetCatalogue.FrameGenerationRecommended, "DXVK_NVAPI_DRS_NGX_DLSS_FG_OVERRIDE_RENDER_PRESET_SELECTION", "0xfffffe" },
    };

    [Theory]
    [MemberData(nameof(PresetCases))]
    public void APreset_SetsExactlyItsOwnVariable(string family, uint value, string variable, string expected)
    {
        var settings = new NvidiaGameSettings();
        if (family == "SR") settings.SuperResolutionPreset = value;
        else if (family == "RR") settings.RayReconstructionPreset = value;
        else settings.FrameGenerationPreset = value;

        var env = NvidiaLaunchSettings.Environment(settings.Normalize(), AllFiles, frameGeneration: false);

        // The Frame Generation preset is unknown by name to Proton 10's dxvk-nvapi: also in the numeric form.
        var wanted = new Dictionary<string, string> { [variable] = expected };
        if (family == "FG") wanted["DXVK_NVAPI_DRS_SETTINGS"] = "0x10e41df1=" + expected;
        Assert.Equal(wanted, env);
    }

    [Fact]
    public void RenderScale_SetsTheCustomModeAndTheRatio_ForSrAndRr()
    {
        var env = NvidiaLaunchSettings.Environment(
            new NvidiaGameSettings { SuperResolutionRenderScale = 67, RayReconstructionRenderScale = 100 }.Normalize(), AllFiles, false);

        Assert.Equal(new Dictionary<string, string>
        {
            ["DXVK_NVAPI_DRS_NGX_DLSS_SR_MODE"] = "6",
            ["DXVK_NVAPI_DRS_NGX_DLSS_SR_OVERRIDE_SCALING_RATIO"] = "67",
            ["DXVK_NVAPI_DRS_NGX_DLSS_RR_MODE"] = "6",
            ["DXVK_NVAPI_DRS_NGX_DLSS_RR_OVERRIDE_SCALING_RATIO"] = "100",
            // dxvk-nvapi v0.9.0 has no name for either ratio: also in the numeric form.
            ["DXVK_NVAPI_DRS_SETTINGS"] = "0x10c7d4a2=100,0x10e41df5=67",
        }, env);
    }

    [Theory]
    [InlineData(32u)]
    [InlineData(101u)]
    public void ARenderScaleTheDriverWouldNotTake_IsTheGamesOwn(uint percent)
    {
        var settings = new NvidiaGameSettings { SuperResolutionRenderScale = percent }.Normalize();

        Assert.Equal(0u, settings.SuperResolutionRenderScale);
        Assert.True(settings.IsDefault());
    }

    [Fact]
    public void FixedFrameGeneration_OnAnRtx50_SetsModeOnAndTheFrameCount()
    {
        var env = NvidiaLaunchSettings.Environment(
            new NvidiaGameSettings { FrameGenerationMode = NvidiaFrameGenerationModes.Fixed, FrameCount = 3 }.Normalize(), AllFiles, true);

        Assert.Equal(new Dictionary<string, string>
        {
            ["DXVK_NVAPI_DRS_NGX_DLSSG_MODE"] = "2",
            ["DXVK_NVAPI_DRS_NGX_DLSSG_MULTI_FRAME_COUNT"] = "3",
            ["DXVK_NVAPI_DRS_SETTINGS"] = "0x10308298=2", // the mode is unknown by name to Proton 10's dxvk-nvapi
        }, env);
    }

    [Fact]
    public void DynamicFrameGeneration_OnAnRtx50_SetsModeDynamicTheMaximumAndTheTarget()
    {
        var env = NvidiaLaunchSettings.Environment(new NvidiaGameSettings
        {
            FrameGenerationMode = NvidiaFrameGenerationModes.Dynamic, DynamicMaxFrameCount = 5,
            DynamicTargetFrameRate = NvidiaGameSettings.TargetFrameRateMaxRefresh
        }.Normalize(), AllFiles, true);

        Assert.Equal(new Dictionary<string, string>
        {
            ["DXVK_NVAPI_DRS_NGX_DLSSG_MODE"] = "4",
            ["DXVK_NVAPI_DRS_NGX_DLSSG_DYNAMIC_MULTI_FRAME_COUNT_MAX"] = "5",
            ["DXVK_NVAPI_DRS_NGX_DLSSG_DYNAMIC_TARGET_FRAME_RATE"] = "0x1000000",
            ["DXVK_NVAPI_DRS_SETTINGS"] = "0x10308298=4,0x10562d0f=5,0x10cf4125=0x1000000",
        }, env);
    }

    [Fact]
    public void AModeKeepsOnlyItsOwnValues_AsUpstreamClearsTheOthers()
    {
        var fixedMode = new NvidiaGameSettings
        {
            FrameGenerationMode = NvidiaFrameGenerationModes.Fixed, FrameCount = 2, DynamicMaxFrameCount = 4, DynamicTargetFrameRate = 116
        }.Normalize();
        Assert.Equal((2u, 0u, 0u), (fixedMode.FrameCount, fixedMode.DynamicMaxFrameCount, fixedMode.DynamicTargetFrameRate));

        var dynamicMode = new NvidiaGameSettings { FrameGenerationMode = "Dynamic", FrameCount = 2, DynamicTargetFrameRate = 19 }.Normalize();
        Assert.Equal((0u, 0u), (dynamicMode.FrameCount, dynamicMode.DynamicTargetFrameRate)); // 19 FPS is below upstream's 20

        var defaultMode = new NvidiaGameSettings { FrameGenerationMode = "nonsense", FrameCount = 2 }.Normalize();
        Assert.True(defaultMode.IsDefault());
    }

    [Fact]
    public void FrameGeneration_WithoutAnRtx50_AddsNothing_ButTheFgPresetStays()
    {
        var settings = new NvidiaGameSettings { FrameGenerationPreset = 2, FrameGenerationMode = NvidiaFrameGenerationModes.Fixed, FrameCount = 3 }.Normalize();

        var env = NvidiaLaunchSettings.Environment(settings, AllFiles, frameGeneration: false);

        Assert.Equal(new Dictionary<string, string>
        {
            ["DXVK_NVAPI_DRS_NGX_DLSS_FG_OVERRIDE_RENDER_PRESET_SELECTION"] = "2",
            ["DXVK_NVAPI_DRS_SETTINGS"] = "0x10e41df1=2",
        }, env);
    }

    // ---- Proton 10 (finding 3): names its dxvk-nvapi lacks are also written by number --------------------------------

    [Fact]
    public void NamesOlderDxvkNvapiBuildsLack_AreAlsoWrittenByNumber_InIdOrder_AndKnownNamesAreNot()
    {
        var env = NvidiaLaunchSettings.Environment(new NvidiaGameSettings
        {
            SuperResolutionPreset = 11, SuperResolutionRenderScale = 67, RayReconstructionPreset = 5, FrameGenerationPreset = 3,
            FrameGenerationMode = NvidiaFrameGenerationModes.Dynamic, DynamicMaxFrameCount = 3, DynamicTargetFrameRate = 138, NewestDlss = true
        }.Normalize(), AllFiles, frameGeneration: true);

        // Ids per NvApiDriverSettings.h; names per dxvk-nvapi util_drs.h at v0.9.0 (no ratios) and Proton 10's 0ea2317
        // (no FG preset, no DLSSG mode/dynamic names). Presets SR/RR, SR mode, overrides and the NGX updater stay by name only.
        Assert.Equal("0x10308298=4,0x10562d0f=3,0x10cf4125=138,0x10e41df1=3,0x10e41df5=67", env["DXVK_NVAPI_DRS_SETTINGS"]);
        Assert.Equal("11", env["DXVK_NVAPI_DRS_NGX_DLSS_SR_OVERRIDE_RENDER_PRESET_SELECTION"]);
        Assert.Equal("3", env["DXVK_NVAPI_DRS_NGX_DLSS_FG_OVERRIDE_RENDER_PRESET_SELECTION"]);
        Assert.Equal("4", env["DXVK_NVAPI_DRS_NGX_DLSSG_MODE"]);
    }

    [Fact]
    public void ADirectLaunch_KeepsTheUsersOwnDrsSettingsInFront_AndAHandoffLineHoldsOnlyRhis()
    {
        var settings = new NvidiaGameSettings { FrameGenerationPreset = 2 };
        var service = new LinuxHdrLaunchProfileService(
            environment: name => name == "DXVK_NVAPI_DRS_SETTINGS" ? "0x10e41df1=1,0x1234=5" : null,
            commandExists: _ => false, nvidiaVersion: () => null, nvidiaGpu: () => Rtx40.Gpu);

        var direct = service.BuildPlan(Direct(), Profile(LinuxHdrModes.Off, settings), Rtx40, AllFiles);
        var steam = service.BuildPlan(Steam(), Profile(LinuxHdrModes.Off, settings), Rtx40, AllFiles);

        // dxvk-nvapi keeps the last value for an id: RHI's 2 wins over the user's 1, the user's 0x1234 stays.
        Assert.Equal("0x10e41df1=1,0x1234=5,0x10e41df1=2", direct.Environment["DXVK_NVAPI_DRS_SETTINGS"]);
        Assert.Equal("0x10e41df1=2", steam.Environment["DXVK_NVAPI_DRS_SETTINGS"]);
        Assert.Equal("DXVK_NVAPI_DRS_NGX_DLSS_FG_OVERRIDE_RENDER_PRESET_SELECTION=2 DXVK_NVAPI_DRS_SETTINGS='0x10e41df1=2' %command%", steam.LaunchOptions);
    }

    // ---- the Frame Generation preset needs an RTX 40 or newer (finding 5) ---------------------------------------------

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3080", false)]
    [InlineData("NVIDIA GeForce RTX 2060 SUPER", false)]
    [InlineData("NVIDIA GeForce GTX 1660 Ti", false)]
    [InlineData("Quadro RTX 4000", false)]
    [InlineData("NVIDIA RTX A4000", false)]
    [InlineData("NVIDIA GeForce RTX 4060 Laptop GPU", true)]
    [InlineData("NVIDIA RTX 2000 Ada Generation", true)]
    [InlineData("NVIDIA GeForce RTX 5090", true)]
    [InlineData(null, true)] // model unknown (PCI fallback): kept
    public void TheFrameGenerationPreset_IsOfferedAndWrittenOnlyOnAGpuThatRunsFrameGeneration(string? model, bool offered)
    {
        var host = Host(new NvidiaGpuFacts(true, NvidiaGpuProbe.IsRtx50(model), model));

        var plan = NvidiaLaunchSettings.Plan(Direct(), new NvidiaGameSettings { FrameGenerationPreset = 2 }, host, AllFiles);

        Assert.Equal(offered, plan.FrameGenerationPresetOffered);
        Assert.Equal(offered, plan.Environment.ContainsKey("DXVK_NVAPI_DRS_NGX_DLSS_FG_OVERRIDE_RENDER_PRESET_SELECTION"));
        Assert.Equal(offered, plan.Environment.ContainsKey("DXVK_NVAPI_DRS_SETTINGS"));
    }

    [Fact]
    public void NewestDlss_TurnsOnTheNgxUpdaterAndTheOverrideOfEachFeatureTheGameShips()
    {
        var env = NvidiaLaunchSettings.Environment(new NvidiaGameSettings { NewestDlss = true }, new NvidiaDlssFamilies(true, false, true), false);

        Assert.Equal(new Dictionary<string, string>
        {
            ["PROTON_ENABLE_NGX_UPDATER"] = "1",
            ["DXVK_NVAPI_DRS_NGX_DLSS_SR_OVERRIDE"] = "1",
            ["DXVK_NVAPI_DRS_NGX_DLSS_FG_OVERRIDE"] = "1",
        }, env);
    }

    [Fact]
    public void ASettingForAFeatureTheGameDoesNotShip_AddsNothing()
    {
        var env = NvidiaLaunchSettings.Environment(new NvidiaGameSettings
        {
            RayReconstructionPreset = 5, RayReconstructionRenderScale = 50, FrameGenerationPreset = 1, SuperResolutionPreset = 11
        }.Normalize(), new NvidiaDlssFamilies(true, false, false), true);

        Assert.Equal(["DXVK_NVAPI_DRS_NGX_DLSS_SR_OVERRIDE_RENDER_PRESET_SELECTION"], env.Keys);
    }

    [Fact]
    public void Default_AddsNothing_AndNeverAVariableThatWritesToDisk()
    {
        Assert.Empty(NvidiaLaunchSettings.Environment(new NvidiaGameSettings(), AllFiles, true));
        var all = NvidiaLaunchSettings.Environment(new NvidiaGameSettings
        {
            SuperResolutionPreset = 11, RayReconstructionPreset = 5, FrameGenerationPreset = 3, SuperResolutionRenderScale = 50,
            RayReconstructionRenderScale = 50, FrameGenerationMode = "Dynamic", DynamicMaxFrameCount = 3, DynamicTargetFrameRate = 138, NewestDlss = true
        }.Normalize(), AllFiles, true);
        Assert.DoesNotContain("DXVK_NVAPI_SET_NGX_DEBUG_OPTIONS", all.Keys);
        Assert.All(all.Keys, key => Assert.True(key.StartsWith("DXVK_NVAPI_DRS_NGX_", StringComparison.Ordinal)
                                                || key is "PROTON_ENABLE_NGX_UPDATER" or "DXVK_NVAPI_DRS_SETTINGS", key));
    }

    // ---- the launch plan --------------------------------------------------------------------------------------------

    [Fact]
    public void HdrOff_WithNvidiaSettings_HasAnEnvironment_AndAppliesAtLaunch()
    {
        var plan = Service().BuildPlan(Direct(), Profile(LinuxHdrModes.Off, new NvidiaGameSettings { SuperResolutionPreset = 11 }), Rtx40, AllFiles);

        Assert.False(plan.Enabled);
        Assert.Equal("11", plan.Environment["DXVK_NVAPI_DRS_NGX_DLSS_SR_OVERRIDE_RENDER_PRESET_SELECTION"]);
        Assert.True(plan.AppliesAtLaunch);
        Assert.Equal("DXVK_NVAPI_DRS_NGX_DLSS_SR_OVERRIDE_RENDER_PRESET_SELECTION=11 %command%", plan.LaunchOptions);
    }

    [Fact]
    public void HdrOff_WithoutNvidiaSettings_StaysEmpty_AndDoesNotApplyAtLaunch()
    {
        var plan = Service().BuildPlan(Direct(), new LinuxHdrGameProfile(), Rtx40, AllFiles);

        Assert.Empty(plan.Environment);
        Assert.False(plan.AppliesAtLaunch);
        Assert.Equal("%command%", plan.LaunchOptions);
    }

    [Fact]
    public void AnHdrMode_KeepsItsVariablesAndWrapper_AndGainsTheNvidiaOnes()
    {
        var plan = Service(gamescope: true).BuildPlan(Direct(), Profile(LinuxHdrModes.Gamescope,
            new NvidiaGameSettings { SuperResolutionRenderScale = 75 }, legacyWsi: true), Rtx40, AllFiles);

        Assert.True(plan.AppliesAtLaunch);
        Assert.Equal("1", plan.Environment["ENABLE_HDR_WSI"]);
        Assert.Equal("75", plan.Environment["DXVK_NVAPI_DRS_NGX_DLSS_SR_OVERRIDE_SCALING_RATIO"]);
        Assert.Equal("gamescope --hdr-enabled --", plan.CommandPrefix);
        Assert.Equal("DXVK_NVAPI_DRS_NGX_DLSS_SR_MODE=6 DXVK_NVAPI_DRS_NGX_DLSS_SR_OVERRIDE_SCALING_RATIO=75 DXVK_NVAPI_DRS_SETTINGS='0x10e41df5=75' "
                     + "ENABLE_HDR_WSI=1 gamescope --hdr-enabled -- %command%", plan.LaunchOptions);
    }

    [Fact]
    public void ABlockedHdrMode_LeavesTheCopyableLineToTheNvidiaPart()
    {
        var plan = Service().BuildPlan(Steam(), Profile(LinuxHdrModes.Gamescope,
            new NvidiaGameSettings { SuperResolutionPreset = 12 }), Rtx40 with { GamescopeAvailable = false }, AllFiles);

        Assert.False(plan.CanApply);
        Assert.Equal("DXVK_NVAPI_DRS_NGX_DLSS_SR_OVERRIDE_RENDER_PRESET_SELECTION=12 %command%", plan.LaunchOptions);
    }

    [Fact]
    public void SteamHandoff_PutsTheVariablesInTheCopyableLine_AndRhiAppliesNothing()
    {
        var plan = Service().BuildPlan(Steam(), Profile(LinuxHdrModes.Off, new NvidiaGameSettings { SuperResolutionPreset = 11, NewestDlss = true }),
            Rtx40, new NvidiaDlssFamilies(true, false, false));

        Assert.Equal(LaunchHandoff.Steam, plan.Handoff);
        Assert.False(plan.AppliesAtLaunch);
        Assert.True(plan.Nvidia.Shown);
        Assert.Equal("DXVK_NVAPI_DRS_NGX_DLSS_SR_OVERRIDE=1 DXVK_NVAPI_DRS_NGX_DLSS_SR_OVERRIDE_RENDER_PRESET_SELECTION=11 PROTON_ENABLE_NGX_UPDATER=1 %command%",
            plan.LaunchOptions);
    }

    [Fact]
    public void Rtx50Gating_OffersFrameGenerationOnlyOnAnRtx50WithAFrameGenerationFile()
    {
        var settings = new NvidiaGameSettings { FrameGenerationMode = NvidiaFrameGenerationModes.Fixed, FrameCount = 2 };
        var on50 = NvidiaLaunchSettings.Plan(Direct(), settings, Rtx50, AllFiles);
        var on40 = NvidiaLaunchSettings.Plan(Direct(), settings, Rtx40, AllFiles);
        var noFgFile = NvidiaLaunchSettings.Plan(Direct(), settings, Rtx50, new NvidiaDlssFamilies(true, true, false));

        Assert.True(on50.FrameGenerationOffered);
        Assert.Equal("2", on50.Environment["DXVK_NVAPI_DRS_NGX_DLSSG_MODE"]);
        Assert.False(on40.FrameGenerationOffered);
        Assert.Empty(on40.Environment);
        Assert.False(noFgFile.FrameGenerationOffered);
        Assert.Empty(noFgFile.Environment);
    }

    public static TheoryData<string> HiddenCases => new() { "no-nvidia", "native", "no-dlss", "direct-wine", "store-client", "not-probed" };

    [Theory]
    [MemberData(nameof(HiddenCases))]
    public void WhereTheSettingsCannotApply_TheyAreHidden_AndAddNothing(string why)
    {
        var game = why switch
        {
            "native" => Direct(runner: "Native Linux"),
            "direct-wine" => WineSource(),
            "store-client" => Direct(command: "xdg-open 'itch://caves/abc/launch'"),
            _ => Direct()
        };
        var host = why switch
        {
            "no-nvidia" => Host(NvidiaGpuFacts.None),
            "not-probed" => LinuxHdrHostSnapshot.NotProbed,
            _ => Rtx50
        };
        var families = why == "no-dlss" ? new NvidiaDlssFamilies(false, false, false) : AllFiles;
        var expected = why switch
        {
            "no-nvidia" => NvidiaPlanReason.NoNvidiaGpu,
            "native" => NvidiaPlanReason.NativeLinux,
            "no-dlss" => NvidiaPlanReason.NoDlss,
            "direct-wine" => NvidiaPlanReason.DirectWineLaunch,
            "store-client" => NvidiaPlanReason.LauncherTakesNoVariables,
            _ => NvidiaPlanReason.HostNotProbed
        };

        var plan = Service().BuildPlan(game, Profile(LinuxHdrModes.Off, new NvidiaGameSettings { SuperResolutionPreset = 11, NewestDlss = true }), host, families);

        Assert.Equal(expected, plan.Nvidia.Reason);
        Assert.False(plan.Nvidia.Shown);
        Assert.Empty(plan.Environment);
        Assert.False(plan.AppliesAtLaunch);
        Assert.Equal("%command%", plan.LaunchOptions);
    }

    [Fact]
    public void WhileTheGamesFilesAreNotKnown_TheSettingsAreNotOffered_ButASavedChoiceStillApplies()
    {
        var plan = Service().BuildPlan(Direct(), Profile(LinuxHdrModes.Off, new NvidiaGameSettings { SuperResolutionPreset = 11 }), Rtx40, dlss: null);

        Assert.Equal(NvidiaPlanReason.FilesNotKnownYet, plan.Nvidia.Reason);
        Assert.False(plan.Nvidia.Shown);
        Assert.True(plan.AppliesAtLaunch);
        Assert.Equal("11", plan.Environment["DXVK_NVAPI_DRS_NGX_DLSS_SR_OVERRIDE_RENDER_PRESET_SELECTION"]);
    }

    [Fact]
    public void TheHostProbe_CarriesTheGpuFromItsSeam()
    {
        var host = Service(gpu: new NvidiaGpuFacts(true, true, "NVIDIA GeForce RTX 5080")).ProbeHost();

        Assert.True(host.Gpu.Present);
        Assert.True(host.Gpu.Rtx50);
        Assert.False(LinuxHdrHostSnapshot.NotProbed.Gpu.Present);
    }

    // ---- the GPU probe (read-only /proc and /sys, scratch trees here) ---------------------------------------------

    [Theory]
    [InlineData("NVIDIA GeForce RTX 5090", true)]
    [InlineData("NVIDIA GeForce RTX 5070 Ti Laptop GPU", true)]
    [InlineData("NVIDIA RTX PRO 6000 Blackwell Workstation Edition", true)]
    [InlineData("NVIDIA GeForce RTX 4090", false)]
    [InlineData("NVIDIA GeForce RTX 3050", false)]
    [InlineData("NVIDIA GeForce GTX 1050 Ti", false)]
    public void GpuProbe_ReadsTheDriversModelName(string model, bool rtx50)
    {
        var proc = Path.Combine(_root, "proc");
        var gpu = Directory.CreateDirectory(Path.Combine(proc, "driver", "nvidia", "gpus", "0000:65:00.0")).FullName;
        File.WriteAllText(Path.Combine(gpu, "information"), $"Model: \t\t {model}\nIRQ:   \t\t 70\n");

        var facts = NvidiaGpuProbe.Read(proc, Path.Combine(_root, "sys"));

        Assert.Equal((true, rtx50, model), (facts.Present, facts.Rtx50, facts.Model));
    }

    [Theory]
    [InlineData("0x10de", "0x030000", true)]
    [InlineData("0x10de", "0x040300", false)] // the card's audio function only
    [InlineData("0x1002", "0x030000", false)] // AMD
    public void GpuProbe_WithNvidiasDriverButNoModel_FallsBackToAPciDisplayDevice_WithoutRtx50(string vendor, string pciClass, bool present)
    {
        var device = Directory.CreateDirectory(Path.Combine(_root, "sys", "bus", "pci", "devices", "0000:65:00.0")).FullName;
        File.WriteAllText(Path.Combine(device, "vendor"), vendor + "\n");
        File.WriteAllText(Path.Combine(device, "class"), pciClass + "\n");
        var driver = Directory.CreateDirectory(Path.Combine(_root, "proc", "driver", "nvidia")).FullName;
        File.WriteAllText(Path.Combine(driver, "version"), "NVRM version: NVIDIA UNIX Open Kernel Module for x86_64  610.57.04\n");

        var facts = NvidiaGpuProbe.Read(Path.Combine(_root, "proc"), Path.Combine(_root, "sys"));

        Assert.Equal(present, facts.Present);
        Assert.False(facts.Rtx50);
    }

    [Fact]
    public void GpuProbe_WithoutTheNvidiaDriver_ANouveauOrNvkCardIsNone()
    {
        // An NVIDIA display device but no /proc/driver/nvidia (nouveau/NVK): no NGX/DLSS, so the settings stay hidden.
        var device = Directory.CreateDirectory(Path.Combine(_root, "sys", "bus", "pci", "devices", "0000:01:00.0")).FullName;
        File.WriteAllText(Path.Combine(device, "vendor"), "0x10de\n");
        File.WriteAllText(Path.Combine(device, "class"), "0x030000\n");

        var facts = NvidiaGpuProbe.Read(Path.Combine(_root, "proc"), Path.Combine(_root, "sys"));

        Assert.False(facts.Present);
        Assert.False(NvidiaLaunchSettings.Plan(Direct(), new NvidiaGameSettings { SuperResolutionPreset = 11 }, Host(facts), AllFiles).Shown);
    }

    // ---- stored with the per-game launch profile ----------------------------------------------------------------

    [Fact]
    public void Profile_RoundTripsTheNvidiaSettings_InTheLibraryFile()
    {
        var store = new LinuxGameLibraryStore(Path.Combine(_root, "game-library.json"));
        var game = Direct();
        var state = new LinuxGameLibraryState();
        var settings = new NvidiaGameSettings
        {
            SuperResolutionPreset = 11, RayReconstructionPreset = DlssPresetCatalogue.Recommended, FrameGenerationPreset = 3,
            SuperResolutionRenderScale = 67, RayReconstructionRenderScale = 88, FrameGenerationMode = "Dynamic",
            DynamicMaxFrameCount = 4, DynamicTargetFrameRate = 138, NewestDlss = true
        };
        state.HdrProfiles[LinuxGameLibraryStore.GameKey(game)] = new LinuxHdrGameProfile { Nvidia = settings };
        store.Save(state);

        var loaded = store.Load().HdrProfiles[LinuxGameLibraryStore.GameKey(game)].Nvidia!;

        Assert.Equal(JsonSerializer.Serialize(settings), JsonSerializer.Serialize(loaded));
    }

    [Fact]
    public void AProfileWithNothingNvidiaChosen_KeepsTheShapeOlderBuildsWrote()
    {
        var older = """{"Mode":"Gamescope","EnableLegacyWsi":true}""";
        var profile = JsonSerializer.Deserialize<LinuxHdrGameProfile>(older)!.Normalize();
        Assert.Null(profile.Nvidia);
        Assert.Equal(older, JsonSerializer.Serialize(profile));

        profile.Nvidia = new NvidiaGameSettings(); // all Default
        Assert.Equal(older, JsonSerializer.Serialize(profile.Normalize()));
        Assert.True(new LinuxHdrGameProfile { Nvidia = new NvidiaGameSettings() }.IsDefault());
        Assert.False(new LinuxHdrGameProfile { Nvidia = new NvidiaGameSettings { NewestDlss = true } }.IsDefault());
        Assert.Equal("""{"Mode":"Off","EnableLegacyWsi":false,"Nvidia":{"SuperResolutionPreset":11}}""",
            JsonSerializer.Serialize(new LinuxHdrGameProfile { Nvidia = new NvidiaGameSettings { SuperResolutionPreset = 11 } }.Normalize()));
    }

    [Fact]
    public void AMovedGame_KeepsItsNvidiaChoice()
    {
        // The choice lives in the game's launch profile, which moves with every other library entry (key adoption).
        var game = Direct();
        var state = new LinuxGameLibraryState();
        state.HdrProfiles[LinuxGameLibraryStore.LegacyGameKey(game)] = new LinuxHdrGameProfile { Nvidia = new NvidiaGameSettings { SuperResolutionPreset = 13 } };

        Assert.True(state.AdoptOlderKeys(game));

        Assert.Equal(13u, state.HdrProfiles[LinuxGameLibraryStore.GameKey(game)].Nvidia!.SuperResolutionPreset);
    }

    // ---- the manifest's preset lists --------------------------------------------------------------------------------

    [Fact]
    public void Fallback_IsUpstreamsBuiltInListsMergedWithTheSyncedManifest_IncludingFgPresetC()
    {
        Assert.Equal(["Default", "E - CNN", "F - CNN", "J - TF1", "K - TF1", "L - TF2", "M - TF2", "NVIDIA Recommended"],
            DlssPresetCatalogue.Fallback.SuperResolution.Select(p => p.Name));
        Assert.Equal(["Default", "D - TF1", "E - TF1", "F - TF2", "NVIDIA Recommended"], DlssPresetCatalogue.Fallback.RayReconstruction.Select(p => p.Name));
        Assert.Equal([0u, 1u, 2u, 3u, DlssPresetCatalogue.FrameGenerationRecommended], DlssPresetCatalogue.Fallback.FrameGeneration.Select(p => p.Value));
    }

    [Fact]
    public void Merge_AddsAlphabeticallyBeforeRecommended_KeepsAnExistingName_AndDisabledRemovesAllButDefault()
    {
        var merged = DlssPresetCatalogue.Merge(DlssPresetCatalogue.BuiltIn, new UpstreamDlssPresetsDefinition(
            [new("N - TF2", 14, false), new("K - TF1", 99, false), new("J - TF1", 0, true), new("Default", 0, true), new("Z", 0x1000000, false)],
            [], []));

        Assert.Equal(["Default", "K - TF1", "L - TF2", "M - TF2", "N - TF2", "NVIDIA Recommended"], merged.SuperResolution.Select(p => p.Name));
        Assert.Equal(11u, merged.SuperResolution.Single(p => p.Name == "K - TF1").Value);
    }

    [Fact]
    public async Task TheManifestsDlssPresets_AreRead_AndTheScanHandsThemToTheCatalogue()
    {
        var json = """{"version":1,"dlssPresets":{"sr":[{"name":"F - CNN","value":6},{"name":"J - TF1","disabled":true}],"fg":[{"name":"C","value":3}]}}""";
        using var http = new HttpClient(new FixedHandler(json));
        var manifest = new UpstreamRhiManifestService(http, "https://raw.githubusercontent.com/rhi-tests/rhi/main/manifest.json", null,
            Path.Combine(_root, "manifest-cache.json"));

        var policy = await manifest.FetchLibraryPolicyAsync();

        Assert.NotNull(policy.DlssPresets);
        Assert.Equal(["F - CNN", "J - TF1"], policy.DlssPresets!.SuperResolution.Select(e => e.Name));
        Assert.True(policy.DlssPresets.SuperResolution[1].Disabled);
        Assert.Equal(3, Assert.Single(policy.DlssPresets.FrameGeneration).Value);
        // A source of its own: the app's shared lists (read by view-model tests running in parallel) stay as they are.
        var source = new DlssPresetSource();
        var shared = DlssPresetCatalogue.Current;
        source.Observe(policy);
        Assert.Same(shared, DlssPresetCatalogue.Current);
        Assert.DoesNotContain(source.Current.SuperResolution, p => p.Name == "J - TF1");
        Assert.Contains(source.Current.SuperResolution, p => p.Name == "F - CNN");
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private static LinuxHdrHostSnapshot Host(NvidiaGpuFacts gpu)
        => new(true, "KDE", true, "610.57.04", false) { Gpu = gpu };

    private static LinuxHdrGameProfile Profile(string mode, NvidiaGameSettings nvidia, bool legacyWsi = false)
        => new() { Mode = mode, EnableLegacyWsi = legacyWsi, Nvidia = nvidia };

    private static LinuxHdrLaunchProfileService Service(bool gamescope = true, NvidiaGpuFacts? gpu = null)
        => new(
            environment: name => name switch
            {
                "XDG_SESSION_TYPE" => "wayland",
                "WAYLAND_DISPLAY" => "wayland-0",
                "XDG_CURRENT_DESKTOP" => "KDE",
                _ => null
            },
            commandExists: command => command == "gamescope" && gamescope,
            nvidiaVersion: () => "610.57.04",
            nvidiaGpu: () => gpu ?? new NvidiaGpuFacts(true, false));

    private DetectedGame Direct(string runner = "Proton", string command = "umu-run /games/quest/Quest.exe")
        => new()
        {
            Name = "Quest", Source = "Manual", InstallPath = Path.Combine(_root, "Quest"), Runner = runner,
            CompatibilityTool = "GE-Proton10-25", CompatibilityLayerType = "proton", LaunchCommand = command
        };

    private DetectedGame Steam()
        => new()
        {
            Name = "Steam Quest", Source = "Steam", InstallPath = Path.Combine(_root, "Steam Quest"), Runner = "Proton",
            CompatibilityTool = "proton_experimental", CompatibilityLayerType = "proton", LaunchCommand = "steam -applaunch 1234", SteamAppId = 1234
        };

    private DetectedGame WineSource()
        => new()
        {
            Name = "Prefix Quest", Source = "Wine", InstallPath = Path.Combine(_root, "Prefix Quest"), Runner = "Proton",
            CompatibilityTool = "GE-Proton10-25", CompatibilityLayerType = "proton",
            LaunchCommand = "WINEPREFIX='/p' '/proton/files/bin/wine' '/p/drive_c/Game/game.exe'"
        };

    private sealed class FixedHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
