using RHI.Core.Models;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Launch;

namespace RHI.Core.Tests;

/// <summary>
/// #250: the launch plan's legacy HDR WSI rule. <c>ENABLE_HDR_WSI=1</c> goes into a plan only when the profile ticks it AND
/// the probed host needs the layer (NVIDIA driver older than 595). Everything RHI shows for pasting (Steam's launch
/// options, the line copied for the app that installed an Xbox game) and the environment of a game RHI starts itself come
/// from the plan, so a tick saved earlier is left out of all of them on a host that does not need it (the owner's
/// Borderlands 3 crashed on every load with it in Steam's launch options) — and the saved profile is not changed.
/// </summary>
public sealed class LegacyHdrWsiPlanTests
{
    private const string Variable = "ENABLE_HDR_WSI";

    public static TheoryData<string?, bool, string, string> Matrix()
    {
        var data = new TheoryData<string?, bool, string, string>();
        // Older than 595, the first version that no longer needs it, the owner's driver, unreadable, not a version.
        foreach (var driver in new string?[] { "580.95.05", "594.99", "595.44.02", "615.78.08", null, "unknown" })
            foreach (var tick in new[] { true, false })
                foreach (var mode in new[] { LinuxHdrModes.NativeWayland, LinuxHdrModes.Gamescope })
                    foreach (var launch in new[] { "direct", "steam", "xbox" })
                        data.Add(driver, tick, mode, launch);
        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void TheVariable_IsUsedOnlyWhereTheHostNeedsIt_InEveryTextAndDirectStart(string? driver, bool tick, string mode, string launch)
    {
        var service = Service(driver);
        var host = service.ProbeHost();
        var needed = driver is "580.95.05" or "594.99";
        Assert.Equal(needed, host.LegacyWsiLikelyRequired);
        var game = Game(launch);
        var profile = new LinuxHdrGameProfile { Mode = mode, EnableLegacyWsi = tick };

        var plan = service.BuildPlan(game, profile, host);

        Assert.True(plan.Enabled && plan.CanApply, $"{mode} should apply on this host ({plan.Reason})");
        Assert.Equal(launch switch { "steam" => LaunchHandoff.Steam, "xbox" => LaunchHandoff.Xbox, _ => LaunchHandoff.None }, plan.Handoff);
        var used = needed && tick;
        // The direct-start environment (what RHI applies itself) and the pasted line (Steam / the installing app).
        Assert.Equal(used, plan.Environment.ContainsKey(Variable));
        Assert.Equal(used, plan.LaunchOptions.Contains(Variable + "=1", StringComparison.Ordinal));
        Assert.Equal(launch == "direct", plan.AppliesAtLaunch);
        // The saved choice itself is never changed by planning.
        Assert.Equal(tick, profile.EnableLegacyWsi);
    }

    [Theory]
    [InlineData("580.95.05", true)]
    [InlineData("615.78.08", false)]
    [InlineData(null, false)]
    public void ABlockedProfile_FollowsTheSameRule(string? driver, bool used)
    {
        // Native Wayland on X11: blocked; a hand-off still starts and its plan's environment is what the steps list.
        var service = Service(driver, wayland: false);
        var plan = service.BuildPlan(Game("steam"), new LinuxHdrGameProfile { Mode = LinuxHdrModes.NativeWayland, EnableLegacyWsi = true }, service.ProbeHost());

        Assert.False(plan.CanApply);
        Assert.Equal(used, plan.Environment.ContainsKey(Variable));
        Assert.DoesNotContain(Variable, plan.LaunchOptions, StringComparison.Ordinal);
    }

    [Fact]
    public void BeforeTheHostIsProbed_ASavedTickIsNotUsed()
    {
        var plan = Service("580.95.05").BuildPlan(Game("direct"),
            new LinuxHdrGameProfile { Mode = LinuxHdrModes.Gamescope, EnableLegacyWsi = true }, LinuxHdrHostSnapshot.NotProbed);

        Assert.Equal(LinuxHdrPlanReason.HostNotProbed, plan.Reason);
        Assert.DoesNotContain(Variable, plan.Environment.Keys);
        Assert.DoesNotContain(Variable, plan.LaunchOptions, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("580.95.05")]
    [InlineData("615.78.08")]
    public void HdrOff_StaysEmpty_WithASavedTick(string driver)
    {
        // HDR Off was already right (empty environment): pinned, unchanged.
        var service = Service(driver);
        var plan = service.BuildPlan(Game("direct"), new LinuxHdrGameProfile { Mode = LinuxHdrModes.Off, EnableLegacyWsi = true }, service.ProbeHost());

        Assert.False(plan.Enabled);
        Assert.Empty(plan.Environment);
        Assert.Equal("%command%", plan.LaunchOptions);
    }

    [Fact]
    public void ADirectStart_OnTheOwnersHost_WithMetroExodussSavedProfile_GetsNoVariable()
    {
        // The owner's host: NVIDIA 615.78.08 on KDE Wayland; his saved profile has EnableLegacyWsi = true.
        var service = Service("615.78.08");
        var profile = new LinuxHdrGameProfile { Mode = LinuxHdrModes.Gamescope, EnableLegacyWsi = true };

        var direct = service.BuildPlan(Game("direct"), profile);
        var steam = service.BuildPlan(Game("steam"), profile);

        Assert.True(direct.AppliesAtLaunch);
        Assert.Equal("gamescope --hdr-enabled -- %command%", direct.LaunchOptions);
        Assert.Equal("gamescope --hdr-enabled -- %command%", steam.LaunchOptions);
        Assert.Empty(direct.Environment);
        Assert.True(profile.EnableLegacyWsi);
        Assert.False(profile.IsDefault());
    }

    internal static LinuxHdrLaunchProfileService Service(string? driver, bool wayland = true)
        => new(
            environment: name => name switch
            {
                "XDG_SESSION_TYPE" => wayland ? "wayland" : "x11",
                "WAYLAND_DISPLAY" => wayland ? "wayland-0" : null,
                "XDG_CURRENT_DESKTOP" => "KDE",
                _ => null
            },
            commandExists: command => command == "gamescope",
            nvidiaVersion: () => driver,
            nvidiaGpu: () => NvidiaGpuFacts.None);

    internal static DetectedGame Game(string launch)
    {
        var game = new DetectedGame
        {
            Name = "Wsi Quest", InstallPath = "/games/Wsi Quest", Runner = "Proton", CompatibilityTool = "GE-Proton11-3",
            CompatibilityLayerType = "proton"
        };
        switch (launch)
        {
            case "steam":
                game.Source = "Steam";
                game.SteamAppId = 1449560;
                game.LaunchCommand = "steam -applaunch 1449560";
                break;
            case "xbox":
                // Started by the app that installed it (its own start script).
                game.Source = "Xbox";
                game.LaunchCommand = "'/apps/installer/start.sh'";
                game.XboxPackage = new XboxPackageInfo("9WSI", "Installer", game.LaunchCommand, null);
                break;
            default:
                game.Source = "Manual";
                game.LaunchCommand = "umu-run '/games/Wsi Quest/Game.exe'";
                break;
        }
        return game;
    }
}
