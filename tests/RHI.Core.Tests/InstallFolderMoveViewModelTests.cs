using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #293 (upstream 2.9.2) over the real offline shell: after Change install folder (Edit game) or Reset install folder
/// (Restore automatic detection) RHI asks to move the game's mods; declined, nothing changes. A folder the manifest
/// corrected takes the installs with it on its own, never while the game runs.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class InstallFolderMoveViewModelTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-movevm-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        UiLocalizationService.Apply("en-US");
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private sealed class Running(bool running) : IGameRunningProbe
    {
        public bool IsRunning(DetectedGame game) => running;
    }

    private static string Addon(ViewModelFixture fx, DetectedGame game, string dir)
    {
        var addon = Path.Combine(dir, "dof.addon64");
        File.WriteAllText(addon, "dof");
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.DofFix, BinaryDirectory = dir,
            Files = [new LinuxDeployedFile { Path = addon, Sha256 = FileHash.Sha256Hex(addon) }]
        });
        return addon;
    }

    private static async Task<(ViewModelFixture Fx, DetectedGame Game, string Root, string Bin, string BinExe)> ManualGameAsync(bool answer)
    {
        var fx = await ViewModelFixture.StartAsync([]);
        fx.Managed.FolderMoveRunningProbe = new Running(false);
        var game = fx.Game("Tunic");
        var (added, message) = await fx.Vm.ManualGames.TryAddManualGameAsync(new()
        {
            Name = game.Name, InstallPath = game.InstallPath, ExecutablePath = game.ExecutablePath, Runner = "Manual Windows"
        });
        Assert.True(added, message);
        await fx.WhenSelectionProbeIdleAsync(TimeSpan.FromSeconds(60));
        var bin = Directory.CreateDirectory(Path.Combine(game.InstallPath, "bin")).FullName;
        var binExe = Path.Combine(bin, "Tunic.exe");
        File.WriteAllBytes(binExe, [0x4D, 0x5A]);
        fx.Vm.ConfirmFolderMoveAsync = _ => Task.FromResult(answer);
        return (fx, fx.Vm.SelectedGame!, game.InstallPath, bin, binExe);
    }

    [Fact]
    public async Task ChangeInstallFolder_AsksToMoveTheMods_AndMovesThemWithTheNewFolder()
    {
        var (fx, game, root, bin, binExe) = await ManualGameAsync(answer: true);
        using var _ = fx;
        var addon = Addon(fx, game, game.BinaryDirectory!);
        MainWindowViewModel.FolderMovePrompt? asked = null;
        fx.Vm.ConfirmFolderMoveAsync = prompt => { asked = prompt; return Task.FromResult(true); };

        var (saved, message) = await fx.Vm.ManualGames.SaveSelectedGameEditAsync(binExe, root, bin, null, null);

        Assert.True(saved, message);
        Assert.StartsWith($"Moved Tunic's mods to {bin}.", message, StringComparison.Ordinal);
        Assert.Equal("Move the game's mods?", asked!.Title);
        Assert.Equal($"RHI installed {LinuxManagedComponentService.DisplayDependencyName(ManagedComponentKind.DofFix)} in {root}. Move the game's mods, settings and presets to the new folder, {bin}? Nothing is moved while the game is running.",
            asked.Message);
        Assert.False(File.Exists(addon));
        Assert.Equal("dof", File.ReadAllText(Path.Combine(bin, "dof.addon64")));
        var edit = Assert.Single(fx.Library.Load().GameEdits).Value;
        Assert.Equal(bin, edit.BinaryDirectory);
        Assert.Equal(bin, Assert.Single(fx.Records.All()).BinaryDirectory);
    }

    [Fact]
    public async Task ChangeInstallFolder_Declined_ChangesNothing()
    {
        var (fx, game, root, bin, binExe) = await ManualGameAsync(answer: false);
        using var _ = fx;
        var addon = Addon(fx, game, game.BinaryDirectory!);

        var (saved, message) = await fx.Vm.ManualGames.SaveSelectedGameEditAsync(binExe, root, bin, null, null);

        Assert.False(saved);
        Assert.Equal($"Nothing was changed: Tunic's components stay in {root}. To change the folder, let RHI move them or remove them first.", message);
        Assert.True(File.Exists(addon));
        Assert.Empty(fx.Library.Load().GameEdits);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResetInstallFolder_OffersTheMoveBack_AndKeepsTheChosenFolderWhenDeclined(bool answer)
    {
        var (fx, _, root, bin, binExe) = await ManualGameAsync(answer);
        using var __ = fx;
        var (saved, message) = await fx.Vm.ManualGames.SaveSelectedGameEditAsync(binExe, root, bin, null, null);
        Assert.True(saved, message);
        await fx.WhenSelectionProbeIdleAsync(TimeSpan.FromSeconds(60));
        var addon = Addon(fx, fx.Vm.SelectedGame!, bin);

        var (reset, resetMessage) = await fx.Vm.ManualGames.ResetSelectedGameEditAsync();

        Assert.Equal(answer, reset);
        if (answer)
        {
            Assert.StartsWith($"Moved Tunic's mods to {root}.", resetMessage, StringComparison.Ordinal);
            Assert.False(File.Exists(addon));
            Assert.True(File.Exists(Path.Combine(root, "dof.addon64")));
            Assert.Empty(fx.Library.Load().GameEdits);
            Assert.Equal(root, Assert.Single(fx.Records.All()).BinaryDirectory);
        }
        else
        {
            Assert.Equal($"Nothing was changed: Tunic keeps the folder you chose. Nothing was changed: Tunic's components stay in {bin}. To change the folder, let RHI move them or remove them first.",
                resetMessage);
            Assert.True(File.Exists(addon));
            Assert.Equal(bin, Assert.Single(fx.Library.Load().GameEdits).Value.BinaryDirectory);
            Assert.Equal(bin, fx.Vm.SelectedGame!.BinaryDirectory);
        }
    }

    // w14 final review m15: a reset saved before its move question was answered (RHI closed there) leaves the installs in
    // the chosen folder and the game on its detected one. Reset, and Edit game, still offer to move them from there.
    [Fact]
    public async Task AResetThatStoppedBeforeItsQuestion_LeavesInstallsThatResetCanStillMove()
    {
        var (fx, _, root, bin, binExe) = await ManualGameAsync(answer: true);
        using var __ = fx;
        var (saved, message) = await fx.Vm.ManualGames.SaveSelectedGameEditAsync(binExe, root, bin, null, null);
        Assert.True(saved, message);
        await fx.WhenSelectionProbeIdleAsync(TimeSpan.FromSeconds(60));
        var addon = Addon(fx, fx.Vm.SelectedGame!, bin);
        var state = fx.Library.Load(); // the reset's save landed; the move question never came
        state.GameEdits.Clear();
        fx.Library.Save(state);
        await fx.Vm.RefreshCommand.ExecuteAsync(null);
        await fx.WhenSelectionProbeIdleAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(root, fx.Vm.SelectedGame!.BinaryDirectory);

        var (reset, resetMessage) = await fx.Vm.ManualGames.ResetSelectedGameEditAsync();

        Assert.True(reset, resetMessage);
        Assert.StartsWith($"Moved Tunic's mods to {root}.", resetMessage, StringComparison.Ordinal);
        Assert.False(File.Exists(addon));
        Assert.True(File.Exists(Path.Combine(root, "dof.addon64")));
        Assert.Equal(root, Assert.Single(fx.Records.All()).BinaryDirectory);
    }

    [Fact]
    public async Task EditGame_MovesInstallsLeftInAnEarlierFolder()
    {
        var (fx, _, root, bin, _) = await ManualGameAsync(answer: true);
        using var __ = fx;
        var addon = Addon(fx, fx.Vm.SelectedGame!, bin); // left in bin; the game uses its root
        var other = Directory.CreateDirectory(Path.Combine(root, "other")).FullName;
        var otherExe = Path.Combine(other, "Tunic.exe");
        File.WriteAllBytes(otherExe, [0x4D, 0x5A]);

        var (saved, message) = await fx.Vm.ManualGames.SaveSelectedGameEditAsync(otherExe, root, other, null, null);

        Assert.True(saved, message);
        Assert.False(File.Exists(addon));
        Assert.True(File.Exists(Path.Combine(other, "dof.addon64")));
    }

    private DetectedGame ManifestMoved(string name, int appId, out string oldDir, out string newDir)
    {
        var root = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        oldDir = Directory.CreateDirectory(Path.Combine(root, "ProjectPBZ", "Binaries", "Win64")).FullName;
        newDir = Directory.CreateDirectory(Path.Combine(root, "platform", "steam")).FullName;
        var exe = Path.Combine(newDir, "PBZ.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        return new DetectedGame
        {
            Name = name, InstallPath = newDir, ManifestInstallRoot = root, ExecutablePath = exe, BinaryDirectory = newDir,
            SteamAppId = appId, Source = "Steam", Runner = "Proton"
        };
    }

    [Fact]
    public async Task AFolderTheManifestCorrected_TakesTheModsWithIt_OnItsOwn()
    {
        var game = ManifestMoved("Phantom Blade Zero", 80001, out var oldDir, out var newDir);
        using var fx = await ViewModelFixture.StartAsync([game]);
        fx.Managed.FolderMoveRunningProbe = new Running(false);
        var addon = Addon(fx, game, oldDir);

        await fx.Vm.RefreshCommand.ExecuteAsync(null); // the next analysis (start, Refresh, selection) finds the installs behind
        await WaitUntil(() => !File.Exists(addon));

        Assert.Equal("dof", File.ReadAllText(Path.Combine(newDir, "dof.addon64")));
        await WaitUntil(() => fx.Vm.StatusText.Contains("Moved Phantom Blade Zero's mods to platform/steam.", StringComparison.Ordinal));
        Assert.Equal(newDir, Assert.Single(fx.Records.All()).BinaryDirectory);
    }

    [Fact]
    public async Task AnAutomaticMove_WaitsWhileTheGameRuns()
    {
        var game = ManifestMoved("Running Game", 80002, out var oldDir, out _);
        using var fx = await ViewModelFixture.StartAsync([game]);
        fx.Managed.FolderMoveRunningProbe = new Running(true);
        var addon = Addon(fx, game, oldDir);

        await fx.Vm.RefreshCommand.ExecuteAsync(null);
        await WaitUntil(() => fx.Vm.StatusText.Contains($"Running Game's mods stay in {oldDir} until the game is closed; RHI moves them next time.", StringComparison.Ordinal));

        Assert.True(File.Exists(addon));
        Assert.Equal(oldDir, Assert.Single(fx.Records.All()).BinaryDirectory);
    }

    [Fact]
    public async Task ASplitGamesChild_NeverTakesItsSiblingsMods()
    {
        // splitGames children share the store's key and root: another child's installs sit in another folder of that root.
        var game = ManifestMoved("Split Child", 80003, out var siblingDir, out _);
        game.IsManifestSplitChild = true;
        using var fx = await ViewModelFixture.StartAsync([game]);
        fx.Managed.FolderMoveRunningProbe = new Running(false);
        var addon = Addon(fx, game, siblingDir);

        await fx.Vm.RefreshCommand.ExecuteAsync(null);
        await fx.WhenSelectionProbeIdleAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(300);

        Assert.True(File.Exists(addon));
        Assert.Equal(siblingDir, Assert.Single(fx.Records.All()).BinaryDirectory);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }
}
