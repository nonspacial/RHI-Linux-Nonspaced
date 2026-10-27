using System.IO.Compression;
using System.Net;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #249 items 9 and 10 (upstream 2.8.8 <c>b8094e6</c>): a Luma archive member that is absolute, or uses <c>..</c> even
/// where it would land back inside the game, is refused before anything is staged; Luma removal deletes only the files
/// its record lists (a file beside them stays); a Luma update writes one Update row in the update history.
/// </summary>
public sealed partial class LumaFrameworkDeploymentServiceTests
{
    [Theory]
    [InlineData("/tmp/rhi-pin288-absolute.txt", "Absolute archive entry is not allowed: ")]
    [InlineData("C:/Windows/rhi-pin288.txt", "Absolute archive entry is not allowed: ")]
    [InlineData("Shaders/../inside.txt", "Unsafe archive entry: ")]          // lands inside: only the ".." rule refuses it
    [InlineData("../Luma-escape.addon64", "Unsafe archive entry: ")]        // add-ons are routed by file name
    public async Task Pin288_ExtractionPlan_RefusesAbsoluteAndDotDotMembers(string member, string refusal)
    {
        var game = CreateGame();
        var zipPath = Path.Combine(_root, "pin288-" + Guid.NewGuid().ToString("N") + ".zip");
        using (var file = File.Create(zipPath))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            WriteEntry(zip, member, new byte[64]);
        var staging = Directory.CreateDirectory(Path.Combine(_root, "pin288-staging-" + Guid.NewGuid().ToString("N"))).FullName;

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            LumaFrameworkDeploymentService.BuildExtractionPlanAsync(zipPath, game.BinaryDirectory!, staging, CancellationToken.None));

        Assert.StartsWith(refusal, error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(staging));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "inside.txt")));
    }

    [Fact]
    public async Task Pin288_Remove_DeletesOnlyTheRecordedFiles()
    {
        var game = CreateGame();
        var records = NewRecords("pin288-remove-records.json");
        SeedManagedReShade(records, game);
        File.WriteAllText(Path.Combine(game.BinaryDirectory!, "ReShade.ini"), "[ADDON]\nAddonPath=.\\addons\n");
        var zip = MakeLumaZip();
        using var http = BuildHttp(zip, Sha256(zip));
        var service = new LumaFrameworkDeploymentService(http, records, Path.Combine(_root, "cache-pin288-remove"));
        Assert.True((await service.InstallAsync(game)).Success);
        // The user's own files next to Luma's (same folders, Luma-like names) are not Luma's.
        var userShader = Path.Combine(game.BinaryDirectory!, "Shaders", "Luma-user.fx");
        var userAddon = Path.Combine(game.BinaryDirectory!, "addons", "Luma-user.addon64");
        File.WriteAllText(userShader, "user");
        File.WriteAllText(userAddon, "user");
        var recorded = records.Find(game, ManagedComponentKind.LumaFramework)!.Files.Select(file => file.Path).ToArray();

        var remove = await service.RemoveAsync(game);

        Assert.True(remove.Success, remove.Message);
        Assert.All(recorded, path => Assert.False(File.Exists(path), path));
        Assert.Equal(recorded.Order(StringComparer.Ordinal), remove.ChangedFiles!.Order(StringComparer.Ordinal));
        Assert.True(File.Exists(userShader));
        Assert.True(File.Exists(userAddon));
    }

    // A ledger entry that names a file outside the game (a hand-edited or damaged deployments.json): removal must not
    // delete it. Upstream 2.8.8 guards its Luma uninstall with SafeCombine against the game folder.
    [Fact(Skip = "#249: Luma removal deletes a recorded file outside the game folder (no containment check at removal; w12-pins progress file, Decision needed)")]
    public async Task Pin288_Remove_NeverDeletesARecordedFileOutsideTheGame()
    {
        var game = CreateGame();
        var records = NewRecords("pin288-outside-records.json");
        SeedManagedReShade(records, game);
        File.WriteAllText(Path.Combine(game.BinaryDirectory!, "ReShade.ini"), "[ADDON]\nAddonPath=.\\addons\n");
        var zip = MakeLumaZip();
        using var http = BuildHttp(zip, Sha256(zip));
        var service = new LumaFrameworkDeploymentService(http, records, Path.Combine(_root, "cache-pin288-outside"));
        Assert.True((await service.InstallAsync(game)).Success);
        var outside = Path.Combine(_root, "pin288-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outside, "not the game's");
        var record = records.Find(game, ManagedComponentKind.LumaFramework)!;
        record.Files.Add(new LinuxDeployedFile { Path = outside, Sha256 = await FileHash.Sha256HexAsync(outside, CancellationToken.None) });
        records.Upsert(record);

        await service.RemoveAsync(game);

        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task Pin288_ManagedUpdate_IsLoggedInTheUpdateHistory()
    {
        var game = CreateGame();
        var records = NewRecords("pin288-history-records.json");
        SeedManagedReShade(records, game);
        File.WriteAllText(Path.Combine(game.BinaryDirectory!, "ReShade.ini"), "[ADDON]\nAddonPath=.\\addons\n");
        var first = MakeLumaZip();
        var second = MakeLumaZip("Shaders/Luma-new.fx");
        var release = (Tag: "latest-630", Zip: first);
        using var http = new HttpClient(new FakeHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url == LumaFrameworkDeploymentService.LatestReleaseApiUrl)
                return JsonResponse("[" + ReleaseJson(release.Tag, $"https://github.com/rhi-tests/{release.Tag}/luma.zip", Sha256(release.Zip)) + "]");
            if (url == $"https://github.com/rhi-tests/{release.Tag}/luma.zip")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(release.Zip) };
            throw new InvalidOperationException("Unexpected URL: " + url);
        }));
        var service = new LumaFrameworkDeploymentService(http, records, Path.Combine(_root, "cache-pin288-history"));
        Assert.True((await service.InstallAsync(game)).Success);
        var before = records.Find(game, ManagedComponentKind.LumaFramework)!.Version;
        var log = new ComponentUpdateLogStore(Path.Combine(_root, "pin288-update-log.json"));
        var managed = new LinuxManagedComponentService(records, reshade: null!, addonManager: null!, renodx: null!, renodxUpgrade: null!,
            optiscaler: null!, relimiter: null!, displayCommander: null!, mfgAdaUnlock: null!, dlssg2030: null!,
            dlssEnablerStandalone: null!, rtx40Mfg: null!, dlss5Feeder: null!, dlss5Bridge: null!, d3dCompilerTools: null!,
            ultimateAsiLoader: null!, shortFuse: null!, dofFix: null!, reFramework: null!, lumaFramework: service, lumaPolicy: null!,
            updateLog: log);
        release = ("latest-631", second);

        var update = await managed.UpdateManagedAsync(game, ManagedComponentKind.LumaFramework, []);

        Assert.True(update.Success, update.Message);
        var after = records.Find(game, ManagedComponentKind.LumaFramework)!.Version;
        Assert.NotEqual(before, after);
        var row = Assert.Single(log.GetAll());
        Assert.Equal((ManagedComponentKind.LumaFramework, ComponentUpdateAction.Update, true, before, after),
            (row.Component, row.Action, row.Success, row.FromVersion, row.ToVersion));
    }
}
