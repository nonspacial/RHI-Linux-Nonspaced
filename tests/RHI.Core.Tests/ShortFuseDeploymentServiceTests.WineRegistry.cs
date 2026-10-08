using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// #24 (DEP-05): ShortFuse no longer snapshots user.reg and writes it back whole (truncate-and-write, every other change
// since the snapshot reverted). A failed removal undoes only the overrides it changed, key by key, through the editor.
// #30: a ReShade override the user changed is neither suspended nor put back.
public sealed partial class ShortFuseDeploymentServiceTests
{
    private static string UserReg(GameFixture fixture) => File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg"));

    [Fact]
    public async Task RemoveFailingLate_UndoesItsOwnOverridesKeyByKey_AndKeepsOtherRegistryChanges()
    {
        using var fixture = CreateGame("late-failure");
        var editor = new WineRegistryEditor(_ => false);
        var ctx = CreateContext(fixture, fullNetwork: true, wineOverrides: new WineDllOverrideService(editor));
        AddManagedReShade(ctx.Records, fixture, withWineOverride: true);
        File.WriteAllText(Path.Combine(fixture.Bin, "ReShade.ini"), "[INSTALL]\nHookStreamline=0\n");
        var install = await ctx.ShortFuse.InstallAsync(fixture.Game, autoConfigEnabled: true);
        Assert.True(install.Success, install.Message);
        Assert.Null(WineDllOverrideService.TryReadOverride(UserReg(fixture), "dxgi", "Game.exe")); // suspended
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(UserReg(fixture), "winmm", "Game.exe"));

        var hive = Path.Combine(fixture.Prefix, "user.reg");
        var edits = 0;
        editor.InsideLockForTesting = _ =>
        {
            switch (++edits)
            {
                case 1:
                    // Mid-removal, something else records its own setting in the hive (a launcher, a hand edit).
                    File.AppendAllText(hive, "\n[Software\\\\Vendor\\\\Game] 1\n\"Volume\"=\"7\"\n");
                    break;
                case 2:
                    // ReShade's override is being put back; the very next ledger write (ShortFuse's record) fails.
                    ctx.Records.SaveFaultForTesting = () =>
                    {
                        ctx.Records.SaveFaultForTesting = null;
                        return new IOException("Simulated ledger failure at the end of the removal");
                    };
                    break;
            }
        };

        var remove = await ctx.ShortFuse.RemoveAsync(fixture.Game);

        Assert.False(remove.Success);
        var text = UserReg(fixture);
        Assert.Null(WineDllOverrideService.TryReadOverride(text, "dxgi", "Game.exe")); // suspended again
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(text, "winmm", "Game.exe")); // UAL's back
        Assert.Contains("\"Volume\"=\"7\"", text); // the whole-hive restore threw this away
        Assert.Contains("\"x\"=\"y\"", text);
        Assert.NotNull(ctx.Records.Find(fixture.Game, ManagedComponentKind.ShortFuse));
        Assert.DoesNotContain(Directory.GetFiles(fixture.Prefix), path => path.Contains(".rhi-tmp-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReShadeOverrideTheUserChanged_IsNeitherSuspendedNorPutBackByShortFuse()
    {
        using var fixture = CreateGame("user-reshade-override");
        var ctx = CreateContext(fixture, fullNetwork: true);
        AddManagedReShade(ctx.Records, fixture, withWineOverride: true);
        var hive = Path.Combine(fixture.Prefix, "user.reg");
        File.WriteAllText(hive, WineDllOverrideService.SetOverride(File.ReadAllText(hive), "dxgi", "builtin", "Game.exe"));

        var install = await ctx.ShortFuse.InstallAsync(fixture.Game, autoConfigEnabled: true);
        Assert.True(install.Success, install.Message);
        Assert.Equal("builtin", WineDllOverrideService.TryReadOverride(UserReg(fixture), "dxgi", "Game.exe"));
        Assert.Equal("0", ctx.Records.Find(fixture.Game, ManagedComponentKind.ShortFuse)!.Metadata["ShortFuse.ReShadeOverrideSuspended"]);

        var remove = await ctx.ShortFuse.RemoveAsync(fixture.Game);

        Assert.True(remove.Success, remove.Message);
        Assert.Equal("builtin", WineDllOverrideService.TryReadOverride(UserReg(fixture), "dxgi", "Game.exe"));
    }

    [Fact]
    public async Task Remove_WhileWineRunsInThePrefix_IsRefusedBeforeTeardown()
    {
        using var fixture = CreateGame("wine-running");
        var ctx = CreateContext(fixture, fullNetwork: true);
        AddManagedReShade(ctx.Records, fixture, withWineOverride: true);
        Assert.True((await ctx.ShortFuse.InstallAsync(fixture.Game, autoConfigEnabled: true)).Success);
        var asi = Path.Combine(fixture.Bin, ShortFuseDeploymentService.ReShadeAsiName);
        var userRegBefore = UserReg(fixture);
        var running = CreateContext(fixture, fullNetwork: true, wineOverrides: new WineDllOverrideService(new WineRegistryEditor(_ => true)));

        var remove = await running.ShortFuse.RemoveAsync(fixture.Game);

        Assert.False(remove.Success);
        Assert.Equal(WineRegistryEditor.PrefixInUseRefusalPrefix + fixture.Prefix, remove.Message);
        Assert.True(File.Exists(asi));
        Assert.Equal(userRegBefore, UserReg(fixture));
        Assert.NotNull(running.Records.Find(fixture.Game, ManagedComponentKind.ShortFuse));
    }
}
