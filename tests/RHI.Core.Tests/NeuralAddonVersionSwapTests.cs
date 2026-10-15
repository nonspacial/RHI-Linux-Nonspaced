using RHI.Core.Deployment;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

/// <summary>Upstream RHI 2.7.9 NR fixes: rc sort order, Update All coverage and in-place add-on swap.</summary>
public sealed partial class NeuralAddonVersionSwapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-nr-swap-" + Guid.NewGuid().ToString("N"));

    public NeuralAddonVersionSwapTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void NewestFirst_PutsRc10AboveRc5AndStableAboveRc()
    {
        var sorted = ReleaseVersionOrdering.NewestFirst(
            new[] { "v1.0-rc5", "v0.9", "v1.0-rc10", "v1.0" }.Select(t => new GitHubReleaseVersion(t, t.TrimStart('v'), false)));

        Assert.Equal(["v1.0", "v1.0-rc10", "v1.0-rc5", "v0.9"], sorted.Select(v => v.Tag));
    }

    [Fact]
    public void ShortFuseLatest_PrefersRc10OverRc5RegardlessOfApiOrder()
    {
        var json = """
        [
          { "tag_name":"renodx-dlss-SF-8.5.0-rc5", "assets":[{"name":"renodx-dlss.addon64","browser_download_url":"https://github.com/rhi-tests/rc5"}] },
          { "tag_name":"renodx-dlss-SF-8.5.0-rc10", "assets":[{"name":"renodx-dlss.addon64","browser_download_url":"https://github.com/rhi-tests/rc10"}] }
        ]
        """;

        Assert.Equal("8.5.0-rc10", ShortFuseDeploymentService.ParseReleaseJson(json).Version);
    }

    [Fact]
    public void UpdateAll_IncludesNeuralRenderingAddons()
    {
        Assert.True(LinuxUpdateAllService.IsSupported(ManagedComponentKind.Dlss5Feeder));
        Assert.True(LinuxUpdateAllService.IsSupported(ManagedComponentKind.Dlss5Dx11Bridge));
        Assert.True(LinuxUpdateAllService.IsSupported(ManagedComponentKind.ShortFuse));
    }

    [Fact]
    public async Task ManagedFileSwap_ReplacesOnlyTargetAndRestoresFileAndRecordWhenSaveFails()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "rollback")).FullName;
        var game = Game(bin);
        var target = Write(Path.Combine(bin, "addon.addon64"), [1, 2, 3]);
        var other = Write(Path.Combine(bin, "other.dll"), [4, 5, 6]);
        var ledger = Path.Combine(_root, "rollback-deployments.json");
        var records = new LinuxDeploymentRecordStore(ledger);
        var seeded = Record(game, ManagedComponentKind.ShortFuse, "OLD", target, other);
        seeded.SourceUrl = "https://github.com/rhi-tests/old";
        seeded.Metadata["GitHub.ReleaseTagPin"] = "vOLD";
        records.Upsert(seeded);
        var oldHash = Sha256(target);
        var source = Write("new.addon64", [9, 9, 9]);

        // The ledger's directory refuses writes, so Upsert fails after the file was swapped.
        var record = records.Find(game, ManagedComponentKind.ShortFuse)!;
        using (LedgerWriteBlocker.Block(records, ledger))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => ManagedFileSwap.ReplaceAsync(record, [(target, source)], updated =>
            {
                updated.Version = "NEW";
                updated.SourceUrl = "https://github.com/rhi-tests/new";
                updated.Metadata["GitHub.ReleaseTagPin"] = "vNEW";
                records.Upsert(updated);
            }));
        }

        Assert.Equal([1, 2, 3], File.ReadAllBytes(target));
        Assert.Equal(oldHash, ManagedFileSwap.FindFile(record, "addon.addon64")!.Sha256);
        Assert.Equal("OLD", record.Version);
        // The store, and the ledger after an unrelated save, still describe the old file.
        records.Upsert(Record(game, ManagedComponentKind.ReShade, "1", other));
        foreach (var stored in new[] { records.Find(game, ManagedComponentKind.ShortFuse)!, new LinuxDeploymentRecordStore(ledger).Find(game, ManagedComponentKind.ShortFuse)! })
        {
            Assert.Equal("OLD", stored.Version);
            Assert.Equal("https://github.com/rhi-tests/old", stored.SourceUrl);
            Assert.Equal("vOLD", stored.Metadata["GitHub.ReleaseTagPin"]);
            Assert.Equal(oldHash, ManagedFileSwap.FindFile(stored, "addon.addon64")!.Sha256);
        }
        Assert.Single(Directory.EnumerateFiles(bin, "addon.addon64*"));

        var persisted = 0;
        Assert.Equal(1, await ManagedFileSwap.ReplaceAsync(record, [(target, source)], updated =>
        {
            persisted++;
            records.Upsert(updated);
        }));
        Assert.Equal([9, 9, 9], File.ReadAllBytes(target));
        Assert.Equal([4, 5, 6], File.ReadAllBytes(other));
        Assert.Equal(Sha256(target), ManagedFileSwap.FindFile(records.Find(game, ManagedComponentKind.ShortFuse)!, "addon.addon64")!.Sha256, ignoreCase: true);
        Assert.Equal(oldHash, ManagedFileSwap.FindFile(record, "addon.addon64")!.Sha256);
        Assert.Equal(1, persisted);
        Assert.Single(Directory.EnumerateFiles(bin, "addon.addon64*"));
    }

    [Fact]
    public async Task ManagedFileSwap_KeepsBackupAndSaysSoWhenRestoreFails()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "restore-fails")).FullName;
        var target = Write(Path.Combine(bin, "addon.addon64"), [1, 2, 3]);
        var record = Record(Game(bin), ManagedComponentKind.ShortFuse, "OLD", target);
        var source = Write("new.addon64", [9, 9, 9]);

        // Persist fails after something replaced the target with a non-empty directory, so the
        // backup cannot be renamed back over it.
        var ex = await Assert.ThrowsAsync<ManagedFileRestoreException>(() => ManagedFileSwap.ReplaceAsync(record, [(target, source)], _ =>
        {
            File.Delete(target);
            Directory.CreateDirectory(target);
            File.WriteAllBytes(Path.Combine(target, "blocker"), [0]);
            throw new IOException("disk full");
        }));

        // The verified copy the swap took before changing the file is kept (#31: never a raw, unverified copy).
        var backup = Assert.Single(ex.BackupPaths);
        Assert.StartsWith(target + RHI.Core.Deployment.OwnedFileSet.SnapshotMarker, backup, StringComparison.Ordinal);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(backup));
        Assert.Contains("could not be restored", ex.Message);
        Assert.Contains(backup, ex.Message);
        Assert.Contains("disk full", ex.Message);
        Assert.IsType<IOException>(ex.InnerException);
        Assert.Equal(Sha256(backup), record.Files[0].Sha256);
    }

    [Fact]
    public async Task ManagedFileSwap_RefusesFileModifiedOutsideRhi()
    {
        var target = Write("edited.addon64", [1]);
        var record = new LinuxDeploymentRecord { Files = [new LinuxDeployedFile { Path = target, Sha256 = new string('0', 64) }] };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ManagedFileSwap.ReplaceAsync(record, [(target, Write("src.addon64", [2]))], _ => { }));
        Assert.Equal([1], File.ReadAllBytes(target));
    }

    [Fact]
    public async Task ShortFuseSwap_FailedSaveKeepsAddonAndRecord()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "sf-fail", "game")).FullName;
        var game = Game(bin);
        var addon = Write(Path.Combine(bin, ShortFuseDeploymentService.AddonFileName), Payload(0x10, 40 * 1024));
        var ledger = Path.Combine(_root, "sf-fail-deployments.json");
        var records = new LinuxDeploymentRecordStore(ledger);
        var seeded = Record(game, ManagedComponentKind.ShortFuse, "8.5.0-rc5", addon);
        seeded.SourceUrl = "https://github.com/rhi-tests/rc5";
        seeded.Metadata["GitHub.ReleaseTagPin"] = "renodx-dlss-SF-8.5.0-rc5";
        seeded.Metadata["ShortFuse.AddonSha256"] = Sha256(addon);
        records.Upsert(seeded);
        var oldBytes = File.ReadAllBytes(addon);
        using var http = ShortFuseHttp(Payload(0x30, 40 * 1024));
        var sf = ShortFuse(http, records, "sf-fail");

        ComponentOperationResult result;
        using (LedgerWriteBlocker.Block(records, ledger))
            result = await sf.SwapAddonVersionAsync(game, releaseTag: null);

        Assert.False(result.Success);
        Assert.Contains("the previous add-on was kept", result.Message);
        Assert.Equal(oldBytes, File.ReadAllBytes(addon));
        var record = records.Find(game, ManagedComponentKind.ShortFuse)!;
        Assert.Equal("8.5.0-rc5", record.Version);
        Assert.Equal("https://github.com/rhi-tests/rc5", record.SourceUrl);
        Assert.Equal("renodx-dlss-SF-8.5.0-rc5", record.Metadata["GitHub.ReleaseTagPin"]);
        Assert.Equal(Sha256(addon), record.Metadata["ShortFuse.AddonSha256"]);
        Assert.Equal(Sha256(addon), record.Files[0].Sha256);
    }

    [Fact]
    public async Task ShortFuseSwap_UsesRecordedZzzAddonAndKeepsItsName()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "sf-zzz", "game")).FullName;
        var game = Game(bin);
        var addon = Write(Path.Combine(bin, "zzz_renodx-dlss.addon64"), Payload(0x10, 40 * 1024));
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "sf-zzz-deployments.json"));
        var seeded = Record(game, ManagedComponentKind.ShortFuse, "8.5.0-rc5", addon);
        seeded.Metadata["ShortFuse.AddonSha256"] = Sha256(addon);
        records.Upsert(seeded);
        var newAddon = Payload(0x30, 40 * 1024);
        using var http = ShortFuseHttp(newAddon);

        var result = await ShortFuse(http, records, "sf-zzz").SwapAddonVersionAsync(game, releaseTag: null);

        Assert.True(result.Success, result.Message);
        Assert.Equal(newAddon, File.ReadAllBytes(addon));
        Assert.False(File.Exists(Path.Combine(bin, ShortFuseDeploymentService.AddonFileName)));
        Assert.Equal(addon, Assert.Single(result.ChangedFiles!));
        var record = records.Find(game, ManagedComponentKind.ShortFuse)!;
        Assert.Equal("8.5.0-rc10", record.Version);
        Assert.Equal(addon, Assert.Single(record.Files).Path);
        Assert.Equal(Sha256(addon), record.Files[0].Sha256, ignoreCase: true);
        Assert.Equal(Sha256(addon), record.Metadata["ShortFuse.AddonSha256"], ignoreCase: true);
        DownloadRecordAssert.Recorded(record, Sha256(addon), VerifiedDownload.RecordedOnly); // review w2-trust minor 6
    }

    private static HttpClient ShortFuseHttp(byte[] rc10Addon)
    {
        var json = """
        [
          { "tag_name":"renodx-dlss-SF-8.5.0-rc5", "assets":[{"name":"renodx-dlss.addon64","browser_download_url":"https://github.com/rhi-tests/rc5"}] },
          { "tag_name":"renodx-dlss-SF-8.5.0-rc10", "assets":[{"name":"renodx-dlss.addon64","browser_download_url":"https://github.com/rhi-tests/rc10"}] }
        ]
        """;
        return new HttpClient(new MapHandler(new()
        {
            [ShortFuseDeploymentService.ReleasesApiUrl] = Encoding.UTF8.GetBytes(json),
            ["https://github.com/rhi-tests/rc10"] = rc10Addon
        }));
    }

    [Fact]
    public async Task ShortFuseLatestReleaseCheck_IgnoresThePerGamePin_WhileUpdateCheckHonoursIt()
    {
        var game = Game(Directory.CreateDirectory(Path.Combine(_root, "sf-pin", "game")).FullName);
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "sf-pin-deployments.json"));
        const string pinned = "renodx-dlss-SF-8.5.0-rc5";
        using var http = new HttpClient(new MapHandler(new()
        {
            [ShortFuseDeploymentService.ReleasesApiUrl] = Encoding.UTF8.GetBytes("""
                [
                  { "tag_name":"renodx-dlss-SF-8.5.0-rc5", "assets":[{"name":"renodx-dlss.addon64","browser_download_url":"https://github.com/rhi-tests/rc5"}] },
                  { "tag_name":"renodx-dlss-SF-8.5.0-rc10", "assets":[{"name":"renodx-dlss.addon64","browser_download_url":"https://github.com/rhi-tests/rc10"}] }
                ]
                """),
            [GitHubReleaseAssetResolver.BuildReleaseTagApiUrl(ShortFuseDeploymentService.ReleasesApiUrl, pinned)] = Encoding.UTF8.GetBytes(
                """{ "tag_name":"renodx-dlss-SF-8.5.0-rc5", "assets":[{"name":"renodx-dlss.addon64","browser_download_url":"https://github.com/rhi-tests/rc5"}] }""")
        }));
        var pins = new NeuralAddonVersionPreferenceStore(Path.Combine(_root, "sf-pin-pins.json"));
        pins.Set(game, ManagedComponentKind.ShortFuse, pinned);
        var service = new LinuxManagedComponentService(
            records: records, reshade: null!, addonManager: null!, renodx: null!, renodxUpgrade: null!, optiscaler: null!,
            relimiter: null!, displayCommander: null!, mfgAdaUnlock: null!, dlssg2030: null!, dlssEnablerStandalone: null!,
            rtx40Mfg: null!, dlss5Feeder: null!, dlss5Bridge: null!, d3dCompilerTools: null!, ultimateAsiLoader: null!,
            shortFuse: ShortFuse(http, records, "sf-pin"), dofFix: null!, reFramework: null!, lumaFramework: null!,
            lumaPolicy: null!, neuralVersionPins: pins);

        var latest = await service.CheckShortFuseLatestReleaseAsync(game);
        var pinnedCheck = await service.CheckShortFuseUpdateAsync(game);

        Assert.True(latest.Success, latest.Message);
        Assert.Equal("8.5.0-rc10", latest.LatestVersion);
        Assert.True(pinnedCheck.Success, pinnedCheck.Message);
        Assert.Equal("8.5.0-rc5", pinnedCheck.LatestVersion);
    }

    private ShortFuseDeploymentService ShortFuse(HttpClient http, LinuxDeploymentRecordStore records, string tag)
    {
        var files = new TransactionalFileDeploymentService();
        var overrides = new WineDllOverrideService();
        return new ShortFuseDeploymentService(http, files, records, overrides,
            new UltimateAsiLoaderDeploymentService(http, records, overrides, Path.Combine(_root, tag + "-cache-ual")),
            new DlssManagerService(http, files, Path.Combine(_root, tag + "-cache-dlss")),
            Path.Combine(_root, tag + "-cache-sf"), new ShortFuseSettingsStore(Path.Combine(_root, tag + "-sf.json")));
    }

    [Fact]
    public async Task ShortFuseSwap_ReplacesAddonOnlyAndUpdatesRecord()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "sf", "game")).FullName;
        var game = Game(bin);
        var addon = Write(Path.Combine(bin, ShortFuseDeploymentService.AddonFileName), Payload(0x10, 40 * 1024));
        var dlss = Write(Path.Combine(bin, "nvngx_dlss.dll"), Payload(0x20, 1024));
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "sf-deployments.json"));
        records.Upsert(Record(game, ManagedComponentKind.ShortFuse, "8.5.0-rc5", addon, dlss));
        var dlssHash = Sha256(dlss);

        var newAddon = Payload(0x30, 40 * 1024);
        var json = """
        [
          { "tag_name":"renodx-dlss-SF-8.5.0-rc5", "assets":[{"name":"renodx-dlss.addon64","browser_download_url":"https://github.com/rhi-tests/rc5"}] },
          { "tag_name":"renodx-dlss-SF-8.5.0-rc10", "assets":[{"name":"renodx-dlss.addon64","browser_download_url":"https://github.com/rhi-tests/rc10"}] }
        ]
        """;
        using var http = new HttpClient(new MapHandler(new()
        {
            [ShortFuseDeploymentService.ReleasesApiUrl] = Encoding.UTF8.GetBytes(json),
            ["https://github.com/rhi-tests/rc10"] = newAddon
        }));
        var files = new TransactionalFileDeploymentService();
        var overrides = new WineDllOverrideService();
        var sf = new ShortFuseDeploymentService(http, files, records, overrides,
            new UltimateAsiLoaderDeploymentService(http, records, overrides, Path.Combine(_root, "cache-ual")),
            new DlssManagerService(http, files, Path.Combine(_root, "cache-dlss")),
            Path.Combine(_root, "cache-sf"), new ShortFuseSettingsStore(Path.Combine(_root, "sf.json")));

        var result = await sf.SwapAddonVersionAsync(game, releaseTag: null);

        Assert.True(result.Success, result.Message);
        Assert.Equal(newAddon, File.ReadAllBytes(addon));
        Assert.Equal(dlssHash, Sha256(dlss));
        var record = records.Find(game, ManagedComponentKind.ShortFuse)!;
        Assert.Equal("8.5.0-rc10", record.Version);
        Assert.Equal(Sha256(addon), ManagedFileSwap.FindFile(record, ShortFuseDeploymentService.AddonFileName)!.Sha256, ignoreCase: true);
        Assert.Equal(dlssHash, ManagedFileSwap.FindFile(record, "nvngx_dlss.dll")!.Sha256);
    }

    [Fact]
    public async Task FeederSwap_ReplacesFeederAddonOnlyAndKeepsRecordOnDownloadFailure()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "feeder", "game")).FullName;
        var game = Game(bin);
        var addon = Write(Path.Combine(bin, "dlss5-feed.addon64"), Pe(0x40));
        var nr = Write(Path.Combine(bin, "nvngx_dlssnr.dll"), Pe(0x50));
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "feeder-deployments.json"));
        var seeded = Record(game, ManagedComponentKind.Dlss5Feeder, "1.0", addon, nr);
        seeded.Metadata["Feeder.FullStack"] = "1";
        records.Upsert(seeded);
        var nrHash = Sha256(nr);

        var newAddon = Pe(0x60);
        var responses = new Dictionary<string, byte[]>
        {
            ["https://api.github.com/repos/jlrouzies-fr/DLSS5-Feeder/releases/tags/v1.1"] = Encoding.UTF8.GetBytes(
                """{"tag_name":"v1.1","assets":[{"name":"DLSS5-Feeder-1.1.zip","browser_download_url":"https://github.com/rhi-tests/feeder-1.1.zip"}]}""")
        };
        using var http = new HttpClient(new MapHandler(responses));
        var feeder = Feeder(http, records);

        var failed = await feeder.SwapFeederVersionAsync(game, "v1.1");
        Assert.False(failed.Success);
        Assert.Equal("1.0", records.Find(game, ManagedComponentKind.Dlss5Feeder)!.Version);
        Assert.Equal(Pe(0x40), File.ReadAllBytes(addon));

        var feederZip = Zip(("DLSS5-Feeder/dlss5-feed.addon64", newAddon));
        responses["https://github.com/rhi-tests/feeder-1.1.zip"] = feederZip;
        var result = await feeder.SwapFeederVersionAsync(game, "v1.1");

        Assert.True(result.Success, result.Message);
        Assert.Equal(newAddon, File.ReadAllBytes(addon));
        Assert.Equal(nrHash, Sha256(nr));
        var record = records.Find(game, ManagedComponentKind.Dlss5Feeder)!;
        Assert.Equal("1.1", record.Version);
        Assert.Equal("v1.1", record.Metadata["GitHub.ReleaseTagPin"]);
        Assert.Equal(Sha256(addon), ManagedFileSwap.FindFile(record, "dlss5-feed.addon64")!.Sha256, ignoreCase: true);
        // The record names the release zip the swapped add-on came from (review w2-trust minor 6); no digest was published.
        DownloadRecordAssert.Recorded(record, DownloadRecordAssert.Sha256Of(feederZip), VerifiedDownload.RecordedOnly);
    }

    [Fact]
    public async Task FeederSwap_DownloadsTheReleaseZipOncePerVersion_ForEveryGame()
    {
        // #114: swapping several games (or one game twice) to the same Feeder release reuses the stamped zip.
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "feeder-cache-deployments.json"));
        var games = new List<(DetectedGame Game, string Addon)>();
        foreach (var name in new[] { "one", "two" })
        {
            var bin = Directory.CreateDirectory(Path.Combine(_root, "feeder-cache", name)).FullName;
            var game = Game(bin);
            game.Name = name;
            game.InstallPath = bin;
            var addon = Write(Path.Combine(bin, "dlss5-feed.addon64"), Pe(0x40));
            var seeded = Record(game, ManagedComponentKind.Dlss5Feeder, "1.0", addon, Write(Path.Combine(bin, "nvngx_dlssnr.dll"), Pe(0x50)));
            seeded.Metadata["Feeder.FullStack"] = "1";
            records.Upsert(seeded);
            games.Add((game, addon));
        }
        const string zipUrl = "https://github.com/rhi-tests/feeder-1.2.zip";
        var newAddon = Pe(0x61);
        var handler = new MapHandler(new Dictionary<string, byte[]>
        {
            ["https://api.github.com/repos/jlrouzies-fr/DLSS5-Feeder/releases/tags/v1.2"] = Encoding.UTF8.GetBytes(
                $$"""{"tag_name":"v1.2","assets":[{"name":"DLSS5-Feeder-1.2.zip","browser_download_url":"{{zipUrl}}"}]}"""),
            [zipUrl] = Zip(("DLSS5-Feeder/dlss5-feed.addon64", newAddon))
        });
        using var http = new HttpClient(handler);
        var feeder = Feeder(http, records);

        foreach (var (game, addon) in games)
        {
            var result = await feeder.SwapFeederVersionAsync(game, "v1.2");
            Assert.True(result.Success, result.Message);
            Assert.Equal(newAddon, File.ReadAllBytes(addon));
        }
        var again = await feeder.SwapFeederVersionAsync(games[0].Game, "v1.2");

        Assert.True(again.Success, again.Message);
        Assert.Equal(1, handler.Gets[zipUrl]);
    }

    [Fact]
    public async Task LegacyAddonOnlyFeeder_PinKeptForReinstall_AndUpdateAllDoesNotFailIt()
    {
        // #115: an add-on-only Feeder from before the full stack cannot be swapped in place. A pin change used to start
        // a swap that always failed (and reverted the pin); manual Update All reported it failed on every run.
        var bin = Directory.CreateDirectory(Path.Combine(_root, "legacy", "game")).FullName;
        var game = Game(bin);
        var addon = Write(Path.Combine(bin, "dlss5-feed.addon64"), Pe(0x50));
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "legacy-deployments.json"));
        records.Upsert(Record(game, ManagedComponentKind.Dlss5Feeder, "0.9", addon)); // no Feeder.FullStack metadata
        using var http = new HttpClient(new MapHandler(new()));
        var service = new LinuxManagedComponentService(
            records: records, reshade: null!, addonManager: null!, renodx: null!, renodxUpgrade: null!, optiscaler: null!,
            relimiter: null!, displayCommander: null!, mfgAdaUnlock: null!, dlssg2030: null!, dlssEnablerStandalone: null!,
            rtx40Mfg: null!, dlss5Feeder: Feeder(http, records), dlss5Bridge: null!, d3dCompilerTools: null!, ultimateAsiLoader: null!,
            shortFuse: null!, dofFix: null!, reFramework: null!, lumaFramework: null!,
            lumaPolicy: null!, neuralVersionPins: new NeuralAddonVersionPreferenceStore(Path.Combine(_root, "legacy-pins.json")));

        Assert.Equal(NeuralPinChange.ReinstallNeeded, service.PlanNeuralPinChange(game, ManagedComponentKind.Dlss5Feeder));
        Assert.Equal(NeuralPinChange.SaveOnly, service.PlanNeuralPinChange(game, ManagedComponentKind.ShortFuse));

        foreach (var mode in new[] { LinuxUpdateRunMode.Manual, LinuxUpdateRunMode.Background })
        {
            var result = await new LinuxUpdateAllService(records, service, new NeverRunning()).RunAsync([game], _ => [], mode);
            var item = Assert.Single(result.Items);
            Assert.True(item.Success, item.Message);
            Assert.Equal((0, 0), (result.FailedCount, result.ChangedCount));
            // Not compared against any release: "could not be checked", never "current" (w2-launch review, minor 4).
            Assert.Equal((0, 1), (result.CurrentCount, result.UncheckedCount));
        }
        Assert.Equal(Pe(0x50), File.ReadAllBytes(addon));
        Assert.Equal("0.9", records.Find(game, ManagedComponentKind.Dlss5Feeder)!.Version);
    }

    [Fact]
    public void FullStackFeeder_PinChangeSwapsInPlace()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "full", "game")).FullName;
        var game = Game(bin);
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "full-deployments.json"));
        var record = Record(game, ManagedComponentKind.Dlss5Feeder, "1.0", Write(Path.Combine(bin, "dlss5-feed.addon64"), Pe(0x51)));
        record.Metadata["Feeder.FullStack"] = "1";
        records.Upsert(record);
        var service = new LinuxManagedComponentService(
            records: records, reshade: null!, addonManager: null!, renodx: null!, renodxUpgrade: null!, optiscaler: null!,
            relimiter: null!, displayCommander: null!, mfgAdaUnlock: null!, dlssg2030: null!, dlssEnablerStandalone: null!,
            rtx40Mfg: null!, dlss5Feeder: null!, dlss5Bridge: null!, d3dCompilerTools: null!, ultimateAsiLoader: null!,
            shortFuse: null!, dofFix: null!, reFramework: null!, lumaFramework: null!,
            lumaPolicy: null!, neuralVersionPins: new NeuralAddonVersionPreferenceStore(Path.Combine(_root, "full-pins.json")));

        Assert.Equal(NeuralPinChange.SwapInPlace, service.PlanNeuralPinChange(game, ManagedComponentKind.Dlss5Feeder));
    }

    private sealed class NeverRunning : IGameRunningProbe
    {
        public bool IsRunning(DetectedGame game) => false;
    }

    // w2/vmsplit review finding 1: the in-place swap (ShortFuse/Feeder add-on, NR DLL) ends the cancellable phase
    // before its first file and then swaps every file (a cancel between files used to roll the swap back mid-way).
    [Fact]
    public async Task ManagedFileSwap_CancelAfterTheFirstWrite_SwapsEveryFile()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "cancel-swap", "game")).FullName;
        var game = Game(bin);
        var first = Write(Path.Combine(bin, "addon.addon64"), [1, 2, 3]);
        var second = Write(Path.Combine(bin, "helper.exe"), [4, 5, 6]);
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "cancel-swap-deployments.json"));
        records.Upsert(Record(game, ManagedComponentKind.Dlss5Feeder, "OLD", first, second));
        var record = records.Find(game, ManagedComponentKind.Dlss5Feeder)!;
        var newFirst = Write("cancel-new-addon.addon64", [7, 7, 7]);
        var newSecond = Write("cancel-new-helper.exe", [8, 8, 8]);

        var run = await CancelAtWritePhase.RunAsync(ct => ManagedFileSwap.ReplaceAsync(record, [(first, newFirst), (second, newSecond)], updated =>
        {
            updated.Version = "NEW";
            records.Upsert(updated);
        }, ct), [bin]);

        Assert.Equal(2, run.Result);
        Assert.Equal([7, 7, 7], File.ReadAllBytes(first));
        Assert.Equal([8, 8, 8], File.ReadAllBytes(second));
        Assert.Equal("NEW", records.Find(game, ManagedComponentKind.Dlss5Feeder)!.Version);
    }

    private Dlss5FeederFullDeploymentService Feeder(HttpClient http, LinuxDeploymentRecordStore records)
    {
        var files = new TransactionalFileDeploymentService();
        var manifest = new UpstreamRhiManifestService(http, "https://manifest.invalid/rhi.json");
        var legacy = new UpstreamRhiAddonDeploymentService(http, records, manifest, new UpstreamRhiAddonDeploymentOptions(
            ManagedComponentKind.Dlss5Feeder, "dlss5-feed", "DLSS5 Feeder", "https://github.com/rhi-tests/latest", 64 * 1024,
            DeployFileName64: "dlss5-feed.addon64"), Path.Combine(_root, "cache-legacy"));
        return new Dlss5FeederFullDeploymentService(http, records,
            new DlssManagerService(http, files, Path.Combine(_root, "cache-dlss")), legacy, Path.Combine(_root, "cache-feeder"));
    }

    private static DetectedGame Game(string bin) => new()
    {
        Name = "NR swap " + Path.GetFileName(Path.GetDirectoryName(bin)), InstallPath = Path.GetDirectoryName(bin)!,
        Source = "Test", Runner = "Wine", BinaryDirectory = bin, ExecutablePath = Path.Combine(bin, "Game.exe")
    };

    private static LinuxDeploymentRecord Record(DetectedGame game, ManagedComponentKind kind, string version, params string[] paths) => new()
    {
        GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = kind,
        BinaryDirectory = game.BinaryDirectory!, Version = version,
        Files = paths.Select(p => new LinuxDeployedFile { Path = p, Sha256 = Sha256(p) }).ToList()
    };

    private string Write(string nameOrPath, byte[] bytes)
    {
        var path = Path.IsPathRooted(nameOrPath) ? nameOrPath : Path.Combine(_root, nameOrPath);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Payload(byte marker, int size) => Enumerable.Range(0, size).Select(i => (byte)(marker + i * 7)).ToArray();

    private static byte[] Pe(byte marker)
    {
        var bytes = Payload(marker, 80 * 1024);
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
        bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E'; bytes[0x82] = 0; bytes[0x83] = 0;
        BitConverter.GetBytes((ushort)0x8664).CopyTo(bytes, 0x84);
        return bytes;
    }

    private static byte[] Zip(params (string Name, byte[] Bytes)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, bytes) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(bytes);
            }
        return ms.ToArray();
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private sealed class MapHandler(Dictionary<string, byte[]> responses) : HttpMessageHandler
    {
        public Dictionary<string, int> Gets { get; } = new(StringComparer.Ordinal);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Gets[url] = Gets.GetValueOrDefault(url) + 1;
            return Task.FromResult(responses.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
