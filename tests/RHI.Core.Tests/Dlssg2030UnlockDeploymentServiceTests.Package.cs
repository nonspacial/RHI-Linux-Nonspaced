using System.IO.Compression;
using System.Text;
using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// #261 (upstream 2.8.9 68856af): since 0.4.x the mod is a release package (dlssg-release-0.4.2-x64.zip). RHI installs the
// proxy build for the chosen name, dlssg_sm86.ini and the whole dlssg_sm86/ folder, records every file and removes them;
// installs made from the old single-DLL path, and winhttp.dll installs, are brought to the package by Update.
public sealed partial class Dlssg2030UnlockDeploymentServiceTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    /// <summary>The entry names of the real 0.4.2 package (Fixtures/dlssg-release-0.4.2-x64.files.tsv).</summary>
    private static string[] RealPackageEntries()
        => File.ReadAllLines(Fixture("dlssg-release-0.4.2-x64.files.tsv"))
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split('\t')[1])
            .ToArray();

    private static ZipArchive ZipOf(params string[] names)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var name in names)
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write([1, 2, 3]);
            }
        stream.Position = 0;
        return new ZipArchive(stream, ZipArchiveMode.Read);
    }

    [Fact]
    public void RealPackage_GivesTheGameOnlyTheProxyBuildsTheIniAndTheModFolder()
    {
        using var zip = ZipOf(RealPackageEntries());

        var planned = Dlssg2030UnlockDeploymentService.PlanPackageEntries(zip).Select(entry => entry.Relative).Order(StringComparer.Ordinal);

        Assert.Equal(
        [
            "alternatives/d3d12.dll", "alternatives/dbghelp.dll", "alternatives/dinput8.dll", "alternatives/dxgi.dll", "alternatives/winmm.dll",
            "dlssg_sm86.ini",
            "dlssg_sm86/backend/sm86_backend.dll", "dlssg_sm86/kernels/fg_kernels.dll", "dlssg_sm86/runtime/nvngx_dlssg.dll",
            "dlssg_sm86/sr/nvngx_dlss.dll", "dlssg_sm86/sr/sr_kernels.dll", "dlssg_sm86/xefg/xefg_kernels.dll",
            "version.dll"
        ], planned);
        // The real 0.4.2 INI meets the contract (no Router any more) and the 0.4.2 ProxyNames all have a build in the package.
        Dlssg2030UnlockDeploymentService.ValidateIniText(File.ReadAllText(Fixture("dlssg_sm86-0.4.2.ini")));
        Assert.All(Dlssg2030UnlockDeploymentService.ProxyNames, name => Assert.Contains(Dlssg2030UnlockDeploymentService.PackagePathFor(name), RealPackageEntries()));
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("dlssg_sm86/../../evil.dll")]
    [InlineData("/etc/evil.dll")]
    [InlineData("dlssg_sm86//backend/x.dll")]
    [InlineData(@"dlssg_sm86\..\..\evil.dll")]
    public void Package_WithAnEntryLeavingItsFolder_IsRefused(string bad)
    {
        using var zip = ZipOf([.. RealPackageEntries(), bad]);

        var error = Assert.Throws<InvalidDataException>(() => Dlssg2030UnlockDeploymentService.PlanPackageEntries(zip));
        Assert.StartsWith(Dlssg2030UnlockDeploymentService.PackageFileMissingPrefix, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("version.dll")]
    [InlineData("dlssg_sm86.ini")]
    [InlineData("dlssg_sm86/runtime/nvngx_dlssg.dll")]
    [InlineData("dlssg_sm86/backend/sm86_backend.dll")]
    public void Package_WithoutAFileTheModNeeds_IsRefused(string missing)
    {
        using var zip = ZipOf(RealPackageEntries().Where(name => name != missing).ToArray());

        var error = Assert.Throws<InvalidDataException>(() => Dlssg2030UnlockDeploymentService.PlanPackageEntries(zip));
        Assert.Equal(Dlssg2030UnlockDeploymentService.PackageFileMissingPrefix + missing, error.Message);
    }

    [Fact]
    public void ReleaseAsset_IsTheTagsPackage_FromTodaysRelease_NeverAnotherZip()
    {
        using var release = JsonDocument.Parse(File.ReadAllText(Fixture("dlssg-release-latest-0.4.2.json")));
        var tag = release.RootElement.GetProperty("tag_name").GetString()!;
        var names = release.RootElement.GetProperty("assets").EnumerateArray().Select(asset => asset.GetProperty("name").GetString()!).ToArray();

        Assert.Equal("dlssg-release-0.4.2-x64.zip", Dlssg2030UnlockDeploymentService.SelectPackageAssetName(names, tag));
        // An alternative-runtime build beside it (upstream skips "-310.9" builds) is never taken.
        Assert.Equal("dlssg-release-0.4.2-x64.zip",
            Dlssg2030UnlockDeploymentService.SelectPackageAssetName(["dlssg-release-0.4.2-310.9-x64.zip", "dlssg-release-0.4.2-x64.zip"], "0.4.2"));
        Assert.Equal("dlssg-release-0.4.3-x64.zip", Dlssg2030UnlockDeploymentService.SelectPackageAssetName(["dlssg-release-0.4.3-x64.zip"], "v0.4.3"));
        foreach (var refused in new[] { Array.Empty<string>(), ["dlssg-release-0.4.1-x64.zip"], ["dlssg-release-0.4.2-310.9-x64.zip"] })
        {
            var error = Assert.Throws<InvalidDataException>(() => Dlssg2030UnlockDeploymentService.SelectPackageAssetName(refused, "0.4.2"));
            Assert.Equal(Dlssg2030UnlockDeploymentService.NoPackagePrefix + "0.4.2", error.Message);
        }
    }

    [Theory]
    [InlineData("version.dll")]
    [InlineData("winmm.dll")]
    [InlineData("dbghelp.dll")]
    [InlineData("dinput8.dll")]
    [InlineData("dxgi.dll")]
    [InlineData("d3d12.dll")]
    public async Task EachDllName_InstallsTheModsBuildForThatName(string proxyName)
    {
        using var fixture = CreateGame("name-" + proxyName, 0x8664, GraphicsApi.DirectX12);
        var handler = new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x42), UpstreamIni());
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));

        var result = await CreateService(fixture, http, records).InstallAsync(fixture.Game, Dlssg2030UnlockDeploymentService.GpuGenRtx20, proxyName);

        Assert.True(result.Success, result.Message);
        Assert.Equal(handler.Build(proxyName), File.ReadAllBytes(Path.Combine(fixture.Bin, proxyName)));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(
            File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), Path.GetFileNameWithoutExtension(proxyName), "Game.exe"));
    }

    [Fact]
    public async Task Install_OverAModFolderFileThatIsNotRhis_IsRefusedBeforeAnyChange()
    {
        using var fixture = CreateGame("mod-collision", 0x8664, GraphicsApi.DirectX12);
        var users = Path.Combine(fixture.Bin, "dlssg_sm86", "backend", "sm86_backend.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(users)!);
        File.WriteAllText(users, "copied by hand");
        using var http = new HttpClient(new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x43), UpstreamIni()));
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));

        var result = await CreateService(fixture, http, records).InstallAsync(fixture.Game, Dlssg2030UnlockDeploymentService.GpuGenRtx20, "version.dll");

        Assert.False(result.Success);
        Assert.Equal(Dlssg2030UnlockDeploymentService.PackageFileCollisionPrefix + users, result.Message);
        Assert.Equal("copied by hand", File.ReadAllText(users));
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "version.dll")));
        Assert.Null(WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "version", "Game.exe"));
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.Dlssg2030Unlock));
    }

    [Fact]
    public async Task Remove_TakesEveryPackageFileAndTheModsLogs_AndLeavesNoFolder()
    {
        var (fixture, _, service, records) = await InstalledAsync("remove-package");
        var logs = Path.Combine(fixture.Bin, "dlssg_sm86", "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "dlssg_sm86.log"), "the mod's own log");

        var remove = await service.RemoveAsync(fixture.Game);

        Assert.True(remove.Success, remove.Message);
        Assert.Equal(["Game.exe"], Directory.GetFiles(fixture.Bin).Select(Path.GetFileName));
        Assert.False(Directory.Exists(Path.Combine(fixture.Bin, "dlssg_sm86")));
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.Dlssg2030Unlock));
        Assert.Null(Override(fixture, "winmm"));
    }

    [Fact]
    public async Task Remove_LeavesAPackageFileChangedOutsideRhi_AndNamesIt()
    {
        var (fixture, _, service, records) = await InstalledAsync("remove-changed-mod");
        var changed = Path.Combine(fixture.Bin, "dlssg_sm86", "sr", "sr_kernels.dll");
        File.WriteAllText(changed, "the user's own build");

        var remove = await service.RemoveAsync(fixture.Game);

        Assert.True(remove.Success, remove.Message);
        Assert.EndsWith(ProxylessRecordRemoval.NotDeletedPrefix + changed, remove.Message, StringComparison.Ordinal);
        Assert.Equal("the user's own build", File.ReadAllText(changed));
        Assert.Equal([changed], Directory.GetFiles(Path.Combine(fixture.Bin, "dlssg_sm86"), "*", SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "winmm.dll")));
        Assert.Null(records.Find(fixture.Game, ManagedComponentKind.Dlssg2030Unlock));
    }

    [Fact]
    public async Task RecordWithoutProxyName_Remove_AlsoTakesThePackageFiles()
    {
        var (fixture, _, service, records) = await InstalledAsync("proxyless-package");
        DropProxyName(records, fixture.Game);

        var remove = await service.RemoveAsync(fixture.Game);

        Assert.True(remove.Success, remove.Message);
        Assert.Equal("RTX 20 FG Unlock" + ProxylessRecordRemoval.RemovedSuffix, remove.Message);
        Assert.False(Directory.Exists(Path.Combine(fixture.Bin, "dlssg_sm86")));
        Assert.Equal(["Game.exe"], Directory.GetFiles(fixture.Bin).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Update_PlacesNewPackageFiles_DropsTheOnesTheReleaseNoLongerShips_AndKeepsTheUsersIni()
    {
        var (fixture, handler, service, records) = await InstalledAsync("update-package");
        var ini = Path.Combine(fixture.Bin, Dlssg2030UnlockDeploymentService.IniFileName);
        File.AppendAllText(ini, "\n; the user's tuning\n");
        var usersIni = File.ReadAllBytes(ini);
        handler.Tag = NewerTag;
        handler.Ini = UpstreamIni().Replace("Level=1", "Level=2", StringComparison.Ordinal);
        handler.ModFiles.Remove("sr/sr_kernels.dll");
        handler.ModFiles["xefg/xefg_kernels.dll"] = [9, 9, 9];
        handler.ModFiles["runtime/nvngx_dlssg.dll"] = [7, 7, 7];

        var check = await service.CheckManagedUpdateAsync(fixture.Game);
        var update = await service.UpdateAsync(fixture.Game);

        Assert.Equal((true, false), (check.UpdateAvailable, check.CouldNotCheck));
        Assert.True(update.Success, update.Message);
        Assert.Equal(Dlssg2030UnlockDeploymentService.UpdatedPrefix + NewerTag + "." + Dlssg2030UnlockDeploymentService.UpdatedIniKeptNote, update.Message);
        Assert.Equal(usersIni, File.ReadAllBytes(ini));
        var mod = Path.Combine(fixture.Bin, "dlssg_sm86");
        Assert.False(File.Exists(Path.Combine(mod, "sr", "sr_kernels.dll")));
        Assert.False(Directory.Exists(Path.Combine(mod, "sr")));
        Assert.Equal([9, 9, 9], File.ReadAllBytes(Path.Combine(mod, "xefg", "xefg_kernels.dll")));
        Assert.Equal([7, 7, 7], File.ReadAllBytes(Path.Combine(mod, "runtime", "nvngx_dlssg.dll")));
        var record = records.Find(fixture.Game, ManagedComponentKind.Dlssg2030Unlock)!;
        Assert.Equal(2 + handler.ModFiles.Count, record.Files.Count);
        Assert.DoesNotContain(record.Files, file => file.Path.EndsWith("sr_kernels.dll", StringComparison.Ordinal));

        // Removal afterwards still takes everything, the user's INI excepted.
        Assert.True((await service.RemoveAsync(fixture.Game)).Success);
        Assert.False(Directory.Exists(mod));
        Assert.Equal(usersIni, File.ReadAllBytes(ini));
    }

    [Fact]
    public async Task PackageFileChangedOutsideRhi_CheckCouldNotCheck_AndUpdateRefuses_LeavingIt()
    {
        var (fixture, handler, service, _) = await InstalledAsync("update-changed-mod");
        var changed = Path.Combine(fixture.Bin, "dlssg_sm86", "kernels", "fg_kernels.dll");
        File.WriteAllText(changed, "changed");
        handler.Tag = NewerTag;

        var check = await service.CheckManagedUpdateAsync(fixture.Game);
        var update = await service.UpdateAsync(fixture.Game);

        Assert.Equal((true, false, true), (check.Managed, check.UpdateAvailable, check.CouldNotCheck));
        Assert.Equal(Dlssg2030UnlockDeploymentService.PackageFileChangedPrefix + changed, check.Message);
        Assert.False(update.Success);
        Assert.Equal(Dlssg2030UnlockDeploymentService.PackageFileChangedPrefix + changed, update.Message);
        Assert.Equal("changed", File.ReadAllText(changed));
    }

    // ── Installs from the single-DLL path (before #261) ─────────────────────────────────────────────────────────

    private const string OldIni = """
        ; Native 0.2.4. Restart the game after changing this file.
        [Compatibility]
        Router=SM75
        KernelImage=PTX

        [FrameGeneration]
        MaxGeneratedFrames=3
        """;

    /// <summary>A record as the single-DLL path left it: version.dll from the repository tree renamed to <paramref name="proxyName"/>, its INI.</summary>
    private static (string Proxy, string Ini) SeedOldInstall(GameFixture fixture, LinuxDeploymentRecordStore records, string proxyName)
    {
        var proxy = Path.Combine(fixture.Bin, proxyName);
        var ini = Path.Combine(fixture.Bin, Dlssg2030UnlockDeploymentService.IniFileName);
        File.WriteAllBytes(proxy, CreatePe(0x8664, 300 * 1024, 0x24));
        File.WriteAllText(ini, OldIni);
        var wine = new WineDllOverrideService().EnsureNativeBuiltin(fixture.Prefix, proxyName, "Game.exe");
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["FG2030.ProxyName"] = proxyName,
            ["FG2030.GpuGeneration"] = Dlssg2030UnlockDeploymentService.GpuGenRtx20,
            ["FG2030.Router"] = "SM75",
            ["FG2030.CommitSha"] = "0123456789abcdef0123456789abcdef01234567",
            ["FG2030.SourceDllSha256"] = "aa",
            ["FG2030.SourceIniSha256"] = "bb",
            ["FG2030.Experimental"] = "1",
        };
        VerifiedDownload.Record(metadata, "AA", digestVerified: true);
        VerifiedDownload.Record(metadata, "BB", digestVerified: true, part: "Ini");
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(fixture.Game),
            GameName = fixture.Game.Name,
            Component = ManagedComponentKind.Dlssg2030Unlock,
            BinaryDirectory = fixture.Bin,
            SourceUrl = Dlssg2030UnlockDeploymentService.RepositoryUrl,
            Version = "0.2.4",
            InstalledAtUtc = DateTime.UtcNow,
            WineOverride = wine,
            Files =
            [
                new LinuxDeployedFile { Path = proxy, Sha256 = DownloadRecordAssert.Sha256Of(proxy), AllowUserChanges = false },
                new LinuxDeployedFile { Path = ini, Sha256 = DownloadRecordAssert.Sha256Of(ini), AllowUserChanges = true },
            ],
            Metadata = metadata
        });
        return (proxy, ini);
    }

    [Fact]
    public async Task OldSingleDllInstall_ReadsAsAnUpdate_AndUpdateInstallsThePackage()
    {
        using var fixture = CreateGame("old-install", 0x8664, GraphicsApi.DirectX12);
        var handler = new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x44), UpstreamIni());
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);
        var (proxy, ini) = SeedOldInstall(fixture, records, "winmm.dll");

        var check = await service.CheckManagedUpdateAsync(fixture.Game);
        var update = await service.UpdateAsync(fixture.Game);

        Assert.Equal((true, true, false), (check.Managed, check.UpdateAvailable, check.CouldNotCheck));
        Assert.Equal($"{Dlssg2030UnlockDeploymentService.UpdateAvailablePrefix}0.2.4{Dlssg2030UnlockDeploymentService.OldLayoutSuffix} → {Tag}", check.Message);
        Assert.True(update.Success, update.Message);
        Assert.Equal(Dlssg2030UnlockDeploymentService.UpdatedPrefix + Tag + "." + Dlssg2030UnlockDeploymentService.UpdatedIniReplacedNote, update.Message);
        // The winmm build, the release's INI (the old one was still RHI's bytes) and the whole mod folder.
        Assert.Equal(handler.Build("winmm.dll"), File.ReadAllBytes(proxy));
        Assert.Equal(UpstreamIni(), File.ReadAllText(ini));
        foreach (var (name, bytes) in handler.ModFiles)
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(fixture.Bin, "dlssg_sm86", name)));
        var record = records.Find(fixture.Game, ManagedComponentKind.Dlssg2030Unlock)!;
        Assert.Equal((Tag, Tag), (record.Version, record.Metadata["FG2030.ReleaseTag"]));
        Assert.Equal(2 + handler.ModFiles.Count, record.Files.Count);
        foreach (var key in new[] { "FG2030.Router", "FG2030.CommitSha", "FG2030.SourceDllSha256", "FG2030.SourceIniSha256",
                     VerifiedDownload.Sha256KeyFor("Ini"), VerifiedDownload.IntegrityKeyFor("Ini") })
            Assert.False(record.Metadata.ContainsKey(key), key);
        DownloadRecordAssert.Recorded(record, DownloadRecordAssert.Sha256Of(handler.Package), VerifiedDownload.PublishedDigest);
        Assert.Equal("native,builtin", Override(fixture, "winmm"));

        var again = await service.CheckManagedUpdateAsync(fixture.Game);
        Assert.Equal((false, false), (again.UpdateAvailable, again.CouldNotCheck));
    }

    [Fact]
    public async Task OldSingleDllInstall_WithTheUsersIni_KeepsIt()
    {
        using var fixture = CreateGame("old-install-ini", 0x8664, GraphicsApi.DirectX12);
        var handler = new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x45), UpstreamIni());
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);
        var (_, ini) = SeedOldInstall(fixture, records, "version.dll");
        File.AppendAllText(ini, "\n[Logging]\nLevel=2\n");
        var usersIni = File.ReadAllBytes(ini);

        var update = await service.UpdateAsync(fixture.Game);

        Assert.True(update.Success, update.Message);
        Assert.EndsWith(Dlssg2030UnlockDeploymentService.UpdatedIniKeptNote, update.Message, StringComparison.Ordinal);
        Assert.Equal(usersIni, File.ReadAllBytes(ini));
        Assert.Equal(handler.Build("version.dll"), File.ReadAllBytes(Path.Combine(fixture.Bin, "version.dll")));
        Assert.True(File.Exists(Path.Combine(fixture.Bin, "dlssg_sm86", "runtime", "nvngx_dlssg.dll")));
    }

    [Fact]
    public async Task WinhttpInstall_MovesToVersionDll_WithItsOverride()
    {
        using var fixture = CreateGame("winhttp", 0x8664, GraphicsApi.DirectX12);
        var handler = new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x46), UpstreamIni());
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);
        var (winhttp, _) = SeedOldInstall(fixture, records, "winhttp.dll");
        // A record already on the package but under winhttp.dll still needs the move.
        var seeded = records.Find(fixture.Game, ManagedComponentKind.Dlssg2030Unlock)!;
        seeded.Metadata["FG2030.ReleaseTag"] = Tag;
        records.Upsert(seeded);

        var check = await service.CheckManagedUpdateAsync(fixture.Game);
        var update = await service.UpdateAsync(fixture.Game);

        Assert.True(check.UpdateAvailable, check.Message);
        Assert.True(update.Success, update.Message);
        Assert.EndsWith(Dlssg2030UnlockDeploymentService.UpdatedProxyMovedNote, update.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(winhttp));
        Assert.Equal(handler.Build("version.dll"), File.ReadAllBytes(Path.Combine(fixture.Bin, "version.dll")));
        Assert.Null(Override(fixture, "winhttp"));
        Assert.Equal("native,builtin", Override(fixture, "version"));
        var record = records.Find(fixture.Game, ManagedComponentKind.Dlssg2030Unlock)!;
        Assert.Equal("version.dll", record.Metadata["FG2030.ProxyName"]);
        Assert.Equal("version", record.WineOverride!.DllName);
        Assert.Equal("version.dll", service.GetPreferences(fixture.Game).ProxyName);

        // Removal then takes version.dll and its override.
        Assert.True((await service.RemoveAsync(fixture.Game)).Success);
        Assert.Null(Override(fixture, "version"));
        Assert.Equal(["Game.exe"], Directory.GetFiles(fixture.Bin).Select(Path.GetFileName));
    }

    [Fact]
    public async Task WinhttpInstall_WhenVersionDllIsTaken_IsRefusedAndLeftAsItIs()
    {
        using var fixture = CreateGame("winhttp-taken", 0x8664, GraphicsApi.DirectX12);
        var handler = new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x47), UpstreamIni());
        using var http = new HttpClient(handler);
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var service = CreateService(fixture, http, records);
        var (winhttp, _) = SeedOldInstall(fixture, records, "winhttp.dll");
        var external = Path.Combine(fixture.Bin, "version.dll");
        File.WriteAllText(external, "another mod");
        var before = File.ReadAllBytes(winhttp);
        var requests = handler.RequestCount;

        var update = await service.UpdateAsync(fixture.Game);

        Assert.False(update.Success);
        Assert.Equal(Dlssg2030UnlockDeploymentService.RetiredProxyTargetTakenPrefix + external, update.Message);
        Assert.Equal(requests, handler.RequestCount);
        Assert.Equal(before, File.ReadAllBytes(winhttp));
        Assert.Equal("another mod", File.ReadAllText(external));
        Assert.Equal("native,builtin", Override(fixture, "winhttp"));
        Assert.False(Directory.Exists(Path.Combine(fixture.Bin, "dlssg_sm86")));
    }

    [Fact]
    public async Task Install_CrashWhileSavingTheRecord_NextStartRemovesThePackageAndItsFolders()
    {
        using var fixture = CreateGame("crash-install-package", 0x8664, GraphicsApi.DirectX12);
        using var http = new HttpClient(new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x48), UpstreamIni()));
        var (service, records, journal, journalDir, ledger, _) = JournaledService(fixture, http);
        using var death = new SimulatedDeath(journal);
        records.SaveFaultForTesting = death.Die;

        var (died, _) = await death.RunUntilDeathAsync(() => service.InstallAsync(fixture.Game, Dlssg2030UnlockDeploymentService.GpuGenRtx20, "winmm.dll"));
        Assert.True(died);
        Assert.True(File.Exists(Path.Combine(fixture.Bin, "dlssg_sm86", "runtime", "nvngx_dlssg.dll")));

        var report = await SimulatedDeath.RecoverAsync(journalDir, ledger);

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.Equal(["Game.exe"], Directory.GetFiles(fixture.Bin).Select(Path.GetFileName));
        Assert.False(Directory.Exists(Path.Combine(fixture.Bin, "dlssg_sm86")));
    }

    [Fact]
    public async Task OldInstall_Update_CrashWhileSavingTheRecord_NextStartPutsTheSingleDllInstallBack()
    {
        using var fixture = CreateGame("crash-old-update", 0x8664, GraphicsApi.DirectX12);
        using var http = new HttpClient(new DlssgHandler(CreatePe(0x8664, 320 * 1024, 0x49), UpstreamIni()));
        var (service, records, journal, journalDir, ledger, _) = JournaledService(fixture, http);
        var (proxy, ini) = SeedOldInstall(fixture, records, "winhttp.dll");
        var (proxyBytes, iniBytes) = (File.ReadAllBytes(proxy), File.ReadAllBytes(ini));
        using var death = new SimulatedDeath(journal);
        records.SaveFaultForTesting = death.Die;

        var (died, _) = await death.RunUntilDeathAsync(() => service.UpdateAsync(fixture.Game));
        Assert.True(died);

        var report = await SimulatedDeath.RecoverAsync(journalDir, ledger);

        Assert.Equal(JournalRecoveryOutcome.RolledBack, Assert.Single(report!.Entries).Outcome);
        Assert.Equal(proxyBytes, File.ReadAllBytes(proxy));
        Assert.Equal(iniBytes, File.ReadAllBytes(ini));
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "version.dll")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Bin, "dlssg_sm86")));
        Assert.Equal("native,builtin", Override(fixture, "winhttp"));
        Assert.Null(Override(fixture, "version"));
        Assert.Empty(Directory.GetFiles(fixture.Bin, "*.rhi-*"));
    }
}
