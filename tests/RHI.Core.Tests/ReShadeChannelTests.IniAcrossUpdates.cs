using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

// #203: what RHI did to ReShade.ini at the first install is still taken back by a removal after any number of updates.
public sealed partial class ReShadeChannelTests
{
    private const string PackEffects = @".\reshade-shaders\RHI\CrosireMaster\Shaders\**";

    private (DetectedGame Game, LinuxDeploymentRecordStore Store, ReShadeDeploymentService Service, MutableHandler Handler, ReShadeInstallOptions Options)
        IniUpdateGame(string name, bool packs = true)
    {
        var (game, store) = CreateGame(name);
        var compiler = Path.Combine(_root, name + "-native", "D3DCompiler_47.dll");
        WriteNativeD3DCompiler(compiler, 0x8664);
        var cache = Path.Combine(_root, name + "-cache");
        SeedShaderCache(cache, "CrosireMaster");
        var handler = new MutableHandler { StableVersion = "9.9.8" };
        var service = NewService(store, handler, cache);
        var options = new ReShadeInstallOptions
        {
            ManageIni = true, D3DCompiler47Path = compiler, ShaderPackIds = packs ? ["CrosireMaster"] : []
        };
        return (game, store, service, handler, options);
    }

    private static string IniOf(DetectedGame game) => Path.Combine(game.BinaryDirectory!, "ReShade.ini");

    private static async Task UpdateAsync(ReShadeDeploymentService service, MutableHandler handler, DetectedGame game, ReShadeInstallOptions options, string version)
    {
        handler.StableVersion = version;
        var updated = await service.InstallAsync(game, options);
        Assert.True(updated.Success, updated.Message);
    }

    [Fact]
    public async Task RhiCreatedIni_UpdatedThenRemoved_IsDeleted()
    {
        var (game, _, service, handler, options) = IniUpdateGame("ini203-created");
        Assert.True((await service.InstallAsync(game, options)).Success);
        Assert.True(File.Exists(IniOf(game)));

        await UpdateAsync(service, handler, game, options, "9.9.9");
        Assert.True((await service.RemoveAsync(game)).Success);

        Assert.False(File.Exists(IniOf(game)));
    }

    [Fact]
    public async Task RhiCreatedIni_ReShadeWroteItsSettings_UpdatedThenRemoved_KeepsOnlyTheirLines()
    {
        var (game, _, service, handler, options) = IniUpdateGame("ini203-created-used");
        Assert.True((await service.InstallAsync(game, options)).Success);
        File.AppendAllText(IniOf(game), "\n[SCREENSHOT]\nSavePath=.\\shots\n");

        await UpdateAsync(service, handler, game, options, "9.9.9");
        await UpdateAsync(service, handler, game, options, "9.9.10");
        Assert.True((await service.RemoveAsync(game)).Success);

        var text = File.ReadAllText(IniOf(game));
        Assert.Contains(@"SavePath=.\shots", text);
        Assert.DoesNotContain(PackEffects, text);
        Assert.DoesNotContain("EffectSearchPaths", text);
    }

    [Fact]
    public async Task GamesOwnIni_UpdatedThenRemoved_TakesBackExactlyRhisEntries()
    {
        var (game, _, service, handler, options) = IniUpdateGame("ini203-own");
        const string original = "[GENERAL]\nEffectSearchPaths=.\\mine\\**\nTextureSearchPaths=.\\mytex\\**\n\n[OVERLAY]\nKeepMe=1\n";
        File.WriteAllText(IniOf(game), original);
        Assert.True((await service.InstallAsync(game, options)).Success);
        Assert.Contains(PackEffects, File.ReadAllText(IniOf(game)));

        await UpdateAsync(service, handler, game, options, "9.9.9");
        Assert.True((await service.RemoveAsync(game)).Success);

        Assert.Equal(original, File.ReadAllText(IniOf(game)));
    }

    [Fact]
    public async Task GamesOwnIni_WithTemplatePaths_UpdatedThenRemoved_TakesBackTheTemplatesEntries()
    {
        var (game, _, service, handler, options) = IniUpdateGame("ini203-template", packs: false);
        const string original = "[GENERAL]\nEffectSearchPaths=.\\mine\\**\n";
        File.WriteAllText(IniOf(game), original);
        var templates = new UserIniTemplates(Path.Combine(_root, "ini203-template", "inis"));
        File.WriteAllText(templates.EnsureSeeded(UserIniTemplate.ReShade), "[GENERAL]\nEffectSearchPaths=.\\tmpl\\**\n");
        service.IniDefaults = (g, vulkan) => ReShadeGlobalSettingsApplier.InstallDefaults(new LinuxAppSettings(), g, templates, vulkan);
        Assert.True((await service.InstallAsync(game, options)).Success);
        Assert.Contains(@".\tmpl\**", File.ReadAllText(IniOf(game)));

        await UpdateAsync(service, handler, game, options, "9.9.9");
        Assert.True((await service.RemoveAsync(game)).Success);

        Assert.DoesNotContain(@".\tmpl\**", File.ReadAllText(IniOf(game)));
        Assert.Contains(@"EffectSearchPaths=.\mine\**", File.ReadAllText(IniOf(game)));
    }

    [Fact]
    public async Task RhiCreatedFromTemplate_ChangedByTheGame_UpdatedThenRemoved_TakesBackRhisKeys()
    {
        var (game, _, service, handler, options) = IniUpdateGame("ini203-template-created");
        var templates = new UserIniTemplates(Path.Combine(_root, "ini203-template-created", "inis"));
        File.WriteAllText(templates.EnsureSeeded(UserIniTemplate.ReShade), "[OVERLAY]\nTutorialProgress=4\n");
        service.IniDefaults = (g, vulkan) => ReShadeGlobalSettingsApplier.InstallDefaults(new LinuxAppSettings(), g, templates, vulkan);
        Assert.True((await service.InstallAsync(game, options)).Success);
        File.AppendAllText(IniOf(game), "\n[SCREENSHOT]\nSavePath=.\\shots\n");

        await UpdateAsync(service, handler, game, options, "9.9.9");
        Assert.True((await service.RemoveAsync(game)).Success);

        var text = File.ReadAllText(IniOf(game));
        Assert.DoesNotContain("EffectSearchPaths", text);
        Assert.DoesNotContain("TextureSearchPaths", text);
    }

    [Fact]
    public async Task ChannelSwitch_ThenRemoved_DeletesTheRhiCreatedIni()
    {
        var (game, _, service, _, options) = IniUpdateGame("ini203-switch");
        Assert.True((await service.InstallAsync(game, options)).Success);
        var custom = Path.Combine(_root, "ini203-switch", "MyReShade.dll");
        PeArchitectureDetectorTests.WritePe(custom, 0x8664, 1_100_000);
        StampReShadeSignature(custom);

        options.Channel = ReShadeChannel.Custom;
        options.CustomDllPath = custom;
        var switched = await service.InstallAsync(game, options);
        Assert.True(switched.Success, switched.Message);
        Assert.True((await service.RemoveAsync(game)).Success);

        Assert.False(File.Exists(IniOf(game)));
    }

    [Fact]
    public async Task RhiCreatedIni_FailedUpdate_ThenRemoved_IsDeleted()
    {
        var (game, store, service, handler, options) = IniUpdateGame("ini203-failed-created");
        var templates = new UserIniTemplates(Path.Combine(_root, "ini203-failed-created", "inis"));
        service.IniDefaults = (g, vulkan) => ReShadeGlobalSettingsApplier.InstallDefaults(new LinuxAppSettings { OverlayHotkey = "45,0,0,0" }, g, templates, vulkan);
        Assert.True((await service.InstallAsync(game, options)).Success);

        handler.StableVersion = "9.9.9";
        using (LedgerWriteBlocker.Block(store, "records.json"))
            Assert.False((await service.InstallAsync(game, options)).Success);
        Assert.True((await service.RemoveAsync(game)).Success);

        Assert.False(File.Exists(IniOf(game)));
    }

    [Fact]
    public async Task GamesOwnIni_FailedUpdate_ThenRemoved_TakesBackExactlyRhisEntries()
    {
        var (game, store, service, handler, options) = IniUpdateGame("ini203-failed-own");
        const string original = "[GENERAL]\nEffectSearchPaths=.\\mine\\**\n";
        File.WriteAllText(IniOf(game), original);
        Assert.True((await service.InstallAsync(game, options)).Success);

        handler.StableVersion = "9.9.9";
        using (LedgerWriteBlocker.Block(store, "records.json"))
            Assert.False((await service.InstallAsync(game, options)).Success);
        Assert.True((await service.RemoveAsync(game)).Success);

        Assert.Equal(original, File.ReadAllText(IniOf(game)));
    }

    [Fact]
    public async Task RhiCreatedIni_DeployedFromTheTemplate_ThenRemoved_IsDeleted()
    {
        var (game, store, service, _, options) = IniUpdateGame("ini203-deploy");
        var templates = new UserIniTemplates(Path.Combine(_root, "ini203-deploy", "inis"));
        Assert.True((await service.InstallAsync(game, options)).Success);
        var deployed = await new UserIniService(templates, store, _ => null).DeployReShadeIniAsync(game, new LinuxAppSettings { OverlayHotkey = "45,0,0,0" });
        Assert.True(deployed.Success, deployed.Message);

        Assert.True((await service.RemoveAsync(game)).Success);

        Assert.False(File.Exists(IniOf(game)));
    }

    [Fact]
    public async Task GamesOwnIni_DeployedTemplatePaths_ThenRemoved_TakesThemBack()
    {
        var (game, store, service, _, options) = IniUpdateGame("ini203-deploy-own", packs: false);
        const string original = "[GENERAL]\nEffectSearchPaths=.\\mine\\**\n";
        File.WriteAllText(IniOf(game), original);
        Assert.True((await service.InstallAsync(game, options)).Success);
        var templates = new UserIniTemplates(Path.Combine(_root, "ini203-deploy-own", "inis"));
        File.WriteAllText(templates.EnsureSeeded(UserIniTemplate.ReShade), "[GENERAL]\nEffectSearchPaths=.\\tmpl\\**\n");
        Assert.True((await new UserIniService(templates, store, _ => null).DeployReShadeIniAsync(game, new LinuxAppSettings())).Success);
        Assert.Contains(@".\tmpl\**", File.ReadAllText(IniOf(game)));

        Assert.True((await service.RemoveAsync(game)).Success);

        Assert.Equal(original, File.ReadAllText(IniOf(game)));
    }

    [Fact]
    public async Task RhiCreatedIni_ChangedByTheUserThenDeployed_ThenRemoved_KeepsTheUsersLines()
    {
        var (game, store, service, _, options) = IniUpdateGame("ini203-deploy-changed");
        var templates = new UserIniTemplates(Path.Combine(_root, "ini203-deploy-changed", "inis"));
        Assert.True((await service.InstallAsync(game, options)).Success);
        File.AppendAllText(IniOf(game), "\n[MINE]\nKeepMe=1\n");
        Assert.True((await new UserIniService(templates, store, _ => null).DeployReShadeIniAsync(game, new LinuxAppSettings())).Success);

        Assert.True((await service.RemoveAsync(game)).Success);

        // The file held the user's change when Deploy wrote it, so it is no longer RHI's alone: kept, RHI's paths taken back.
        var text = File.ReadAllText(IniOf(game));
        Assert.Contains("KeepMe=1", text);
        Assert.DoesNotContain(PackEffects, text);
    }
}
