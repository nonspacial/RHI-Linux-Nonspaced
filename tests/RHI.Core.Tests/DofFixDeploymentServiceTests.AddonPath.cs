using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #255 (upstream 2.8.8 <c>54ec8f2</c>): the DOF Fix add-on goes where ReShade loads add-ons — reshade.ini's
/// <c>[ADDON] AddonPath</c> (relative or absolute), else the binary folder. RHI's copy left in the game folder from before
/// AddonPath was set moves there on the next install or update (the record follows); a same-named file RHI does not own
/// is left and named; RHI's copy changed since it placed it is not deleted.
/// </summary>
public sealed partial class DofFixDeploymentServiceTests
{
    private static void SetAddonPath(DetectedGame game, string value)
        => File.WriteAllText(Path.Combine(game.BinaryDirectory!, "reshade.ini"), $"[GENERAL]\nKeep=1\n[ADDON]\nAddonPath={value}\n");

    [Fact]
    public async Task Install_FollowsARelativeAddonPath()
    {
        var (service, records, game, http) = CancelFixture("addonpath-relative");
        using var _ = http;
        SetAddonPath(game, ".\\addons");

        var result = await service.InstallAsync(game);

        var expected = Path.Combine(game.BinaryDirectory!, "addons", DofFixDeploymentService.AddonFileName);
        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(expected));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, DofFixDeploymentService.AddonFileName)));
        var record = records.Find(game, ManagedComponentKind.DofFix)!;
        Assert.Equal(expected, record.Files.Single().Path);
        Assert.Equal(game.BinaryDirectory, record.BinaryDirectory);
        Assert.DoesNotContain(AddonPathFolder.LeftInGameFolderPrefix, result.Message);
    }

    [Fact]
    public async Task Install_FollowsAnAbsoluteAddonPath_AndRemoveFollowsTheRecord()
    {
        var (service, records, game, http) = CancelFixture("addonpath-absolute");
        using var _ = http;
        var folder = Path.Combine(game.InstallPath, "reshade", "addons");
        SetAddonPath(game, folder);

        var install = await service.InstallAsync(game);
        var expected = Path.Combine(folder, DofFixDeploymentService.AddonFileName);
        Assert.True(install.Success, install.Message);
        Assert.True(File.Exists(expected));

        var remove = await service.RemoveAsync(game);
        Assert.True(remove.Success, remove.Message);
        Assert.False(File.Exists(expected));
        Assert.Null(records.Find(game, ManagedComponentKind.DofFix));
    }

    [Fact]
    public async Task Update_AfterAddonPathIsSet_MovesRhisCopyOutOfTheGameFolder_AndTheRecordFollows()
    {
        var (service, records, game, http) = CancelFixture("addonpath-migrate");
        using var _ = http;
        Assert.True((await service.InstallAsync(game)).Success);
        var old = Path.Combine(game.BinaryDirectory!, DofFixDeploymentService.AddonFileName);
        Assert.True(File.Exists(old));
        SetAddonPath(game, "reshade-addons");

        var update = await service.UpdateAsync(game);

        var moved = Path.Combine(game.BinaryDirectory!, "reshade-addons", DofFixDeploymentService.AddonFileName);
        Assert.True(update.Success, update.Message);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(moved));
        Assert.Equal(moved, records.Find(game, ManagedComponentKind.DofFix)!.Files.Single().Path);
        Assert.DoesNotContain(AddonPathFolder.LeftInGameFolderPrefix, update.Message);
    }

    [Fact]
    public async Task Update_RhisOldCopyChangedSinceInstall_IsKept_AndTheUpdateRefused()
    {
        var (service, records, game, http) = CancelFixture("addonpath-modified");
        using var _ = http;
        Assert.True((await service.InstallAsync(game)).Success);
        var old = Path.Combine(game.BinaryDirectory!, DofFixDeploymentService.AddonFileName);
        File.AppendAllText(old, "user edit");
        SetAddonPath(game, ".\\addons");

        var update = await service.UpdateAsync(game);

        Assert.False(update.Success);
        Assert.Contains("modified", update.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(old));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "addons", DofFixDeploymentService.AddonFileName)));
        Assert.Equal(old, records.Find(game, ManagedComponentKind.DofFix)!.Files.Single().Path);
    }

    [Fact]
    public async Task Install_WithAddonPath_LeavesTheUsersOwnCopyInTheGameFolder_AndNamesIt()
    {
        var (service, _, game, http) = CancelFixture("addonpath-user-file");
        using var _http = http;
        var users = Path.Combine(game.BinaryDirectory!, DofFixDeploymentService.AddonFileName);
        File.WriteAllText(users, "the user's own build");
        SetAddonPath(game, ".\\addons");

        var result = await service.InstallAsync(game);

        Assert.True(result.Success, result.Message);
        Assert.Equal("the user's own build", File.ReadAllText(users));
        Assert.Contains(AddonPathFolder.LeftInGameFolderPrefix + users, result.Message);
    }

    // w13 final review m2: a Windows drive the game's prefix does not map is refused, never joined on as <bin>/Q:/….
    [Fact]
    public async Task Install_AddonPathOnAnUnmappedDrive_IsRefused_AndNoJunkFolderIsMade()
    {
        var (service, records, game, http) = CancelFixture("addonpath-unmapped");
        using var _ = http;
        game.PrefixPath = Path.Combine(_root, "addonpath-unmapped-pfx");
        Directory.CreateDirectory(Path.Combine(game.PrefixPath, "drive_c"));
        SetAddonPath(game, @"Q:\ReShade\Addons");

        var result = await service.InstallAsync(game);

        Assert.False(result.Success);
        Assert.StartsWith(AddonPathFolder.UnmappedPrefix, result.Message);
        Assert.Empty(Directory.EnumerateDirectories(game.BinaryDirectory!));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, DofFixDeploymentService.AddonFileName)));
        Assert.Null(records.Find(game, ManagedComponentKind.DofFix));
    }

    [Fact]
    public async Task Install_AddonPathOnDriveC_GoesIntoThePrefix()
    {
        var (service, records, game, http) = CancelFixture("addonpath-drive-c");
        using var _ = http;
        game.PrefixPath = Path.Combine(_root, "addonpath-drive-c-pfx");
        Directory.CreateDirectory(Path.Combine(game.PrefixPath, "drive_c"));
        SetAddonPath(game, @"C:\ReShade\Addons");

        var result = await service.InstallAsync(game);

        var expected = Path.Combine(game.PrefixPath, "drive_c", "ReShade", "Addons", DofFixDeploymentService.AddonFileName);
        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(expected));
        Assert.Equal(expected, records.Find(game, ManagedComponentKind.DofFix)!.Files.Single().Path);
    }

    // w13 final review m1: AddonPath set after the install is a pending move even when no newer release exists, so
    // Update All carries the add-on to where ReShade loads it instead of reporting "current" forever.
    [Fact]
    public async Task Check_AddonPathSetAfterInstall_SameVersion_ReportsTheMove_AndUpdateMovesIt()
    {
        var (service, records, game, http) = CancelFixture("addonpath-check-move");
        using var _ = http;
        Assert.True((await service.InstallAsync(game)).Success);
        Assert.False((await service.CheckUpdateAsync(game)).UpdateAvailable);
        SetAddonPath(game, ".\\addons");

        var check = await service.CheckUpdateAsync(game);

        Assert.True(check.UpdateAvailable, check.Message);
        Assert.Equal(("1.0.2", "1.0.2"), (check.InstalledVersion, check.LatestVersion));
        Assert.Equal(DofFixDeploymentService.DisplayName + AddonPathFolder.MoveDueSuffix, check.Message);

        var update = await service.UpdateAsync(game);
        var moved = Path.Combine(game.BinaryDirectory!, "addons", DofFixDeploymentService.AddonFileName);
        Assert.True(update.Success, update.Message);
        Assert.True(File.Exists(moved));
        Assert.Equal(moved, records.Find(game, ManagedComponentKind.DofFix)!.Files.Single().Path);
        Assert.False((await service.CheckUpdateAsync(game)).UpdateAvailable);
    }
}
