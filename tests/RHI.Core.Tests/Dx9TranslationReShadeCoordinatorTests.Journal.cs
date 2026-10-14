using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// w3/journal: the DX9 migration's rename d3d9.dll -> dxgi.dll (and back) is journaled (#23): a crash before the ReShade
// record is saved puts the proxy back under its earlier name at the next start; the empty catch around the in-process
// rename back is gone (#31).
public sealed partial class Dx9TranslationReShadeCoordinatorTests
{
    private (DetectedGame Game, LinuxDeploymentRecordStore Records, string Ledger, string D3d9, string Dxgi, byte[] Proxy) Dx9Game(string name)
    {
        var bin = Path.Combine(_root, name, "game");
        var prefix = Path.Combine(_root, name, "prefix");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(prefix);
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n");
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x14c, 128 * 1024);
        var d3d9 = Path.Combine(bin, "d3d9.dll");
        PeArchitectureDetectorTests.WritePe(d3d9, 0x14c, 512 * 1024);
        File.AppendAllText(d3d9, "ReShade reshade.me crosire " + name);
        var game = new DetectedGame
        {
            Name = "DX9 Game " + name, InstallPath = bin, BinaryDirectory = bin, ExecutablePath = exe, PrefixPath = prefix,
            GraphicsApi = GraphicsApi.DirectX9, Source = "Test", Runner = "Wine"
        };
        var ledger = Path.Combine(_root, name, "records.json");
        var records = new LinuxDeploymentRecordStore(ledger);
        var d3d9Override = new WineDllOverrideService(new WineRegistryEditor(_ => false)).EnsureNativeBuiltin(prefix, "d3d9", "Game.exe");
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.ReShade,
            BinaryDirectory = bin, WineOverride = d3d9Override,
            Files = [new LinuxDeployedFile { Path = d3d9, ExistedBeforeInstall = false, Sha256 = FileHash.Sha256Hex(d3d9) }]
        });
        return (game, records, ledger, d3d9, Path.Combine(bin, "dxgi.dll"), File.ReadAllBytes(d3d9));
    }

    private static string? Dx9Override(DetectedGame game, string dll)
        => WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(game.PrefixPath!, "user.reg")), dll, "Game.exe");

    [Fact]
    public async Task Migration_CrashWhileSavingTheRecord_NextStartPutsTheProxyBackAtD3D9()
    {
        var (game, records, ledger, d3d9, dxgi, proxy) = Dx9Game("crash-migrate");
        var journalDir = Path.Combine(_root, "crash-migrate", "journal");
        var (journal, overrides) = SimulatedDeath.NewJournal(journalDir, records);
        using var death = new SimulatedDeath(journal);
        records.SaveFaultForTesting = death.Die;

        var (died, _) = await death.RunUntilDeathAsync(() => new Dx9TranslationReShadeCoordinator(records, overrides, journal).MigrateAsync(game));
        Assert.True(died);
        Assert.True(File.Exists(dxgi));
        Assert.False(File.Exists(d3d9));

        var report = await SimulatedDeath.RecoverAsync(journalDir, ledger);

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.Equal(proxy, File.ReadAllBytes(d3d9));
        Assert.False(File.Exists(dxgi));
        Assert.Null(Dx9Override(game, "dxgi"));
        Assert.Equal("native,builtin", Dx9Override(game, "d3d9"));
        Assert.Empty(Directory.GetFiles(game.BinaryDirectory!, "*.rhi-*"));
    }

    [Fact]
    public async Task Restore_CrashWhileSavingTheRecord_NextStartPutsTheProxyBackAtDxgi()
    {
        var (game, records, ledger, d3d9, dxgi, proxy) = Dx9Game("crash-restore");
        var journalDir = Path.Combine(_root, "crash-restore", "journal");
        var (journal, overrides) = SimulatedDeath.NewJournal(journalDir, records);
        var coordinator = new Dx9TranslationReShadeCoordinator(records, overrides, journal);
        var state = await coordinator.MigrateAsync(game);
        using var death = new SimulatedDeath(journal);
        records.SaveFaultForTesting = death.Die;

        var (died, _) = await death.RunUntilDeathAsync(async () => { await coordinator.RestoreAsync(game, state); return 0; });
        Assert.True(died);
        Assert.True(File.Exists(d3d9));
        Assert.Null(Dx9Override(game, "dxgi"));

        var report = await SimulatedDeath.RecoverAsync(journalDir, ledger);

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.Equal(proxy, File.ReadAllBytes(dxgi));
        Assert.False(File.Exists(d3d9));
        Assert.Equal("native,builtin", Dx9Override(game, "dxgi")); // the record still owns the translated override
    }

    [Fact]
    public async Task Migration_RecordSaveFails_PutsTheProxyBackAtD3D9InProcess()
    {
        var (game, records, _, d3d9, dxgi, proxy) = Dx9Game("save-fails");
        var overrides = new WineDllOverrideService(new WineRegistryEditor(_ => false));
        // The migration's own save fails once; the rollback's save of the earlier record goes through.
        records.SaveFaultForTesting = () => { records.SaveFaultForTesting = null; return new IOException("disk full"); };

        await Assert.ThrowsAsync<IOException>(() => new Dx9TranslationReShadeCoordinator(records, overrides).MigrateAsync(game));

        Assert.Equal(proxy, File.ReadAllBytes(d3d9));
        Assert.False(File.Exists(dxgi));
        Assert.Null(Dx9Override(game, "dxgi"));
        Assert.Empty(Directory.GetFiles(game.BinaryDirectory!, "*.rhi-*"));
        Assert.Equal(d3d9, records.Find(game, ManagedComponentKind.ReShade)!.Files.Single().Path);
    }
}
