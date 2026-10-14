using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #97 (upstream 2.8.7 <c>GetUserIniPath</c>/<c>SeedUserInis</c>, install step 5): per-GPU OptiScaler INI templates. The
/// user file for a channel and GPU setup is named as upstream, seeded once from upstream's own file, never overwritten,
/// and is what a new install deploys as the game's OptiScaler.ini.
/// </summary>
public sealed class OptiScalerIniTemplateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-os-templates-" + Guid.NewGuid().ToString("N"));

    public OptiScalerIniTemplateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // Upstream RHI 2.8.7's RenoDXCommander/OptiScaler*.ini, by SHA-256 (Intel uses the AMD files, as upstream).
    public static TheoryData<OptiScalerVariant, OptiScalerGpuType, bool, string, string> Upstream => new()
    {
        { OptiScalerVariant.Stable, OptiScalerGpuType.Nvidia, false, "OptiScaler.nvidia.ini", "FA98579F18F321F700D1E92BA2CD57A71C54A2315BFAFE7E9DA4449B7B4BE23B" },
        { OptiScalerVariant.Stable, OptiScalerGpuType.Amd, true, "OptiScaler.amd-dlss.ini", "FA98579F18F321F700D1E92BA2CD57A71C54A2315BFAFE7E9DA4449B7B4BE23B" },
        { OptiScalerVariant.Stable, OptiScalerGpuType.Intel, false, "OptiScaler.amd-nodlss.ini", "A9EF7643EBACDA5A5ECE0E1C65C1A8BDBDD4E640F55D4010B8927883B548ADD1" },
        { OptiScalerVariant.Nightly, OptiScalerGpuType.Nvidia, true, "OptiScaler_nightly.nvidia.ini", "CF3CA875B28D4B6DD9A0DDA91A43CAE8C6612A22CBE948182652CBB413B97F4A" },
        { OptiScalerVariant.Nightly, OptiScalerGpuType.Amd, false, "OptiScaler_nightly.amd-nodlss.ini", "CF3CA875B28D4B6DD9A0DDA91A43CAE8C6612A22CBE948182652CBB413B97F4A" },
        { OptiScalerVariant.DlssNr, OptiScalerGpuType.Nvidia, true, "OptiScaler_dlssnr.nvidia.ini", "884F5185AD63CE660C69ADA612B0FFFF670AAC4C578B371B7AAAA7FD9738A0B1" },
        { OptiScalerVariant.DlssNr, OptiScalerGpuType.Amd, false, "OptiScaler_dlssnr.amd-nodlss.ini", "874D064F99A7DF3F104899428F3CADE67A2891B727631749C09188CB547E6338" },
        { OptiScalerVariant.F5, OptiScalerGpuType.Intel, true, "OptiScaler_f5.amd-dlss.ini", "5FAE06DC48461A3B374535714A56831166DEC691E40E0358F636017EFA1F3F76" },
        { OptiScalerVariant.F5, OptiScalerGpuType.Amd, false, "OptiScaler_f5.amd-nodlss.ini", "77AEA9CBA1EC6D8A1D9F975330C4910112CC430CB904E1418AE827928E90343D" },
    };

    [Theory]
    [MemberData(nameof(Upstream))]
    public void EnsureSeeded_WritesUpstreamsFile_UnderUpstreamsName(OptiScalerVariant variant, OptiScalerGpuType gpu, bool dlss, string name, string sha256)
    {
        var templates = new OptiScalerIniTemplates(Path.Combine(_root, "inis"), () => new(gpu, dlss));

        var path = templates.EnsureSeeded(variant);

        Assert.Equal(Path.Combine(_root, "inis", name), path);
        Assert.Equal(sha256, FileHash.Sha256Hex(path), ignoreCase: true);
    }

    [Fact]
    public void EnsureSeeded_NeverOverwritesTheUsersEdits_AndSeedAllCreatesEveryMissingFile()
    {
        var dir = Path.Combine(_root, "inis");
        var templates = new OptiScalerIniTemplates(dir, () => new(OptiScalerGpuType.Nvidia, true));
        var path = templates.EnsureSeeded(OptiScalerVariant.Stable);
        File.WriteAllText(path, "[Menu]\nShortcutKey=0x24\n");

        Assert.Equal(path, templates.EnsureSeeded(OptiScalerVariant.Stable));
        Assert.Equal("[Menu]\nShortcutKey=0x24\n", File.ReadAllText(path));

        var all = templates.SeedAll();
        Assert.Equal(12, all.Count);
        Assert.Equal(12, Directory.GetFiles(dir).Length);
        Assert.Equal("[Menu]\nShortcutKey=0x24\n", File.ReadAllText(path));
    }

    [Fact]
    public void Settings_GpuTypeAndDlssInputs_DefaultLikeUpstream_AndNormalise()
    {
        var defaults = new LinuxAppSettings();
        Assert.Equal(new OptiScalerTemplateChoice(OptiScalerGpuType.Nvidia, true), OptiScalerIniTemplates.ChoiceFrom(defaults));
        Assert.Equal("AMD", new LinuxAppSettings { OptiScalerGpuType = " amd " }.Normalize().OptiScalerGpuType);
        Assert.Equal("Intel", new LinuxAppSettings { OptiScalerGpuType = "INTEL" }.Normalize().OptiScalerGpuType);
        Assert.Equal("NVIDIA", new LinuxAppSettings { OptiScalerGpuType = "Matrox" }.Normalize().OptiScalerGpuType);
        Assert.Equal(new OptiScalerTemplateChoice(OptiScalerGpuType.Intel, false),
            OptiScalerIniTemplates.ChoiceFrom(new LinuxAppSettings { OptiScalerGpuType = "Intel", OptiScalerDlssInputs = false }));

        var store = new LinuxSettingsStore(Path.Combine(_root, "settings.json"), Path.Combine(_root, "cache"), Path.Combine(_root, "data"));
        store.Save(new LinuxAppSettings { OptiScalerGpuType = "AMD", OptiScalerDlssInputs = false });
        Assert.Equal(new OptiScalerTemplateChoice(OptiScalerGpuType.Amd, false), OptiScalerIniTemplates.ChoiceFrom(store.Load()));
    }

    [Fact]
    public async Task Install_DeploysTheUsersTemplateAsOptiScalerIni_ForItsChannelAndGpu()
    {
        var (game, store, package) = Arrange("install");
        var choice = new OptiScalerTemplateChoice(OptiScalerGpuType.Amd, false);
        var templates = new OptiScalerIniTemplates(Path.Combine(_root, "inis"), () => choice);
        var userTemplate = templates.EnsureSeeded(OptiScalerVariant.Nightly);
        File.AppendAllText(userTemplate, "\n; edited by the user\n");
        var service = Service(store, package, templates);

        var installed = await service.InstallAsync(game, OptiScalerVariant.Nightly);

        Assert.True(installed.Success, installed.Message);
        Assert.Contains(OptiScalerIniTemplates.NotePrefix + userTemplate, installed.Message);
        var ini = Path.Combine(game.BinaryDirectory!, "OptiScaler.ini");
        Assert.Equal(File.ReadAllText(userTemplate), File.ReadAllText(ini));
        Assert.Contains(store.Find(game, ManagedComponentKind.OptiScaler)!.Files, file => file.Path == ini && file.AllowUserChanges);

        // Another GPU setup on another game: its own template, seeded on first use.
        var (game2, store2, package2) = Arrange("install-nvidia");
        choice = new OptiScalerTemplateChoice(OptiScalerGpuType.Nvidia, true);
        Assert.True((await Service(store2, package2, templates).InstallAsync(game2)).Success);
        Assert.Equal(File.ReadAllBytes(Path.Combine(_root, "inis", "OptiScaler.nvidia.ini")), File.ReadAllBytes(Path.Combine(game2.BinaryDirectory!, "OptiScaler.ini")));
    }

    [Fact]
    public async Task Install_TemplateFolderUnwritable_UsesThePackagesIni_AndSaysSo()
    {
        var (game, store, package) = Arrange("unwritable");
        var blocked = Path.Combine(_root, "not-a-folder");
        File.WriteAllText(blocked, "a file where the INIs folder should be");
        var service = Service(store, package, new OptiScalerIniTemplates(blocked, () => new(OptiScalerGpuType.Nvidia, true)));

        var installed = await service.InstallAsync(game);

        Assert.True(installed.Success, installed.Message);
        Assert.Contains(OptiScalerIniTemplates.UnavailablePrefix, installed.Message);
        Assert.Equal(File.ReadAllText(Path.Combine(package.RootDirectory, "OptiScaler.ini")), File.ReadAllText(Path.Combine(game.BinaryDirectory!, "OptiScaler.ini")));
    }

    private static OptiScalerDeploymentService Service(LinuxDeploymentRecordStore store, OptiScalerPackage package, OptiScalerIniTemplates templates)
        => new(new TransactionalFileDeploymentService(), store, new WineDllOverrideService(), new OnePackage(package)) { IniTemplates = templates };

    private (DetectedGame Game, LinuxDeploymentRecordStore Store, OptiScalerPackage Package) Arrange(string name)
    {
        var bin = Path.Combine(_root, name, "game");
        var prefix = Path.Combine(_root, name, "pfx");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(prefix);
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n");
        var pkg = Path.Combine(_root, name, "package");
        Directory.CreateDirectory(pkg);
        var dll = new byte[4096];
        dll[0] = (byte)'M'; dll[1] = (byte)'Z';
        System.Text.Encoding.ASCII.GetBytes("optiscaler/OptiScaler package").CopyTo(dll, 128);
        File.WriteAllBytes(Path.Combine(pkg, "OptiScaler.dll"), dll);
        File.WriteAllText(Path.Combine(pkg, "OptiScaler.ini"), "[Plugins]\nLoadReshade=auto\n; the package's own INI\n");
        var game = new DetectedGame
        {
            Name = "Template " + name, InstallPath = bin, BinaryDirectory = bin, ExecutablePath = exe, PrefixPath = prefix,
            Runner = "Proton", GraphicsApi = GraphicsApi.DirectX12, SteamAppId = 4243
        };
        return (game, new LinuxDeploymentRecordStore(Path.Combine(_root, name, "records.json")),
            new OptiScalerPackage("9.9.9", "https://github.com/optiscaler/OptiScaler/releases/fake", pkg));
    }

    private sealed class OnePackage(OptiScalerPackage package) : IOptiScalerPackageProvider
    {
        public Task<OptiScalerPackage> GetLatestAsync(CancellationToken cancellationToken = default) => Task.FromResult(package);

        public Task<OptiScalerPackage> GetLatestAsync(OptiScalerVariant variant, CancellationToken cancellationToken = default)
            => Task.FromResult(package with { Variant = variant });
    }
}

/// <summary>#97: the Settings dialog starts from the snapshot, so the snapshot must carry the template choice it saves.</summary>
[Collection(UiLanguageCollection.Name)]
public sealed class OptiScalerTemplateSettingsTests
{
    [Fact]
    public async Task SettingsSnapshot_CarriesTheTemplateChoice_SoSavingTheDialogKeepsIt()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var settings = fx.Vm.Settings.GetLinuxSettingsSnapshot().Settings;
        settings.OptiScalerGpuType = "Intel";
        settings.OptiScalerDlssInputs = false;
        settings.UiLanguage = "en-US";

        fx.Vm.Settings.SaveLinuxSettings(settings);

        var again = fx.Vm.Settings.GetLinuxSettingsSnapshot().Settings;
        Assert.Equal("Intel", again.OptiScalerGpuType);
        Assert.False(again.OptiScalerDlssInputs);
        Assert.Equal(new OptiScalerTemplateChoice(OptiScalerGpuType.Intel, false), OptiScalerIniTemplates.ChoiceFrom(fx.Settings.Load()));
    }
}
