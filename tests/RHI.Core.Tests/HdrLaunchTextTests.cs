using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Launch;

namespace RHI.Core.Tests;

/// <summary>
/// HDR launch profile wording (Refs #146: no English in the PT-BR UI) and store hand-offs (Refs #46: the profile is
/// never wrapped around a store client and reported as applied; the user gets that launcher's steps instead).
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class HdrLaunchTextTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-hdr-text-" + Guid.NewGuid().ToString("N"));

    public HdrLaunchTextTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void HostLine_IsPortugueseInThePtBrUi()
    {
        var plan = Service(gamescope: true, nvidia: "580.95.05").BuildPlan(Game("Manual", "wine game.exe"), Profile(LinuxHdrModes.Gamescope));

        var pt = InLanguage("pt-BR", () => HdrLaunchText.Hint(plan, null));
        var en = InLanguage("en-US", () => HdrLaunchText.Hint(plan, null));

        Assert.Contains("gamescope disponível", pt, StringComparison.Ordinal);
        Assert.DoesNotContain("gamescope available", pt, StringComparison.Ordinal);
        Assert.Contains("driver NVIDIA 580.95.05", pt, StringComparison.Ordinal);
        Assert.Contains("gamescope available", en, StringComparison.Ordinal);
        Assert.Contains("NVIDIA driver 580.95.05", en, StringComparison.Ordinal);
    }

    public static TheoryData<string, bool, bool, string> EveryReason() => new()
    {
        // mode, wayland, gamescope, runner/tool
        { LinuxHdrModes.Off, true, true, "GE-Proton11-3" },
        { LinuxHdrModes.NativeWayland, true, true, "GE-Proton11-3" },
        { LinuxHdrModes.NativeWayland, true, true, "proton-cachyos-11.0" },
        { LinuxHdrModes.NativeWayland, true, true, "Proton 11.0" },
        { LinuxHdrModes.NativeWayland, false, true, "GE-Proton11-3" },
        { LinuxHdrModes.NativeWayland, true, true, "Native Linux" },
        { LinuxHdrModes.NativeWayland, true, true, "direct-wine" },
        { LinuxHdrModes.Gamescope, true, true, "GE-Proton11-3" },
        { LinuxHdrModes.Gamescope, true, true, "Native Linux" },
        { LinuxHdrModes.Gamescope, false, false, "GE-Proton11-3" },
    };

    [Theory]
    [MemberData(nameof(EveryReason))]
    public void EveryPlan_ReadsInTheUiLanguage(string mode, bool wayland, bool gamescope, string tool)
    {
        var game = Game("Manual", "wine game.exe");
        if (tool == "Native Linux") game.Runner = "Native Linux";
        else if (tool == "direct-wine") { game.Source = "Wine"; game.CompatibilityTool = "GE-Proton10-23"; }
        else game.CompatibilityTool = tool;
        var plan = Service(gamescope, wayland: wayland, nvidia: "570.1").BuildPlan(game, Profile(mode));

        var pt = InLanguage("pt-BR", () => HdrLaunchText.Hint(plan, game));
        var en = InLanguage("en-US", () => HdrLaunchText.Hint(plan, game));

        var english = UiLocalizationService.EnglishMarkersIn(pt).Where(w => !w.Equals("no", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.True(english.Length == 0, $"English left ({string.Join(", ", english)}) for {plan.Reason}: {pt}");
        Assert.NotEqual(pt, en);
        Assert.Contains("Host:", en, StringComparison.Ordinal);
    }

    // w2/vmsplit re-review, nit 6: the stand-in host used before the start-up probe lands is never worded as "gamescope
    // not found" or a non-Wayland session; the profile is not judged until the real host is known.
    [Theory]
    [InlineData(LinuxHdrModes.Gamescope)]
    [InlineData(LinuxHdrModes.NativeWayland)]
    public void NotProbedHost_IsNeverWordedAsAMissingGamescopeOrSession(string mode)
    {
        var game = Game("Wine", "WINEPREFIX=/tmp/pfx wine Game.exe");
        var plan = Service(gamescope: true).BuildPlan(game, Profile(mode), LinuxHdrHostSnapshot.NotProbed);

        Assert.Equal(LinuxHdrPlanReason.HostNotProbed, plan.Reason);
        var en = InLanguage("en-US", () => HdrLaunchText.Hint(plan, game));
        var pt = InLanguage("pt-BR", () => HdrLaunchText.Hint(plan, game));
        foreach (var wrong in new[] { "Hdr.GamescopeMissing", "Hdr.HostGamescopeNotFound", "Hdr.HostNotWayland", "Hdr.WaylandSessionRequired" })
        {
            Assert.DoesNotContain(InLanguage("en-US", () => UiLocalizationService.Text(wrong)), en, StringComparison.Ordinal);
            Assert.DoesNotContain(InLanguage("pt-BR", () => UiLocalizationService.Text(wrong)), pt, StringComparison.Ordinal);
        }
        Assert.Contains(InLanguage("en-US", () => UiLocalizationService.Text("Hdr.HostNotChecked")), en, StringComparison.Ordinal);
        Assert.Contains(InLanguage("pt-BR", () => UiLocalizationService.Text("Hdr.HostNotProbedYet")), pt, StringComparison.Ordinal);
        // Off stays Off whatever the host.
        Assert.Equal(LinuxHdrPlanReason.Off, Service(gamescope: true).BuildPlan(game, Profile(LinuxHdrModes.Off), LinuxHdrHostSnapshot.NotProbed).Reason);
    }

    [Fact]
    public void EveryReasonCode_HasWording()
    {
        foreach (var reason in Enum.GetValues<LinuxHdrPlanReason>())
        {
            var plan = LinuxHdrLaunchPlan.NoGame with { Reason = reason };
            Assert.False(string.IsNullOrWhiteSpace(InLanguage("pt-BR", () => HdrLaunchText.Describe(plan))));
        }
    }

    [Fact]
    public void HeroicEpicGame_IsAHandoff_LaunchesUnwrapped_AndNamesHeroicsFields()
    {
        // DET-07: Source "Heroic/Epic" with an xdg-open heroic:// command used to be a direct launch for HDR.
        var game = Game("Heroic/Epic", "xdg-open 'heroic://launch?appName=Fortnite&runner=legendary'");
        var plan = Service(gamescope: true).BuildPlan(game, Profile(LinuxHdrModes.Gamescope, legacy: true));

        Assert.Equal(LaunchHandoff.Heroic, plan.Handoff);
        Assert.False(plan.AppliesAtLaunch);
        Assert.Null(HdrLaunchText.LaunchRefusal(plan, launcherAcceptsOptions: true));
        var notice = InLanguage("en-US", () => HdrLaunchText.HandoffNotice(plan, game));
        Assert.NotNull(notice);
        Assert.Contains("In Heroic", notice, StringComparison.Ordinal);
        Assert.Contains("environment variables ENABLE_HDR_WSI=1", notice, StringComparison.Ordinal);
        Assert.Contains("wrapper command gamescope --hdr-enabled --", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("%command%", notice, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Steam", "steam -applaunch 1234", "Launch Options", "gamescope --hdr-enabled -- %command%")]
    [InlineData("Lutris", "lutris 'lutris:rungame/game'", "System options", "command prefix gamescope --hdr-enabled --")]
    [InlineData("Manual", "flatpak run com.usebottles.bottles -b Games -p Game", "that launcher", "wrapper command gamescope --hdr-enabled --")]
    public void EachLauncher_GetsItsOwnSteps(string source, string command, string where, string what)
    {
        var game = Game(source, command);
        var plan = Service(gamescope: true).BuildPlan(game, Profile(LinuxHdrModes.Gamescope));

        var en = InLanguage("en-US", () => HdrLaunchText.HandoffNotice(plan, game));
        var pt = InLanguage("pt-BR", () => HdrLaunchText.HandoffNotice(plan, game));

        Assert.Contains(where, en, StringComparison.Ordinal);
        Assert.Contains(what, en, StringComparison.Ordinal);
        Assert.DoesNotContain(UiLocalizationService.EnglishMarkersIn(pt), w => !w.Equals("no", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Wine", "WINEPREFIX='/p' wine '/p/drive_c/Ubisoft/UbisoftConnect.exe' 'uplay://launch/5266'")]
    [InlineData("Wine", "WINEPREFIX='/p' wine '/p/drive_c/Battle.net/Battle.net.exe' '--exec=launch pro'")]
    [InlineData("Wine", "WINEPREFIX='/p' wine '/p/drive_c/Rockstar/Launcher.exe' '-launchTitleInFolder' 'C:\\Games\\GTA'")]
    [InlineData("itch", "xdg-open 'itch://games/1001'")]
    public void StoreClientHandoff_SaysTheProfileCannotGoThroughIt_NeverToSetAWrapperThere(string source, string command)
    {
        // w4 review minor 2: a plain-Wine prefix card started through its store client was told "Set it in that launcher:
        // wrapper command …", but Ubisoft Connect, Battle.net, Rockstar and itch have no such setting.
        var game = Game(source, command);
        game.Runner = "Wine";
        game.CompatibilityTool = null;
        game.CompatibilityLayerType = null;
        var plan = Service(gamescope: true).BuildPlan(game, Profile(LinuxHdrModes.Gamescope));

        var en = InLanguage("en-US", () => HdrLaunchText.HandoffNotice(plan, game));
        var pt = InLanguage("pt-BR", () => HdrLaunchText.HandoffNotice(plan, game));

        Assert.Equal(LaunchHandoff.StoreClient, plan.Handoff);
        Assert.False(plan.AppliesAtLaunch);
        Assert.Null(HdrLaunchText.LaunchRefusal(plan, launcherAcceptsOptions: true));
        Assert.Equal("This game's store client starts it, and that client has no setting for launch options or a wrapper, so the HDR profile cannot be applied to this game through it.", en);
        Assert.StartsWith("O cliente da loja deste jogo é quem o inicia", pt, StringComparison.Ordinal);
        Assert.DoesNotContain("gamescope", en, StringComparison.Ordinal);
        Assert.DoesNotContain(UiLocalizationService.EnglishMarkersIn(pt), w => !w.Equals("no", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(en, InLanguage("en-US", () => HdrLaunchText.Copied(plan, game)));
    }

    [Fact]
    public void SteamCardWithAWrappedClientCommand_IsAHandoff_AndTheProfileIsNotWrappedAroundSteam()
    {
        // w2-launch review, major 1 repro: Edit game → `gamemoderun steam -applaunch <id>`, HDR = Gamescope. RHI ran
        // `gamescope --hdr-enabled -- gamemoderun steam …`, said HDR was applied, and the game Steam started got none.
        var game = Game("Steam", "gamemoderun steam -applaunch 1234");
        var plan = Service(gamescope: true).BuildPlan(game, Profile(LinuxHdrModes.Gamescope));

        Assert.Equal(LaunchHandoff.Steam, plan.Handoff);
        Assert.False(plan.AppliesAtLaunch);
        Assert.Contains("Launch Options", InLanguage("en-US", () => HdrLaunchText.HandoffNotice(plan, game)), StringComparison.Ordinal);
    }

    [Fact]
    public void BlockedProfileOnAHandoff_IsNotARefusal_TheNoticeSaysWhyInTheUiLanguage()
    {
        // w2-launch review, minor 3: RHI applies nothing to a hand-off, so a blocked profile must not stop Launch.
        var game = Game("Steam", "steam -applaunch 1234");
        var plan = Service(gamescope: false).BuildPlan(game, Profile(LinuxHdrModes.Gamescope));

        Assert.False(plan.CanApply);
        Assert.Null(HdrLaunchText.LaunchRefusal(plan, launcherAcceptsOptions: true));
        Assert.Null(HdrLaunchText.LaunchRefusal(plan, launcherAcceptsOptions: false));
        var en = InLanguage("en-US", () => HdrLaunchText.HandoffNotice(plan, game));
        var pt = InLanguage("pt-BR", () => HdrLaunchText.HandoffNotice(plan, game));
        Assert.StartsWith("The launcher starts this game without the HDR profile, which is blocked: Gamescope mode is selected", en, StringComparison.Ordinal);
        Assert.StartsWith("O launcher inicia este jogo sem o perfil HDR, que está bloqueado: O modo Gamescope", pt, StringComparison.Ordinal);
        Assert.DoesNotContain(UiLocalizationService.EnglishMarkersIn(pt), w => !w.Equals("no", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LutrisSteps_SayCommandPrefixNeedsAdvancedOptions()
    {
        // w2-launch review, nit 10: Lutris shows "Command prefix" only with "Show advanced options" ticked.
        var game = Game("Lutris", "lutris 'lutris:rungame/game'");
        var plan = Service(gamescope: true).BuildPlan(game, Profile(LinuxHdrModes.Gamescope));

        Assert.Contains("tick “Show advanced options”", InLanguage("en-US", () => HdrLaunchText.HandoffNotice(plan, game)), StringComparison.Ordinal);
        Assert.Contains("marque “Mostrar opções avançadas”", InLanguage("pt-BR", () => HdrLaunchText.HandoffNotice(plan, game)), StringComparison.Ordinal);
    }

    [Fact]
    public void LutrisLauncherPrefixGame_NamesTheParentLauncherEntry()
    {
        var game = Game("Lutris", "lutris 'lutris:rungame/ubisoft-connect'");
        game.ParentLauncherName = "Ubisoft Connect";
        var plan = Service(gamescope: true).BuildPlan(game, Profile(LinuxHdrModes.Gamescope));

        Assert.Contains("“Ubisoft Connect”", InLanguage("en-US", () => HdrLaunchText.HandoffNotice(plan, game)), StringComparison.Ordinal);
    }

    [Fact]
    public void DirectLaunch_HasNoHandoffNotice_AndAppliesAtLaunch()
    {
        var game = Game("Manual", "WINEPREFIX=/tmp/pfx wine game.exe");
        var plan = Service(gamescope: true).BuildPlan(game, Profile(LinuxHdrModes.Gamescope));

        Assert.True(plan.AppliesAtLaunch);
        Assert.Null(HdrLaunchText.HandoffNotice(plan, game));
        Assert.Null(HdrLaunchText.LaunchRefusal(plan, launcherAcceptsOptions: true));
        Assert.NotNull(HdrLaunchText.LaunchRefusal(plan, launcherAcceptsOptions: false));
    }

    [Fact]
    public async Task Launcher_RefusesLaunchOptionsOnAStoreHandoff_WithoutStartingAnything()
    {
        // A fake store client that would record that it ran; the refusal comes before any process starts.
        var marker = Path.Combine(_root, "ran");
        var fake = Path.Combine(_root, "steam");
        File.WriteAllText(fake, $"#!/bin/sh\ntouch '{marker}'\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var game = Game("Steam", $"'{fake}' -applaunch 1234");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new LinuxGameLauncher().LaunchAsync(
            // "env" as the wrapper: harmless if the guard were missing (the fake client would just run).
            game, new GameLaunchOptions(new Dictionary<string, string> { ["DXVK_HDR"] = "1" }, "env")));

        Assert.Equal(LinuxGameLauncher.HandoffProfileRefused, error.Message);
        Assert.False(File.Exists(marker));
    }

    private static string InLanguage(string language, Func<string?> text)
    {
        UiLocalizationService.Apply(language);
        try { return text() ?? string.Empty; }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    private static LinuxHdrGameProfile Profile(string mode, bool legacy = false) => new() { Mode = mode, EnableLegacyWsi = legacy };

    private static LinuxHdrLaunchProfileService Service(bool gamescope, bool wayland = true, string? nvidia = null)
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

    private DetectedGame Game(string source, string command) => new()
    {
        Name = "HDR Game", InstallPath = _root, Source = source, Runner = "Proton",
        CompatibilityTool = "GE-Proton11-3", CompatibilityLayerType = "proton", LaunchCommand = command
    };

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
