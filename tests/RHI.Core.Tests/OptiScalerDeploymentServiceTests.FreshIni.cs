using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #269 (upstream 2.8.9 "Replace with a fresh copy…", and its fix: the same template as a fresh install): the game's
/// OptiScaler.ini becomes the template for its version, GPU type and DLSS Inputs; the previous file is kept in RHI's data
/// folder; the record keeps the new hash.
/// </summary>
public sealed partial class OptiScalerDeploymentServiceTests
{
    [Theory]
    [InlineData(OptiScalerGpuType.Nvidia, true, "OptiScaler.nvidia.ini")]
    [InlineData(OptiScalerGpuType.Amd, false, "OptiScaler.amd-nodlss.ini")]
    public async Task ReplaceIniWithFreshCopy_UsesTheFreshInstallTemplate_KeepsTheOldFile_AndRecordsTheHash(OptiScalerGpuType gpu, bool dlss, string templateName)
    {
        var (game, store) = CreateGame("fresh-ini-" + gpu + dlss, GraphicsApi.DirectX12);
        var service = NewService(store, CreatePackage("fresh-ini-package-" + gpu + dlss));
        Assert.True((await service.InstallAsync(game)).Success);
        var ini = Path.Combine(game.BinaryDirectory!, "OptiScaler.ini");
        File.WriteAllText(ini, "[User]\nMine=1\n");
        var choice = new OptiScalerTemplateChoice(gpu, dlss);
        service.IniTemplates = new OptiScalerIniTemplates(Path.Combine(_root, "fresh-inis-" + gpu + dlss), () => choice);
        service.FreshIniBackupDirectory = Path.Combine(_root, "fresh-backups-" + gpu + dlss);

        var result = await service.ReplaceIniWithFreshCopyAsync(game);

        Assert.True(result.Success, result.Message);
        var template = Path.Combine(_root, "fresh-inis-" + gpu + dlss, templateName);
        Assert.Equal(template, OptiScalerIniTemplates.FileName(OptiScalerVariant.Stable, choice) is var name ? Path.Combine(_root, "fresh-inis-" + gpu + dlss, name) : null);
        Assert.StartsWith(OptiScalerDeploymentService.FreshIniReplacedPrefix + template, result.Message, StringComparison.Ordinal);
        var text = File.ReadAllText(ini);
        Assert.DoesNotContain("Mine=1", text);
        Assert.Contains("LoadAsiPlugins=true", text);
        var backup = Assert.Single(Directory.GetFiles(service.FreshIniBackupDirectory, "*.ini", SearchOption.AllDirectories));
        Assert.Equal("[User]\nMine=1\n", File.ReadAllText(backup));
        Assert.EndsWith(OptiScalerDeploymentService.FreshIniBackupPrefix + backup, result.Message, StringComparison.Ordinal);
        Assert.Equal(FileHash.Sha256Hex(ini), store.Find(game, ManagedComponentKind.OptiScaler)!.Files
            .Single(file => Path.GetFileName(file.Path) == "OptiScaler.ini").Sha256, ignoreCase: true);
    }

    [Fact]
    public async Task ReplaceIniWithFreshCopy_WithoutRhisOptiScalerOrATemplate_ChangesNothing()
    {
        var (game, store) = CreateGame("fresh-ini-refused", GraphicsApi.DirectX12);
        var service = NewService(store, CreatePackage("fresh-ini-refused-package"));
        Assert.Equal(OptiScalerDeploymentService.FreshIniNotManagedMessage, (await service.ReplaceIniWithFreshCopyAsync(game)).Message);

        Assert.True((await service.InstallAsync(game)).Success);
        var ini = Path.Combine(game.BinaryDirectory!, "OptiScaler.ini");
        var before = File.ReadAllText(ini);
        var refused = await service.ReplaceIniWithFreshCopyAsync(game); // no INI templates (tests, tools)
        Assert.False(refused.Success);
        Assert.Equal(before, File.ReadAllText(ini));
    }
}
