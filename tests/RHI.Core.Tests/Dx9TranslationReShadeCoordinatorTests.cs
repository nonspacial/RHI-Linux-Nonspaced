using System.Text;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class Dx9TranslationReShadeCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-dx9-rs-migration-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Migration_MovesManagedReShadeToDxgiAndRestoresItToD3D9()
    {
        var bin = Path.Combine(_root, "game");
        var prefix = Path.Combine(_root, "prefix");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(prefix);
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n");
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x14c, 128 * 1024);
        var d3d9 = Path.Combine(bin, "d3d9.dll");
        PeArchitectureDetectorTests.WritePe(d3d9, 0x14c, 2 * 1024 * 1024);
        using (var stream = new FileStream(d3d9, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Position = 4096;
            stream.Write(Encoding.ASCII.GetBytes("ReShade reshade.me crosire"));
        }

        var game = new DetectedGame
        {
            Name = "DX9 Game",
            InstallPath = bin,
            BinaryDirectory = bin,
            ExecutablePath = exe,
            PrefixPath = prefix,
            GraphicsApi = GraphicsApi.DirectX9,
            Source = "Test",
            Runner = "Wine"
        };
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json"));
        var overrides = new WineDllOverrideService();
        var d3d9Override = overrides.EnsureNativeBuiltin(prefix, "d3d9", Path.GetFileName(exe));
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game),
            GameName = game.Name,
            Component = ManagedComponentKind.ReShade,
            BinaryDirectory = bin,
            WineOverride = d3d9Override,
            Files = [new LinuxDeployedFile
            {
                Path = d3d9,
                ExistedBeforeInstall = false,
                Sha256 = Dx9TranslationReShadeCoordinator.ComputeSha256(d3d9)
            }]
        });
        var coordinator = new Dx9TranslationReShadeCoordinator(records, overrides);

        var state = await coordinator.MigrateAsync(game);

        var dxgi = Path.Combine(bin, "dxgi.dll");
        Assert.False(File.Exists(d3d9));
        Assert.True(File.Exists(dxgi));
        var migrated = records.Find(game, ManagedComponentKind.ReShade)!;
        Assert.Equal(dxgi, migrated.Files.Single(f => f.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Path);
        Assert.Equal("dxgi", migrated.WineOverride!.DllName, ignoreCase: true);

        await coordinator.RestoreAsync(game, state);

        Assert.True(File.Exists(d3d9));
        Assert.False(File.Exists(dxgi));
        var restored = records.Find(game, ManagedComponentKind.ReShade)!;
        Assert.Equal(d3d9, restored.Files.Single(f => f.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Path);
        Assert.Equal("d3d9", restored.WineOverride!.DllName, ignoreCase: true);
    }

    // w2 wine review, minor 2 (N4): a restoration failing after it restored the translated (dxgi) override puts RHI's
    // value back, since the record keeps the translated state.
    [Fact]
    public async Task RestoreFailingAfterTheOverrideWasRestored_PutsTheTranslatedOverrideBack()
    {
        var bin = Path.Combine(_root, "game-fails");
        var prefix = Path.Combine(_root, "prefix-fails");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(prefix);
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n");
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x14c, 128 * 1024);
        var d3d9 = Path.Combine(bin, "d3d9.dll");
        PeArchitectureDetectorTests.WritePe(d3d9, 0x14c, 2 * 1024 * 1024);
        var game = new DetectedGame
        {
            Name = "DX9 Game", InstallPath = bin, BinaryDirectory = bin, ExecutablePath = exe, PrefixPath = prefix,
            GraphicsApi = GraphicsApi.DirectX9, Source = "Test", Runner = "Wine"
        };
        var ledger = Path.Combine(_root, "records-fails.json");
        var records = new LinuxDeploymentRecordStore(ledger);
        var overrides = new WineDllOverrideService();
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.ReShade,
            BinaryDirectory = bin, WineOverride = overrides.EnsureNativeBuiltin(prefix, "d3d9", "Game.exe"),
            Files = [new LinuxDeployedFile { Path = d3d9, Sha256 = Dx9TranslationReShadeCoordinator.ComputeSha256(d3d9) }]
        });
        var coordinator = new Dx9TranslationReShadeCoordinator(records, overrides);
        var state = await coordinator.MigrateAsync(game);
        var dxgi = Path.Combine(bin, "dxgi.dll");
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(prefix, "user.reg")), "dxgi", "Game.exe"));

        using (LedgerWriteBlocker.Block(records, ledger))
            await Assert.ThrowsAnyAsync<IOException>(() => coordinator.RestoreAsync(game, state));

        Assert.True(File.Exists(dxgi));
        Assert.False(File.Exists(d3d9));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(prefix, "user.reg")), "dxgi", "Game.exe"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
