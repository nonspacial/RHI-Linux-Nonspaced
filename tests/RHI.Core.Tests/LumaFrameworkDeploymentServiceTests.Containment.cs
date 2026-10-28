using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// Pins gap 2 (w12 coordinator, moved from the pins lane; upstream 2.8.8 <c>b8094e6</c> guards Luma uninstall with
/// <c>SafeCombine</c>): Luma removal and update delete a recorded file only inside the game folder or ReShade's add-on
/// folder (<c>AddonPath</c>, where Luma's add-on goes, even when it points outside the game). A ledger entry naming
/// anything else (a hand-edited or damaged <c>deployments.json</c>) is left alone and never blocks the removal.
/// </summary>
public sealed partial class LumaFrameworkDeploymentServiceTests
{
    private async Task<(DetectedGame Game, LinuxDeploymentRecordStore Records, LumaFrameworkDeploymentService Service)> InstalledLumaAsync(string name, string addonPath = ".\\addons")
    {
        var game = CreateGame();
        var records = NewRecords(name + "-records.json");
        SeedManagedReShade(records, game);
        File.WriteAllText(Path.Combine(game.BinaryDirectory!, "ReShade.ini"), $"[ADDON]\nAddonPath={addonPath}\n");
        var zip = MakeLumaZip();
        var http = BuildHttp(zip, Sha256(zip));
        var service = new LumaFrameworkDeploymentService(http, records, Path.Combine(_root, "cache-" + name));
        var install = await service.InstallAsync(game);
        Assert.True(install.Success, install.Message);
        return (game, records, service);
    }

    private async Task<string> RecordOutsideFileAsync(DetectedGame game, LinuxDeploymentRecordStore records, string? changedSince = null)
    {
        var outside = Path.Combine(_root, "outside-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outside, "not the game's");
        var record = records.Find(game, ManagedComponentKind.LumaFramework)!;
        record.Files.Add(new LinuxDeployedFile { Path = outside, Sha256 = await FileHash.Sha256HexAsync(outside, CancellationToken.None) });
        records.Upsert(record);
        if (changedSince is not null) File.WriteAllText(outside, changedSince);
        return outside;
    }

    [Fact]
    public async Task Remove_NeverDeletesARecordedFileOutsideTheGame_AndRemovesLumasOwn()
    {
        var (game, records, service) = await InstalledLumaAsync("outside-remove");
        var own = records.Find(game, ManagedComponentKind.LumaFramework)!.Files.Select(file => file.Path).ToArray();
        var outside = await RecordOutsideFileAsync(game, records);

        var remove = await service.RemoveAsync(game);

        Assert.True(remove.Success, remove.Message);
        Assert.True(File.Exists(outside));
        Assert.Equal("not the game's", File.ReadAllText(outside));
        Assert.DoesNotContain(outside, remove.ChangedFiles!);
        Assert.All(own, path => Assert.False(File.Exists(path), path));
        Assert.Null(records.Find(game, ManagedComponentKind.LumaFramework));
    }

    [Fact]
    public async Task Remove_AnOutsideEntryChangedSinceInstall_NeitherBlocksTheRemoval_NorIsTouched()
    {
        var (game, records, service) = await InstalledLumaAsync("outside-changed");
        var outside = await RecordOutsideFileAsync(game, records, changedSince: "the user's own text");

        var remove = await service.RemoveAsync(game);

        Assert.True(remove.Success, remove.Message);
        Assert.Equal("the user's own text", File.ReadAllText(outside));
    }

    [Fact]
    public async Task Remove_NeverDeletesARecordedFolderOutsideTheGame()
    {
        var (game, records, service) = await InstalledLumaAsync("outside-folder");
        var folder = Directory.CreateDirectory(Path.Combine(_root, "outside-empty-" + Guid.NewGuid().ToString("N"))).FullName;
        var record = records.Find(game, ManagedComponentKind.LumaFramework)!;
        record.DirectoriesCreatedByRhi.Add(folder);
        records.Upsert(record);

        Assert.True((await service.RemoveAsync(game)).Success);

        Assert.True(Directory.Exists(folder));
    }

    // Luma's add-on goes to ReShade's AddonPath; an AddonPath outside the game folder is still Luma's place.
    [Fact]
    public async Task Remove_DeletesLumasAddon_InAnAddonPathOutsideTheGameFolder()
    {
        var shared = "shared-addons-" + Guid.NewGuid().ToString("N");
        var (game, records, service) = await InstalledLumaAsync("outside-addonpath", addonPath: "..\\" + shared);
        var addon = Path.Combine(_root, shared, "Luma.addon64");
        Assert.Contains(records.Find(game, ManagedComponentKind.LumaFramework)!.Files, file => file.Path == addon);
        Assert.True(File.Exists(addon));

        var remove = await service.RemoveAsync(game);

        Assert.True(remove.Success, remove.Message);
        Assert.False(File.Exists(addon));
    }

    [Fact]
    public async Task Update_NeverDeletesARecordedFileOutsideTheGame()
    {
        var (game, records, service) = await InstalledLumaAsync("outside-update");
        var outside = await RecordOutsideFileAsync(game, records);

        var reinstall = await service.InstallAsync(game);

        Assert.True(reinstall.Success, reinstall.Message);
        Assert.True(File.Exists(outside));
    }

    // w12 follow-up (re-review luma N5): the update drops the outside entry from the new record and says so in one log line,
    // as removal does (that line names the path, never deletes it).
    [Fact]
    public async Task Update_LogsOneLine_ForAnOutsideEntryItDropsFromTheRecord()
    {
        var (game, records, service) = await InstalledLumaAsync("outside-update-log");
        var outside = await RecordOutsideFileAsync(game, records);
        var log = Path.Combine(_root, "luma-update-" + Guid.NewGuid().ToString("N") + ".log");

        ComponentOperationResult reinstall;
        using (AppLog.UseInThisFlow(new AppLog(log)))
            reinstall = await service.InstallAsync(game);

        Assert.True(reinstall.Success, reinstall.Message);
        Assert.True(File.Exists(outside));
        Assert.DoesNotContain(records.Find(game, ManagedComponentKind.LumaFramework)!.Files, file => file.Path == outside);
        var lines = File.Exists(log) ? File.ReadAllLines(log).Where(line => line.Contains(outside, StringComparison.Ordinal)).ToArray() : [];
        Assert.Single(lines);
        Assert.Contains("[luma]", lines[0], StringComparison.Ordinal);
    }

    // The DX9 translation's own files (D3D9.dll, dgVoodoo.conf) are taken from the record too: an entry of that name
    // outside the game is never the one removed; the game's own is.
    [Fact]
    public async Task Dx9Remove_TakesTheGamesOwnTranslationFile_NeverAnOutsideOneOfTheSameName()
    {
        var (service, records, game, _, _, _, _, package) = CreateDx9LumaFixture("outside-dx9");
        Assert.True((await service.InstallLocalPackageAsync(game, package)).Success);
        var record = records.Find(game, ManagedComponentKind.LumaFramework)!;
        var own = record.Files.First(file => Path.GetFileName(file.Path).Equals("dgVoodoo.conf", StringComparison.OrdinalIgnoreCase)).Path;
        var outsideDir = Directory.CreateDirectory(Path.Combine(_root, "outside-dx9-elsewhere")).FullName;
        var outside = Path.Combine(outsideDir, "dgVoodoo.conf");
        File.WriteAllText(outside, "someone else's dgVoodoo settings");
        record.Files.Insert(0, new LinuxDeployedFile { Path = outside, Sha256 = await FileHash.Sha256HexAsync(outside, CancellationToken.None) });
        records.Upsert(record);

        var remove = await service.RemoveAsync(game);

        Assert.True(remove.Success, remove.Message);
        Assert.Equal("someone else's dgVoodoo settings", File.ReadAllText(outside));
        Assert.False(File.Exists(own));
    }
}
