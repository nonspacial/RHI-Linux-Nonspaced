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

/// <summary>
/// w14 final review m12: the game's own DLL ASI Loader moved aside to its Hooked name (<c>UAL.HookedPath</c>) is held:
/// RTX Encore never offers that name nor installs over it. Without one, ASI Loader's Hooked name stays offered (#284).
/// </summary>
public sealed partial class RtxEncoreDeploymentServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AsiLoadersHookedName_IsHeld_WhileItHoldsTheGamesOwnDll(bool movedTheGamesDll)
    {
        var proxy = Path.Combine(_bin, "dinput8.dll");
        File.WriteAllBytes(proxy, Pe(0x41));
        var hooked = Path.Combine(_bin, "dinput8Hooked.dll");
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["UAL.ProxyName"] = "dinput8.dll" };
        if (movedTheGamesDll)
        {
            File.WriteAllText(hooked, "the game's own dinput8.dll");
            metadata["UAL.HookedPath"] = hooked;
        }
        _records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(_game), GameName = _game.Name, Component = ManagedComponentKind.UltimateAsiLoader,
            BinaryDirectory = _bin, Version = "9.7.0", Metadata = metadata,
            Files = [new LinuxDeployedFile { Path = proxy, Sha256 = FileHash.Sha256Hex(proxy) }]
        });
        var service = Service(new EncoreHandler());

        var offered = service.OfferedNames(_game);
        var saved = service.SetName(_game, "dinput8Hooked.dll");

        Assert.Equal(!movedTheGamesDll, offered.Contains("dinput8Hooked.dll", StringComparer.OrdinalIgnoreCase));
        Assert.Equal(!movedTheGamesDll, saved.Success);
        if (!movedTheGamesDll) return;
        // A name saved before ASI Loader moved the game's DLL is refused at install, the game's file untouched.
        _names.SetRtxEncore(_game, "dinput8Hooked.dll");
        var install = await service.InstallAsync(_game);
        Assert.Equal(RtxEncoreDeploymentService.NameHeldPrefix + "dinput8Hooked.dll", install.Message);
        Assert.Equal("the game's own dinput8.dll", File.ReadAllText(hooked));
    }
}
