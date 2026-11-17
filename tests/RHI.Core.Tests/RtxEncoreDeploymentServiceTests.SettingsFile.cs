using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// w14 final review M5: RTX Encore's ⚙ writes rtx-encore.jsonc through a journaled operation on RTX Encore's record. A
/// file it creates is listed (AllowUserChanges): removal takes it while it holds what RHI wrote, and a file the user
/// changed neither blocks the removal nor goes with it. Refused while RTX Encore is not installed.
/// </summary>
public sealed partial class RtxEncoreDeploymentServiceTests
{
    [Fact]
    public async Task SettingsFile_CreatedByTheCog_IsListed_AndGoesWithTheRemoval()
    {
        var service = Service(new EncoreHandler());
        Assert.Equal(ComponentSettingsFile.NotInstalledPrefix + RtxEncoreDeploymentService.DisplayName,
            (await service.SetConfigValueAsync(_game, "frameGeneration", "mode", "fixed")).Message);
        Assert.True((await service.InstallAsync(_game)).Success);
        var path = service.ConfigPath(_game)!;
        Assert.False(File.Exists(path));

        var written = await service.SetConfigValueAsync(_game, "frameGeneration", "mode", "fixed");

        Assert.True(written.Success, written.Message);
        Assert.Equal("fixed", RtxEncoreConfig.Get(File.ReadAllText(path), "frameGeneration", "mode"));
        var listed = Assert.Single(_records.Find(_game, ManagedComponentKind.RtxEncore)!.Files, file => file.Path == path);
        Assert.True(listed.AllowUserChanges);
        Assert.Equal(FileHash.Sha256Hex(path), listed.Sha256);

        var removed = await service.RemoveAsync(_game);

        Assert.True(removed.Success, removed.Message);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(Path.Combine(_bin, "version.dll")));
    }

    [Fact]
    public async Task SettingsFile_TheUserChanged_NeitherBlocksTheRemovalNorGoesWithIt()
    {
        var service = Service(new EncoreHandler());
        Assert.True((await service.InstallAsync(_game)).Success);
        var path = service.ConfigPath(_game)!;
        Assert.True((await service.SetConfigValueAsync(_game, "frameGeneration", "mode", "fixed")).Success);
        File.AppendAllText(path, "// mine\n");

        var removed = await service.RemoveAsync(_game);

        Assert.True(removed.Success, removed.Message);
        Assert.Contains("// mine", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_bin, "version.dll")));
    }
}
