using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

// #208 (owner decision, unlike upstream's MergeRsIni): ReShade's install and update only fill in the template keys the
// game's ReShade.ini lacks, so the user's own values survive; a new file is still a copy of the template; the explicit
// "Deploy ReShade.ini" still writes every template key; the Settings values are written as before.
public sealed partial class ReShadeChannelTests
{
    private const string FillInTemplate =
        "[ADDON]\nDisabledAddons=Generic Depth,Effect Runtime Sync\n\n[GENERAL]\nNoReloadOnInit=1\n\n[OVERLAY]\nTutorialProgress=4\nVariableListUseTabs=1\n";

    private UserIniTemplates FillInTemplates(string name)
    {
        var templates = new UserIniTemplates(Path.Combine(_root, name, "inis"));
        File.WriteAllText(templates.EnsureSeeded(UserIniTemplate.ReShade), FillInTemplate);
        File.WriteAllText(templates.EnsureSeeded(UserIniTemplate.ReShadeVulkan), FillInTemplate);
        return templates;
    }

    [Fact]
    public async Task Install_AndUpdate_KeepTheGamesOwnValues_AndFillInOnlyMissingTemplateKeys()
    {
        var (game, _, service, handler, options) = IniUpdateGame("fill-in", packs: false);
        File.WriteAllText(IniOf(game), "; mine\n[ADDON]\nDisabledAddons=\n\n[OVERLAY]\nTutorialProgress=0\n");
        var templates = FillInTemplates("fill-in");
        service.IniDefaults = (g, vulkan) => ReShadeGlobalSettingsApplier.InstallDefaults(new LinuxAppSettings(), g, templates, vulkan);

        Assert.True((await service.InstallAsync(game, options)).Success);
        var installed = File.ReadAllText(IniOf(game));
        Assert.Contains("DisabledAddons=\n", installed);
        Assert.Contains("TutorialProgress=0", installed);
        Assert.Contains("NoReloadOnInit=1", installed);
        Assert.Contains("VariableListUseTabs=1", installed);

        // The user changes a value the template added and one of the game's own; an update keeps both.
        File.WriteAllText(IniOf(game), installed.Replace("NoReloadOnInit=1", "NoReloadOnInit=0").Replace("TutorialProgress=0", "TutorialProgress=2"));
        await UpdateAsync(service, handler, game, options, "9.9.9");

        var updated = File.ReadAllText(IniOf(game));
        Assert.Contains("NoReloadOnInit=0", updated);
        Assert.DoesNotContain("NoReloadOnInit=1", updated);
        Assert.Contains("TutorialProgress=2", updated);
        Assert.Contains("DisabledAddons=\n", updated);
        Assert.Contains("; mine", updated);
    }

    [Fact]
    public async Task Install_NewIni_IsACopyOfTheTemplate()
    {
        var (game, _, service, _, options) = IniUpdateGame("fill-in-new", packs: false);
        var templates = FillInTemplates("fill-in-new");
        service.IniDefaults = (g, vulkan) => ReShadeGlobalSettingsApplier.InstallDefaults(new LinuxAppSettings(), g, templates, vulkan);

        Assert.True((await service.InstallAsync(game, options)).Success);

        Assert.Equal(FillInTemplate, File.ReadAllText(IniOf(game)));
    }

    [Fact]
    public async Task Install_SettingsValues_StillReplaceTheGamesKeys()
    {
        // The Settings hotkey is not a template key (#208 leaves it alone): a non-default choice still goes in.
        var (game, _, service, _, options) = IniUpdateGame("fill-in-settings", packs: false);
        File.WriteAllText(IniOf(game), "[INPUT]\nKeyOverlay=50,0,0,0\n");
        var templates = FillInTemplates("fill-in-settings");
        service.IniDefaults = (g, vulkan) => ReShadeGlobalSettingsApplier.InstallDefaults(new LinuxAppSettings { OverlayHotkey = "45,0,0,0" }, g, templates, vulkan);

        Assert.True((await service.InstallAsync(game, options)).Success);

        Assert.Contains("KeyOverlay=45,0,0,0", File.ReadAllText(IniOf(game)));
    }

    [Fact]
    public async Task VulkanLayer_Install_KeepsTheGamesOwnValues()
    {
        var (game, store) = CreateVulkanGame("vk-fill-in");
        File.WriteAllText(Path.Combine(game.BinaryDirectory!, "ReShade.ini"), "[OVERLAY]\nTutorialProgress=0\n");
        var templates = FillInTemplates("vk-fill-in");
        var service = NewVulkanService(store, new LunarGHandler(), Path.Combine(_root, "vk-fill-in-cache"));
        service.IniDefaults = (g, vulkan) => ReShadeGlobalSettingsApplier.InstallDefaults(new LinuxAppSettings(), g, templates, vulkan);

        var result = await service.InstallAsync(game, LayerOptions());

        Assert.True(result.Success, result.Message);
        var text = File.ReadAllText(Path.Combine(game.BinaryDirectory!, "ReShade.ini"));
        Assert.Contains("TutorialProgress=0", text);
        Assert.Contains("NoReloadOnInit=1", text);
    }

    [Fact]
    public async Task DeployReShadeIni_StillWritesEveryTemplateKey()
    {
        // Coordinator decision: the explicit Deploy is the user asking for the template, so its keys overwrite.
        var (game, store, service, _, options) = IniUpdateGame("fill-in-deploy", packs: false);
        File.WriteAllText(IniOf(game), "[OVERLAY]\nTutorialProgress=0\n");
        Assert.True((await service.InstallAsync(game, options)).Success);
        var templates = FillInTemplates("fill-in-deploy");

        var deployed = await new UserIniService(templates, store, _ => null).DeployReShadeIniAsync(game, new LinuxAppSettings());

        Assert.True(deployed.Success, deployed.Message);
        var text = File.ReadAllText(IniOf(game));
        Assert.Contains("TutorialProgress=4", text);
        Assert.DoesNotContain("TutorialProgress=0", text);
        Assert.Contains("DisabledAddons=Generic Depth,Effect Runtime Sync", text);
    }
}
