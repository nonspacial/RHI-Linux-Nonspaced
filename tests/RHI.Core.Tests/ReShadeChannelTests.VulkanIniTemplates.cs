using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

// w5 integration (#92 × #181): the Vulkan layer install writes ReShade.ini from the user's reshade.vulkan.ini template and
// the Settings values (depth defines kept), the way the DirectX install writes it from reshade.ini.
public sealed partial class ReShadeChannelTests
{
    [Fact]
    public async Task VulkanLayer_Install_WithIniDefaults_UsesTheVulkanTemplate_DepthDefinesKept()
    {
        var (game, store) = CreateVulkanGame("vk-ini-template");
        var templates = new UserIniTemplates(Path.Combine(_root, "vk-ini-template", "inis"));
        var service = NewVulkanService(store, new LunarGHandler(), Path.Combine(_root, "vk-ini-template-cache"));
        var asked = new List<bool>();
        service.IniDefaults = (g, vulkan) =>
        {
            asked.Add(vulkan);
            return ReShadeGlobalSettingsApplier.InstallDefaults(new LinuxAppSettings { OverlayHotkey = "45,0,0,0" }, g, templates, vulkan);
        };

        var result = await service.InstallAsync(game, LayerOptions());

        Assert.True(result.Success, result.Message);
        Assert.Equal([true], asked);
        var text = File.ReadAllText(Path.Combine(game.BinaryDirectory!, "ReShade.ini"));
        Assert.Contains("RESHADE_DEPTH_INPUT_IS_REVERSED=1", text); // reshade.vulkan.ini only
        Assert.DoesNotContain("TutorialProgress", text); // reshade.ini only
        Assert.Contains("KeyOverlay=45,0,0,0", text);
        Assert.True(File.Exists(templates.PathOf(UserIniTemplate.ReShadeVulkan))); // seeded on first use
    }
}
