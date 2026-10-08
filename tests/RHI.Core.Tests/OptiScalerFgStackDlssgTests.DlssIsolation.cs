using System.Security.Cryptography;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #135 review finding 1: the direct DLSS swap/restore flows (per-game panel, Quick Apply, Batch
/// Deploy, Restore All) must never target files RHI-managed OptiScaler owns.
/// </summary>
public sealed partial class OptiScalerFgStackDlssgTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DlssSwapRestoreAndBatch_LeaveTheOptiScalerStackItsHashesAndOriginalsUntouched(bool disableStackFirst)
    {
        var fx = CreateFixture(disableStackFirst ? "isolation-disable" : "isolation-remove");
        var stackDir = Path.Combine(fx.Bin, "OptiScaler", "Streamline");
        Directory.CreateDirectory(stackDir);
        var stackDlssg = Path.Combine(stackDir, "nvngx_dlssg.dll");
        var stackCommon = Path.Combine(stackDir, "sl.common.dll");
        File.WriteAllBytes(stackDlssg, Dlssg(0x7A)); // foreign files the stack takes over (.original)
        File.WriteAllBytes(stackCommon, Dlssg(0x34));
        var gameDlssg = Path.Combine(fx.Bin, "nvngx_dlssg.dll");
        var gameInterposer = Path.Combine(fx.Bin, "sl.interposer.dll");
        File.WriteAllBytes(gameDlssg, Dlssg(0x60)); // the game's own FG + Streamline
        File.WriteAllBytes(gameInterposer, Dlssg(0x61));
        Assert.True((await fx.Service.SetAdvancedSettingAsync(fx.Game, OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId, "true")).Success);
        Assert.True(File.Exists(stackDlssg + ".original"));
        var stack = Snapshot(Path.Combine(fx.Bin, "OptiScaler"));
        var defaults = new LinuxDlssDefaultsService(fx.Dlss, fx.Store, new NotRunning());

        var targets = defaults.DetectTargets(fx.Game);
        Assert.Equal(new[] { gameDlssg }, targets.DlssgPaths);
        Assert.Equal(new[] { fx.Bin }, targets.StreamlineFolders);

        // Quick Apply with an older FG default (and a Streamline default).
        var quick = await defaults.ApplyDefaultsAsync(fx.Game, new LinuxDlssVersionDefaults(FrameGeneration: "310.1.0", Streamline: "2.12.0"));
        Assert.Equal(0, quick.FailedCount);
        Assert.Equal(Dlssg(0x50), File.ReadAllBytes(gameDlssg));
        Assert.Equal(Dlssg(0x21), File.ReadAllBytes(gameInterposer));
        Assert.Equal(stack, Snapshot(Path.Combine(fx.Bin, "OptiScaler")));

        // Restore All.
        var restored = await defaults.RestoreAllAsync(fx.Game);
        Assert.Equal(0, restored.FailedCount);
        Assert.Equal(Dlssg(0x60), File.ReadAllBytes(gameDlssg));
        Assert.Equal(Dlssg(0x61), File.ReadAllBytes(gameInterposer));
        Assert.Equal(stack, Snapshot(Path.Combine(fx.Bin, "OptiScaler")));

        // Batch Deploy, then Batch Restore.
        var batch = await defaults.ApplyBatchAsync([fx.Game], new LinuxDlssVersionDefaults(FrameGeneration: "310.1.0", Streamline: "2.12.0"));
        Assert.Equal(0, batch.FailedCount);
        Assert.Equal(Dlssg(0x50), File.ReadAllBytes(gameDlssg));
        Assert.Equal(stack, Snapshot(Path.Combine(fx.Bin, "OptiScaler")));
        var batchRestore = await defaults.RestoreBatchAsync([fx.Game]);
        Assert.Equal(0, batchRestore.FailedCount);
        Assert.Equal(Dlssg(0x60), File.ReadAllBytes(gameDlssg));
        Assert.Equal(stack, Snapshot(Path.Combine(fx.Bin, "OptiScaler")));

        // Per-game panel flow: swap/restore on the action-time targets of a cached detection.
        Assert.True(defaults.TryPanelTargets(fx.Game, targets, "FG", out var fgKind, out var fgPaths));
        var panelFolders = defaults.PanelStreamlineFolders(fx.Game, targets);
        Assert.True((await fx.Dlss.SwapAsync(fgKind, fgPaths, "310.1.0")).Success);
        Assert.True((await fx.Dlss.RestoreAsync(fgKind, fgPaths)).Success);
        Assert.True((await fx.Dlss.SwapStreamlineAsync(panelFolders, "2.12.0")).Success);
        Assert.True((await fx.Dlss.RestoreStreamlineAsync(panelFolders)).Success);
        Assert.Equal(stack, Snapshot(Path.Combine(fx.Bin, "OptiScaler")));

        if (disableStackFirst)
        {
            var disabled = await fx.Service.SetAdvancedSettingAsync(fx.Game, OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId, "false");
            Assert.True(disabled.Success, disabled.Message);
            Assert.Equal(Dlssg(0x7A), File.ReadAllBytes(stackDlssg));
            Assert.Equal(Dlssg(0x34), File.ReadAllBytes(stackCommon));
        }
        var removed = await fx.Service.RemoveAsync(fx.Game);
        Assert.True(removed.Success, removed.Message);
        Assert.Equal(Dlssg(0x7A), File.ReadAllBytes(stackDlssg));
        Assert.Equal(Dlssg(0x34), File.ReadAllBytes(stackCommon));
        Assert.False(File.Exists(stackDlssg + ".original"));
        Assert.False(File.Exists(stackCommon + ".original"));
        Assert.Null(fx.Store.Find(fx.Game, ManagedComponentKind.OptiScaler));
        Assert.Equal(Dlssg(0x60), File.ReadAllBytes(gameDlssg));
    }

    [Fact]
    public void DetectTargets_DropRecordOwnedFilesOutsideTheOptiScalerFolderAndTheirStreamlineFolder()
    {
        var fx = CreateFixture("owned-targets");
        var ownedNr = Path.Combine(fx.Bin, "nvngx_dlssnr.dll"); // DLSS NR variant runtime, owned by the record
        var gameNr = Path.Combine(fx.Game.InstallPath, "Engine", "nvngx_dlssnr.dll");
        var ownedSlFolder = Path.Combine(fx.Game.InstallPath, "Plugins", "Streamline");
        var gameSlFolder = Path.Combine(fx.Game.InstallPath, "Engine", "Streamline");
        Directory.CreateDirectory(ownedSlFolder);
        Directory.CreateDirectory(gameSlFolder);
        File.WriteAllBytes(ownedNr, Dlssg(0x10));
        File.WriteAllBytes(gameNr, Dlssg(0x11));
        File.WriteAllBytes(Path.Combine(ownedSlFolder, "sl.interposer.dll"), Dlssg(0x12));
        File.WriteAllBytes(Path.Combine(gameSlFolder, "sl.interposer.dll"), Dlssg(0x13));
        var defaults = new LinuxDlssDefaultsService(fx.Dlss, fx.Store, new NotRunning());
        var stale = defaults.DetectTargets(fx.Game); // probe taken before OptiScaler owned those files
        Assert.Contains(ownedNr, stale.DlssnrPaths);
        Assert.Contains(ownedSlFolder, stale.StreamlineFolders);

        var record = fx.Store.Find(fx.Game, ManagedComponentKind.OptiScaler)!;
        record.Files.Add(new LinuxDeployedFile { Path = ownedNr, Sha256 = Sha256(Dlssg(0x10)) });
        record.Files.Add(new LinuxDeployedFile { Path = Path.Combine(ownedSlFolder, "sl.interposer.dll"), Sha256 = Sha256(Dlssg(0x12)) });
        fx.Store.Upsert(record);

        foreach (var targets in new[] { defaults.DetectTargets(fx.Game), defaults.ExcludeOwnedTargets(fx.Game, stale) })
        {
            Assert.Equal(new[] { gameNr }, targets.DlssnrPaths);
            Assert.Equal(new[] { gameSlFolder }, targets.StreamlineFolders);
        }
    }

    [Fact]
    public void Detect_ExcludesOptiScalerFoldersInsideTheGameOnly()
    {
        // A library path that itself contains "OptiScaler" must not hide the game's own DLLs.
        var root = Path.Combine(_root, "OptiScaler", "Library", "Game");
        var own = Path.Combine(root, "bin", "nvngx_dlss.dll");
        var external = Path.Combine(root, "bin", "optiscaler", "nvngx_dlss.dll");
        var streamline = Path.Combine(root, "bin", "OptiScaler", "Streamline", "sl.interposer.dll");
        foreach (var path in new[] { own, external, streamline })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Dlssg(0x01));
        }
        var dlss = new DlssManagerService(new HttpClient(), new TransactionalFileDeploymentService(), Path.Combine(_root, "detect-cache"));

        var result = dlss.Detect(root);

        Assert.Equal(new[] { own }, result.DlssPaths);
        Assert.Empty(result.StreamlineFolders);
    }

    // up-135 re-review minor 3 (replaces a source-text guard): the panel resolves its swap/restore targets through the
    // ownership filter at action time, so a detection cached before OptiScaler owned a file never targets it.
    [Theory]
    [InlineData("SR", DlssComponentKind.SuperResolution)]
    [InlineData("rr", DlssComponentKind.RayReconstruction)]
    [InlineData("FG", DlssComponentKind.FrameGeneration)]
    [InlineData("NR", DlssComponentKind.NeuralRendering)]
    public void PanelTargets_ReapplyTheOwnershipFilterToTheCachedDetection(string component, DlssComponentKind expectedKind)
    {
        var fx = CreateFixture("panel-targets-" + component);
        var owned = new Dictionary<DlssComponentKind, (string Owned, string Game)>();
        foreach (var (fileKind, file) in new[]
                 {
                     (DlssComponentKind.SuperResolution, "nvngx_dlss.dll"), (DlssComponentKind.RayReconstruction, "nvngx_dlssd.dll"),
                     (DlssComponentKind.FrameGeneration, "nvngx_dlssg.dll"), (DlssComponentKind.NeuralRendering, "nvngx_dlssnr.dll")
                 })
        {
            var ownedPath = Path.Combine(fx.Bin, file);
            var gamePath = Path.Combine(fx.Game.InstallPath, "Engine", file);
            Directory.CreateDirectory(Path.GetDirectoryName(gamePath)!);
            File.WriteAllBytes(ownedPath, Dlssg(0x20));
            File.WriteAllBytes(gamePath, Dlssg(0x21));
            owned[fileKind] = (ownedPath, gamePath);
        }
        var ownedSl = Path.Combine(fx.Game.InstallPath, "Plugins", "Streamline");
        var gameSl = Path.Combine(fx.Game.InstallPath, "Engine", "Streamline");
        Directory.CreateDirectory(ownedSl);
        Directory.CreateDirectory(gameSl);
        File.WriteAllBytes(Path.Combine(ownedSl, "sl.interposer.dll"), Dlssg(0x22));
        File.WriteAllBytes(Path.Combine(gameSl, "sl.interposer.dll"), Dlssg(0x23));
        var defaults = new LinuxDlssDefaultsService(fx.Dlss, fx.Store, new NotRunning());
        var cached = defaults.DetectTargets(fx.Game); // the panel's probe, taken before OptiScaler owned those files

        var record = fx.Store.Find(fx.Game, ManagedComponentKind.OptiScaler)!;
        record.Files.Add(new LinuxDeployedFile { Path = owned[expectedKind].Owned, Sha256 = Sha256(Dlssg(0x20)) });
        record.Files.Add(new LinuxDeployedFile { Path = Path.Combine(ownedSl, "sl.interposer.dll"), Sha256 = Sha256(Dlssg(0x22)) });
        fx.Store.Upsert(record);

        Assert.True(defaults.TryPanelTargets(fx.Game, cached, component, out var kind, out var paths));
        Assert.Equal(expectedKind, kind);
        Assert.Equal(new[] { owned[expectedKind].Game }, paths);
        Assert.Equal(new[] { gameSl }, defaults.PanelStreamlineFolders(fx.Game, cached));

        Assert.False(defaults.TryPanelTargets(fx.Game, cached, "Streamline", out _, out var none));
        Assert.Empty(none);
        // No selected game: the cached detection as is (nothing to filter against).
        Assert.True(defaults.TryPanelTargets(null, cached, component, out _, out var unfiltered));
        Assert.Contains(owned[expectedKind].Owned, unfiltered);
    }

    // up-135 re-review minor 1: the Streamline component row (which also gates MFG Ada Unlock) counts only the game's
    // own Streamline, like the DLSS panel — never RHI's OptiScaler/Streamline stack.
    [Fact]
    public void ComponentProbe_StreamlineRowIgnoresTheOptiScalerStack()
    {
        var fx = CreateFixture("probe-streamline");
        var stackDir = Path.Combine(fx.Bin, "OptiScaler", "Streamline");
        Directory.CreateDirectory(stackDir);
        var stack = new[] { "sl.interposer.dll", "sl.common.dll", "nvngx_dlssg.dll" }.Select(name => Path.Combine(stackDir, name)).ToArray();
        foreach (var path in stack) File.WriteAllBytes(path, Dlssg(0x30));

        var onlyStack = ComponentProbe.Probe(fx.Game, stack).Single(c => c.Kind == ManagedComponentKind.Streamline);
        Assert.NotEqual(ComponentAvailability.Installed, onlyStack.Availability);

        var own = Path.Combine(fx.Bin, "sl.interposer.dll");
        File.WriteAllBytes(own, Dlssg(0x31));
        var withOwn = ComponentProbe.Probe(fx.Game, [.. stack, own]).Single(c => c.Kind == ManagedComponentKind.Streamline);
        Assert.Equal(ComponentAvailability.Installed, withOwn.Availability);
    }

    [Fact]
    public void OptiScalerDialogInfoLine_IsPresentedInPortuguese()
    {
        var fx = CreateFixture("pt-info");
        var warning = fx.Service.GetAdvancedSettings(fx.Game).Warning!;

        var presented = UiLocalizationService.PresentFor("pt-BR", warning);

        Assert.NotEqual(warning, presented);
        Assert.NotEqual(UiLocalizationService.UnlocalizedFallbackFor("pt-BR"), presented);
    }

    private static Dictionary<string, string> Snapshot(string directory)
        => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);

    private sealed class NotRunning : IGameRunningProbe
    {
        public bool IsRunning(DetectedGame game) => false;
    }
}
