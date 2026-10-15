using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

// #181: the template and the Settings values reach the game's ReShade.ini through the install itself.
public sealed partial class ReShadeChannelTests
{
    [Fact]
    public async Task Install_WithIniDefaults_MergesTheTemplateIntoTheGamesReShadeIni()
    {
        var (game, store) = CreateGame("ini-template");
        File.WriteAllText(Path.Combine(game.BinaryDirectory!, "ReShade.ini"),
            "; mine\n[ADDON]\nDisabledAddons=Generic Depth\n[OVERLAY]\nKeepMe=1\n");
        var custom = Path.Combine(_root, "ini-template", "MyReShade.dll");
        PeArchitectureDetectorTests.WritePe(custom, 0x8664, 1_100_000);
        StampReShadeSignature(custom);
        var templates = new UserIniTemplates(Path.Combine(_root, "ini-template", "inis"));
        var service = NewService(store, new MutableHandler(), Path.Combine(_root, "ini-template-cache"));
        service.IniDefaults = g => ReShadeGlobalSettingsApplier.InstallDefaults(new LinuxAppSettings { OverlayHotkey = "45,0,0,0" }, g, templates);

        var installed = await service.InstallAsync(game, new ReShadeInstallOptions { Channel = ReShadeChannel.Custom, CustomDllPath = custom, ManageIni = true });

        Assert.True(installed.Success, installed.Message);
        var text = File.ReadAllText(Path.Combine(game.BinaryDirectory!, "ReShade.ini"));
        Assert.Contains("; mine", text);
        Assert.Contains("KeepMe=1", text);
        Assert.Contains("DisabledAddons=Generic Depth,Effect Runtime Sync", text); // template wins (upstream)
        Assert.Contains("VariableListUseTabs=1", text);
        Assert.Contains("KeyOverlay=45,0,0,0", text);
        Assert.Contains(@"EffectSearchPaths=.\reshade-shaders\Shaders\**", text);
        Assert.True(File.Exists(templates.PathOf(UserIniTemplate.ReShade))); // seeded on first use
    }
}
