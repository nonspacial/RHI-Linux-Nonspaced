using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// w14 final review M5: MFG Unlock's ⚙ writes MFGAdaUnlock.ini through a journaled operation on MFG Unlock's record. The
/// file it creates is listed (AllowUserChanges), stays listed through an update, and goes with the removal while it holds
/// what RHI wrote; a file the user changed stays. Refused while MFG Unlock is not installed.
/// </summary>
public sealed partial class MfgAdaUnlockUpdateTests
{
    [Fact]
    public async Task SettingsIni_CreatedByTheCog_IsListed_KeptThroughAnUpdate_AndRemovedWithMfgUnlock()
    {
        var f = await InstalledAsync("cog-ini");
        var ini = MfgUnlockIni.PathIn(f.Bin);

        var written = await f.Managed.SetMfgUnlockMethodAsync(f.Game, MfgUnlockMethod.XeMfgCuda);

        Assert.True(written.Success, written.Message);
        Assert.Equal(MfgUnlockMethod.XeMfgCuda, MfgUnlockIni.ReadMethod(f.Bin));
        var listed = Assert.Single(f.Record!.Files, file => file.Path == ini);
        Assert.True(listed.AllowUserChanges);
        Assert.False(listed.ExistedBeforeInstall);
        Assert.Equal(FileHash.Sha256Hex(ini), listed.Sha256);
        Assert.True((await f.Managed.SetMfgUnlockDlssSrAsync(f.Game, true)).Success);
        Assert.Equal(FileHash.Sha256Hex(ini), f.Record!.Files.Single(file => file.Path == ini).Sha256); // the new hash, same commit

        f.Release.Active = "0.6";
        var update = await f.Managed.UpdateManagedAsync(f.Game, ManagedComponentKind.MfgAdaUnlock, []);
        Assert.True(update.Success, update.Message);
        Assert.Contains(f.Record!.Files, file => file.Path == ini && file.AllowUserChanges);

        var removed = await f.Managed.RemoveAsync(f.Game, ManagedComponentKind.MfgAdaUnlock);

        Assert.True(removed.Success, removed.Message);
        Assert.False(File.Exists(ini));
        Assert.False(File.Exists(f.Addon));
    }

    [Fact]
    public async Task SettingsIni_TheUserChanged_StaysAfterTheRemoval()
    {
        var f = await InstalledAsync("cog-ini-user");
        var ini = MfgUnlockIni.PathIn(f.Bin);
        Assert.True((await f.Managed.SetMfgUnlockMethodAsync(f.Game, MfgUnlockMethod.XeMfgViaDlssG)).Success);
        File.AppendAllText(ini, "; mine\n");

        var removed = await f.Managed.RemoveAsync(f.Game, ManagedComponentKind.MfgAdaUnlock);

        Assert.True(removed.Success, removed.Message);
        Assert.Contains("; mine", File.ReadAllText(ini), StringComparison.Ordinal);
        Assert.False(File.Exists(f.Addon));
    }

    // A file already in the game (the mod's own, or the user's) is written but never listed: removal leaves it.
    [Fact]
    public async Task SettingsIni_ThatWasAlreadyThere_IsNotListed()
    {
        var f = await InstalledAsync("cog-ini-there");
        var ini = MfgUnlockIni.PathIn(f.Bin);
        File.WriteAllText(ini, "[Backend]\nSelected=DLSSG\n");

        Assert.True((await f.Managed.SetMfgUnlockMethodAsync(f.Game, MfgUnlockMethod.XeMfgCuda)).Success);
        Assert.DoesNotContain(f.Record!.Files, file => file.Path == ini);
        Assert.True((await f.Managed.RemoveAsync(f.Game, ManagedComponentKind.MfgAdaUnlock)).Success);
        Assert.Equal(MfgUnlockMethod.XeMfgCuda, MfgUnlockIni.ReadMethod(f.Bin));
    }

    [Fact]
    public async Task SettingsIni_IsRefusedWhileMfgUnlockIsNotInstalled()
    {
        var f = await InstalledAsync("cog-ini-none");
        Assert.True((await f.Managed.RemoveAsync(f.Game, ManagedComponentKind.MfgAdaUnlock)).Success);

        var refused = await f.Managed.SetMfgUnlockMethodAsync(f.Game, MfgUnlockMethod.XeMfgCuda);

        Assert.Equal(ComponentSettingsFile.NotInstalledPrefix + "MFG Unlock", refused.Message);
        Assert.False(File.Exists(MfgUnlockIni.PathIn(f.Bin)));
    }
}
