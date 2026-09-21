using System.Text.Json;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Launch;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

public sealed class HdrLinuxIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-hdr-test-" + Guid.NewGuid().ToString("N"));

    public HdrLinuxIntegrationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void OffProfile_DoesNotTouchRunnerEnvironment()
    {
        var service = Service(wayland: true, gamescope: true);
        var plan = service.BuildPlan(Game("GE-Proton11-3"), new LinuxHdrGameProfile());

        Assert.False(plan.Enabled);
        Assert.True(plan.CanApply);
        Assert.Empty(plan.Environment);
        Assert.Null(plan.CommandPrefix);
        Assert.Equal("%command%", plan.LaunchOptions);
    }

    [Fact]
    public void GeProton_NativeWayland_UsesGeHdrContract()
    {
        var service = Service(wayland: true, gamescope: false);
        var plan = service.BuildPlan(Game("GE-Proton11-3"), Profile(LinuxHdrModes.NativeWayland));

        Assert.True(plan.CanApply);
        Assert.Equal("1", plan.Environment["PROTON_ENABLE_HDR"]);
        Assert.Equal("1", plan.Environment["PROTON_ENABLE_WAYLAND"]);
        Assert.DoesNotContain("DXVK_HDR", plan.Environment.Keys);
        Assert.Null(plan.CommandPrefix);
    }

    [Fact]
    public void ProtonCachy_NativeWayland_DoesNotInventRemovedProtonHdrAlias()
    {
        var service = Service(wayland: true, gamescope: false);
        var plan = service.BuildPlan(Game("proton-cachyos-11.0"), Profile(LinuxHdrModes.NativeWayland));

        Assert.True(plan.CanApply);
        Assert.Equal("1", plan.Environment["DXVK_HDR"]);
        Assert.Equal("1", plan.Environment["PROTON_ENABLE_WAYLAND"]);
        Assert.DoesNotContain("PROTON_ENABLE_HDR", plan.Environment.Keys);
    }

    [Fact]
    public void NativeWayland_NonWaylandSession_FailsClosed()
    {
        var service = Service(wayland: false, gamescope: true);
        var plan = service.BuildPlan(Game("GE-Proton11-3"), Profile(LinuxHdrModes.NativeWayland));

        Assert.True(plan.Enabled);
        Assert.False(plan.CanApply);
        Assert.Contains("requires a Wayland", plan.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NativeLinux_NativeWayland_IsNotGenericForced()
    {
        var service = Service(wayland: true, gamescope: true);
        var game = Game(null);
        game.Runner = "Native Linux";
        game.CompatibilityLayerType = null;
        var plan = service.BuildPlan(game, Profile(LinuxHdrModes.NativeWayland));

        Assert.False(plan.CanApply);
        Assert.Empty(plan.Environment);
        Assert.Contains("application/toolkit-specific", plan.Summary, StringComparison.OrdinalIgnoreCase);

        var generic = Game("Proton 11.0");
        var genericPlan = service.BuildPlan(generic, Profile(LinuxHdrModes.NativeWayland));
        Assert.False(genericPlan.CanApply);
        Assert.Contains("will not guess", genericPlan.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GamescopeMode_MissingExecutable_FailsClosedWithoutInstallingAnything()
    {
        var service = Service(wayland: true, gamescope: false);
        var plan = service.BuildPlan(Game("GE-Proton11-3"), Profile(LinuxHdrModes.Gamescope));

        Assert.False(plan.CanApply);
        Assert.Contains("not found", plan.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Null(plan.CommandPrefix);
    }

    [Fact]
    public void GeProton_Gamescope_UsesWrapperOnlyWithoutRedundantRunnerHdrVariables()
    {
        var service = Service(wayland: true, gamescope: true);
        var plan = service.BuildPlan(Game("GE-Proton11-3"), Profile(LinuxHdrModes.Gamescope));

        Assert.True(plan.CanApply);
        Assert.Equal("gamescope --hdr-enabled --", plan.CommandPrefix);
        Assert.Empty(plan.Environment);
        Assert.Equal("gamescope --hdr-enabled -- %command%", plan.LaunchOptions);
    }

    [Fact]
    public void NativeLinux_Gamescope_DoesNotInjectTranslationLayerVariables()
    {
        var service = Service(wayland: true, gamescope: true);
        var game = Game(null);
        game.Runner = "Native Linux";
        game.CompatibilityLayerType = null;
        var plan = service.BuildPlan(game, Profile(LinuxHdrModes.Gamescope));

        Assert.True(plan.CanApply);
        Assert.Empty(plan.Environment);
        Assert.Equal("gamescope --hdr-enabled --", plan.CommandPrefix);
    }

    [Fact]
    public void LegacyWsi_IsExplicitOptInOnly()
    {
        var service = Service(wayland: true, gamescope: true, nvidia: "580.95.05");
        var normal = service.BuildPlan(Game("GE-Proton11-3"), Profile(LinuxHdrModes.Gamescope));
        var legacy = service.BuildPlan(Game("GE-Proton11-3"), Profile(LinuxHdrModes.Gamescope, legacy: true));

        Assert.DoesNotContain("ENABLE_HDR_WSI", normal.Environment.Keys);
        Assert.Equal("1", legacy.Environment["ENABLE_HDR_WSI"]);
        Assert.Contains("older than 595", normal.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ModernNvidia_DoesNotRecommendLegacyWsi()
    {
        var service = Service(wayland: true, gamescope: true, nvidia: "610.57.04");
        var host = service.ProbeHost();

        Assert.False(host.LegacyWsiLikelyRequired);
        Assert.Equal("610.57.04", host.NvidiaDriverVersion);
    }

    [Fact]
    public void LaunchOptions_AreDeterministicAndPasteable()
    {
        var options = LinuxHdrLaunchProfileService.BuildLaunchOptions(
            new Dictionary<string, string> { ["Z_TEST"] = "2", ["A_TEST"] = "1" },
            "gamescope --hdr-enabled --");

        Assert.Equal("A_TEST=1 Z_TEST=2 gamescope --hdr-enabled -- %command%", options);
    }

    [Fact]
    public void LinuxLauncher_PreservesDirectWineCommandInsideWrapper_AndStoreLaunchersAreMediated()
    {
        var command = LinuxGameLauncher.BuildShellCommand("WINEPREFIX='/tmp/My Prefix' wine '/tmp/game.exe'", "gamescope --hdr-enabled --");
        Assert.StartsWith("exec gamescope --hdr-enabled -- /bin/sh -lc ", command, StringComparison.Ordinal);
        Assert.Contains("WINEPREFIX=", command, StringComparison.Ordinal);
        Assert.Contains("/tmp/My Prefix", command, StringComparison.Ordinal);
        Assert.Contains("/tmp/game.exe", command, StringComparison.Ordinal);

        var steam = Game("GE-Proton11-3");
        steam.Source = "Steam";
        Assert.True(LinuxHdrLaunchProfileService.IsLauncherMediated(steam));

        var manual = Game(null);
        manual.Source = "Manual";
        manual.LaunchCommand = "WINEPREFIX='/tmp/pfx' wine '/tmp/game.exe'";
        Assert.False(LinuxHdrLaunchProfileService.IsLauncherMediated(manual));
    }

    [Fact]
    public void GameLibrary_PersistsHdrProfileSeparatelyFromGlobalSettings()
    {
        var path = Path.Combine(_root, "game-library.json");
        var store = new LinuxGameLibraryStore(path);
        var game = Game("GE-Proton11-3");
        var state = new LinuxGameLibraryState();
        state.HdrProfiles[LinuxGameLibraryStore.GameKey(game)] = Profile(LinuxHdrModes.NativeWayland, legacy: true);
        store.Save(state);

        var loaded = store.Load();
        var profile = loaded.HdrProfiles[LinuxGameLibraryStore.GameKey(game)];
        Assert.Equal(LinuxHdrModes.NativeWayland, profile.Mode);
        Assert.True(profile.EnableLegacyWsi);
    }

    [Fact]
    public void DeploymentStore_IgnoresRetiredUnknownComponentWithoutLosingKnownRecords()
    {
        var path = Path.Combine(_root, "deployments.json");
        var game = Game("GE-Proton11-3");
        var key = LinuxDeploymentRecordStore.GameKey(game);
        var payload = new object[]
        {
            new Dictionary<string, object?>
            {
                ["GameKey"] = key, ["GameName"] = "Test", ["Component"] = "ReShade",
                ["BinaryDirectory"] = _root, ["Files"] = Array.Empty<object>(),
                ["AdditionalWineOverrides"] = Array.Empty<string>(),
                ["Metadata"] = new Dictionary<string, string>(),
            },
            new Dictionary<string, object?>
            {
                ["GameKey"] = key, ["GameName"] = "Test", ["Component"] = "RetiredTranslationLayer",
                ["BinaryDirectory"] = _root, ["Files"] = Array.Empty<object>(),
                ["AdditionalWineOverrides"] = Array.Empty<string>(),
                ["Metadata"] = new Dictionary<string, string>(),
            },
            new Dictionary<string, object?>
            {
                ["GameKey"] = key, ["GameName"] = "Test", ["Component"] = 11,
                ["BinaryDirectory"] = _root, ["Files"] = Array.Empty<object>(),
                ["AdditionalWineOverrides"] = Array.Empty<string>(),
                ["Metadata"] = new Dictionary<string, string>(),
            },
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload));

        var store = new LinuxDeploymentRecordStore(path);
        var records = store.FindAll(game);
        var record = Assert.Single(records);
        Assert.Equal(ManagedComponentKind.ReShade, record.Component);
    }

    private static LinuxHdrGameProfile Profile(string mode, bool legacy = false)
        => new() { Mode = mode, EnableLegacyWsi = legacy };

    private LinuxHdrLaunchProfileService Service(bool wayland, bool gamescope, string? nvidia = null)
        => new(
            environment: name => name switch
            {
                "XDG_SESSION_TYPE" => wayland ? "wayland" : "x11",
                "WAYLAND_DISPLAY" => wayland ? "wayland-0" : null,
                "XDG_CURRENT_DESKTOP" => "KDE",
                _ => null
            },
            commandExists: command => command == "gamescope" && gamescope,
            nvidiaVersion: () => nvidia);

    private DetectedGame Game(string? compatibilityTool) => new()
    {
        Name = "HDR Test",
        InstallPath = _root,
        Source = "Manual",
        Runner = compatibilityTool is null ? "Wine" : "Proton",
        CompatibilityTool = compatibilityTool,
        CompatibilityLayerType = compatibilityTool is null ? "wine" : "proton",
        LaunchCommand = "steam -applaunch 123"
    };

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
