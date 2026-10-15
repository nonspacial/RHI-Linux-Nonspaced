using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #91: upstream's "No Addons" ReShade channel — the reshade.me build without add-on support (<c>ReShade_Setup_X.exe</c>,
/// upstream <c>NormalReShadeUpdateService</c>), kept apart from the add-on build in the cache and the update check, and
/// never combined with ReShade add-ons.
/// </summary>
public sealed class ReShadeNoAddonsChannelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-reshade-noaddons-" + Guid.NewGuid().ToString("N"));

    public ReShadeNoAddonsChannelTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task NoAddons_InstallsTheBuildWithoutAddonSupport_AndRecordsTheChannel()
    {
        var (game, store) = CreateGame("install");
        var handler = new Handler { AddonVersion = "6.8.0", NoAddonsVersion = "6.8.0" };
        var service = NewService(store, handler);

        var result = await service.InstallAsync(game, new ReShadeInstallOptions { Channel = ReShadeChannel.NoAddons, ManageIni = false });

        Assert.True(result.Success, result.Message);
        Assert.Contains(handler.Requests, u => u.EndsWith("/downloads/ReShade_Setup_6.8.0.exe", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, u => u.Contains("_Addon", StringComparison.Ordinal));
        Assert.Contains("NO-ADDON-BUILD", File.ReadAllText(Path.Combine(game.BinaryDirectory!, "dxgi.dll"), Encoding.Latin1));
        Assert.Equal(ReShadeChannel.NoAddons, service.GetInstalledOptions(game).Channel);
        Assert.True(ReShadeDeploymentService.LacksAddonSupport(store.Find(game, ManagedComponentKind.ReShade)));
        Assert.Equal("6.8.0", store.Find(game, ManagedComponentKind.ReShade)!.Version);
    }

    [Fact]
    public async Task AddonChannel_StillInstallsTheAddonBuild()
    {
        var (game, store) = CreateGame("addon");
        var handler = new Handler { AddonVersion = "6.8.0", NoAddonsVersion = "6.8.0" };
        var service = NewService(store, handler);

        var result = await service.InstallAsync(game, new ReShadeInstallOptions { ManageIni = false });

        Assert.True(result.Success, result.Message);
        Assert.Contains("ADDON-BUILD", File.ReadAllText(Path.Combine(game.BinaryDirectory!, "dxgi.dll"), Encoding.Latin1));
        Assert.DoesNotContain("NO-ADDON-BUILD", File.ReadAllText(Path.Combine(game.BinaryDirectory!, "dxgi.dll"), Encoding.Latin1));
        Assert.False(ReShadeDeploymentService.LacksAddonSupport(store.Find(game, ManagedComponentKind.ReShade)));
    }

    // The update check compares with the No Addons release, not the add-on one (they can differ on reshade.me).
    [Fact]
    public async Task NoAddons_UpdateCheck_FollowsTheNoAddonsRelease()
    {
        var (game, store) = CreateGame("update");
        var handler = new Handler { AddonVersion = "6.7.0", NoAddonsVersion = "6.7.0" };
        var service = NewService(store, handler);
        Assert.True((await service.InstallAsync(game, new ReShadeInstallOptions { Channel = ReShadeChannel.NoAddons, ManageIni = false })).Success);

        handler.AddonVersion = "6.9.0";
        var current = await service.CheckUpdateAsync(game);
        Assert.False(current.UpdateAvailable, current.Message);
        Assert.Equal(ReShadeDeploymentService.NoAddonsCurrentPrefix + "6.7.0.", current.Message);

        handler.NoAddonsVersion = "6.8.0";
        var newer = await service.CheckUpdateAsync(game);
        Assert.True(newer.UpdateAvailable, newer.Message);
        Assert.Equal("6.8.0", newer.LatestVersion);

        var update = await service.UpdateAsync(game);
        Assert.True(update.Success, update.Message);
        Assert.Equal("6.8.0", store.Find(game, ManagedComponentKind.ReShade)!.Version);
        Assert.Equal(ReShadeChannel.NoAddons, service.GetInstalledOptions(game).Channel);
    }

    // During a reshade.me outage only a cached No Addons copy may stand in, never the add-on build (and the reverse).
    [Fact]
    public async Task NoAddons_Outage_NeverFallsBackToTheAddonBuild()
    {
        var (seedGame, seedStore) = CreateGame("seed");
        var handler = new Handler { AddonVersion = "6.8.0", NoAddonsVersion = "6.8.0" };
        Assert.True((await NewService(seedStore, handler).InstallAsync(seedGame, new ReShadeInstallOptions { ManageIni = false })).Success);

        var (game, store) = CreateGame("outage");
        handler.HomeUnreachable = true;
        var result = await NewService(store, handler).InstallAsync(game, new ReShadeInstallOptions { Channel = ReShadeChannel.NoAddons, ManageIni = false });

        Assert.False(result.Success, result.Message);
        Assert.Contains("No cached ReShade copy", result.Message);
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));
    }

    // Owner decisions (#91, #170): a game switches to No Addons with add-ons installed; their add-on files are turned off
    // (renamed to <name>.rhi-off, kept, never loaded), recorded, and switching back turns exactly those on again. The
    // status line says how many.
    [Fact]
    public async Task NoAddons_TurnsInstalledAddonsOff_AndBackOn_KeepingEveryByte()
    {
        var (game, store) = CreateGame("switch");
        var handler = new Handler { AddonVersion = "6.8.0", NoAddonsVersion = "6.8.0" };
        var service = NewService(store, handler);
        Assert.True((await service.InstallAsync(game, new ReShadeInstallOptions { ManageIni = false })).Success);
        var addon = SeedAddon(game, store, ManagedComponentKind.RenoDX, "renodx-game.addon64", withIni: true);
        var other = SeedAddon(game, store, ManagedComponentKind.ReLimiter, "relimiter.addon64");
        var ini = Path.Combine(game.BinaryDirectory!, "renodx-game.ini");
        // A second add-on file of the same component: still one add-on in the count.
        var extra = Path.Combine(game.BinaryDirectory!, "renodx-game-extra.addon32");
        File.WriteAllText(extra, "MZ extra");
        var withExtra = store.Find(game, ManagedComponentKind.RenoDX)!;
        withExtra.Files.Add(new LinuxDeployedFile { Path = extra, Sha256 = RHI.Core.Deployment.FileHash.Sha256Hex(extra) });
        store.Upsert(withExtra);
        // The user's own add-on (no record, no Addon Manager entry) is never touched (review nit 5).
        var users = Path.Combine(game.BinaryDirectory!, "users-own.addon64");
        File.WriteAllText(users, "MZ user's own");

        var off = await service.InstallAsync(game, new ReShadeInstallOptions { Channel = ReShadeChannel.NoAddons, ManageIni = false });

        Assert.True(off.Success, off.Message);
        Assert.EndsWith(ReShadeAddonSwitch.TurnedOffPrefix + "2.", off.Message, StringComparison.Ordinal);
        Assert.Contains("NO-ADDON-BUILD", File.ReadAllText(Path.Combine(game.BinaryDirectory!, "dxgi.dll"), Encoding.Latin1));
        Assert.False(File.Exists(addon));
        Assert.False(File.Exists(other));
        Assert.Equal("MZ " + Path.GetFileName(addon), File.ReadAllText(addon + ReShadeAddonSwitch.OffSuffix));
        Assert.Equal("MZ " + Path.GetFileName(other), File.ReadAllText(other + ReShadeAddonSwitch.OffSuffix));
        Assert.Equal("[renodx]", File.ReadAllText(ini)); // only the add-on file itself is turned off
        var renoDx = store.Find(game, ManagedComponentKind.RenoDX)!;
        Assert.Equal([addon + ReShadeAddonSwitch.OffSuffix, ini, extra + ReShadeAddonSwitch.OffSuffix], renoDx.Files.Select(f => f.Path));
        Assert.Equal("MZ extra", File.ReadAllText(extra + ReShadeAddonSwitch.OffSuffix));
        Assert.True(ReShadeAddonSwitch.IsOff(renoDx));
        Assert.Equal(other + ReShadeAddonSwitch.OffSuffix, Assert.Single(store.Find(game, ManagedComponentKind.ReLimiter)!.Files).Path);
        Assert.Equal("MZ user's own", File.ReadAllText(users));
        Assert.False(File.Exists(users + ReShadeAddonSwitch.OffSuffix));

        var on = await service.InstallAsync(game, new ReShadeInstallOptions { Channel = ReShadeChannel.Stable, ManageIni = false });

        Assert.True(on.Success, on.Message);
        Assert.EndsWith(ReShadeAddonSwitch.TurnedOnPrefix + "2.", on.Message, StringComparison.Ordinal);
        Assert.Equal("MZ " + Path.GetFileName(addon), File.ReadAllText(addon));
        Assert.Equal("MZ " + Path.GetFileName(other), File.ReadAllText(other));
        Assert.False(File.Exists(addon + ReShadeAddonSwitch.OffSuffix));
        Assert.Equal([addon, ini, extra], store.Find(game, ManagedComponentKind.RenoDX)!.Files.Select(f => f.Path));
        Assert.Equal("MZ extra", File.ReadAllText(extra));
        Assert.False(ReShadeAddonSwitch.IsOff(store.Find(game, ManagedComponentKind.ReLimiter)));
        Assert.Equal("MZ user's own", File.ReadAllText(users));

        // Another apply on the same build changes no add-on and says nothing about add-ons.
        var again = await service.InstallAsync(game, new ReShadeInstallOptions { Channel = ReShadeChannel.Stable, ManageIni = false });
        Assert.DoesNotContain(ReShadeAddonSwitch.TurnedOnPrefix.Trim(), again.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoAddons_TurnsAddonManagerAddonsOffAndOn_AndClearingTheSelectionDeletesTheOffCopy()
    {
        var (game, store) = CreateGame("manager");
        var handler = new Handler();
        var service = NewService(store, handler);
        Assert.True((await service.InstallAsync(game, new ReShadeInstallOptions { ManageIni = false })).Success);
        var managerAddon = Path.Combine(game.BinaryDirectory!, "manager-pick.addon64");
        File.WriteAllText(managerAddon, "MZ manager");
        service.AddonManagerFiles = _ => [managerAddon];

        var off = await service.InstallAsync(game, new ReShadeInstallOptions { Channel = ReShadeChannel.NoAddons, ManageIni = false });
        Assert.EndsWith(ReShadeAddonSwitch.TurnedOffPrefix + "1.", off.Message, StringComparison.Ordinal);
        Assert.Equal("MZ manager", File.ReadAllText(managerAddon + ReShadeAddonSwitch.OffSuffix));
        Assert.False(File.Exists(managerAddon));

        var on = await service.InstallAsync(game, new ReShadeInstallOptions { Channel = ReShadeChannel.Stable, ManageIni = false });
        Assert.EndsWith(ReShadeAddonSwitch.TurnedOnPrefix + "1.", on.Message, StringComparison.Ordinal);
        Assert.Equal("MZ manager", File.ReadAllText(managerAddon));

        // The real manager: an add-on it installed, turned off, is deleted with the selection (removal is the user's act).
        var managed = Managed(store, game.InstallPath);
        var manager = (ReShadeAddonManagerService)typeof(LinuxManagedComponentService)
            .GetField("_addonManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(managed)!;
        var statePath = Path.Combine(game.InstallPath, "addon-manager.json");
        var hash = RHI.Core.Deployment.FileHash.Sha256Hex(managerAddon);
        File.WriteAllText(statePath, System.Text.Json.JsonSerializer.Serialize(new
        {
            Schema = 1,
            Games = new Dictionary<string, object>
            {
                [RHI.Platform.Linux.Settings.GameRecordIdentity.Key(game)] = new { SelectedIds = new[] { "pick" }, Files = new[] { new { AddonId = "pick", Path = managerAddon, Sha256 = hash } } }
            }
        }));
        Assert.Equal([managerAddon], manager.OwnedAddonFiles(game));
        File.Move(managerAddon, managerAddon + ReShadeAddonSwitch.OffSuffix);
        var cleared = await managed.ApplyReShadeAddonSelectionAsync(game, []);
        Assert.False(File.Exists(managerAddon + ReShadeAddonSwitch.OffSuffix), cleared.Message);
    }

    // Review minor 4: when the Addon Manager's saved list cannot be read, nobody can tell its add-ons apart; they are left as
    // they are and the status line says so on a switch to No Addons and back (not on an unrelated Stable update).
    [Fact]
    public async Task AddonManagerListUnreadable_SwitchLeavesItsAddons_AndSaysSo()
    {
        var (game, store) = CreateGame("manager-unreadable");
        var service = NewService(store, new Handler());
        Assert.True((await service.InstallAsync(game, new ReShadeInstallOptions { ManageIni = false })).Success);
        var managerAddon = Path.Combine(game.BinaryDirectory!, "manager-pick.addon64");
        File.WriteAllText(managerAddon, "MZ manager");
        var component = SeedAddon(game, store, ManagedComponentKind.ReLimiter, "relimiter.addon64");

        // The real manager hands back null (not an empty list) for a list it cannot read.
        var statePath = Path.Combine(game.InstallPath, "addon-manager.json");
        File.WriteAllText(statePath, "{ not json");
        var http = new HttpClient(new Handler { HomeUnreachable = true });
        var manager = new ReShadeAddonManagerService(http, new UpstreamRhiManifestService(http), Path.Combine(game.InstallPath, "addon-cache"),
            Path.Combine(game.InstallPath, "custom-addons"), statePath);
        Assert.Null(manager.OwnedAddonFiles(game));
        service.AddonManagerFiles = manager.OwnedAddonFiles;

        var off = await service.InstallAsync(game, new ReShadeInstallOptions { Channel = ReShadeChannel.NoAddons, ManageIni = false });
        Assert.True(off.Success, off.Message);
        Assert.EndsWith(ReShadeAddonSwitch.TurnedOffPrefix + "1." + ReShadeAddonSwitch.ManagerUnreadableNote, off.Message, StringComparison.Ordinal);
        Assert.Equal("MZ manager", File.ReadAllText(managerAddon));
        Assert.True(File.Exists(component + ReShadeAddonSwitch.OffSuffix)); // component add-ons still switch

        var on = await service.InstallAsync(game, new ReShadeInstallOptions { Channel = ReShadeChannel.Stable, ManageIni = false });
        Assert.True(on.Success, on.Message);
        Assert.EndsWith(ReShadeAddonSwitch.TurnedOnPrefix + "1." + ReShadeAddonSwitch.ManagerUnreadableNote, on.Message, StringComparison.Ordinal);

        var again = await service.InstallAsync(game, new ReShadeInstallOptions { Channel = ReShadeChannel.Stable, ManageIni = false });
        Assert.True(again.Success, again.Message);
        Assert.DoesNotContain(ReShadeAddonSwitch.ManagerUnreadableNote, again.Message, StringComparison.Ordinal);

        // Once the list reads again, the note goes and its add-ons switch as before.
        File.Delete(statePath);
        Assert.Empty(manager.OwnedAddonFiles(game)!);
        var readable = await service.InstallAsync(game, new ReShadeInstallOptions { Channel = ReShadeChannel.NoAddons, ManageIni = false });
        Assert.DoesNotContain(ReShadeAddonSwitch.ManagerUnreadableNote, readable.Message, StringComparison.Ordinal);
    }

    // Re-review nit 3: ReShade removed while No Addons had the Addon Manager's add-ons off, then installed again as Stable
    // while that list cannot be read: they stay off, and the status line says so (as on a switch back from No Addons).
    [Fact]
    public async Task ReinstalledStable_WithManagerAddonsLeftOff_AndItsListUnreadable_SaysSo()
    {
        var (game, store) = CreateGame("manager-reinstall");
        var service = NewService(store, new Handler());
        Assert.True((await service.InstallAsync(game, new ReShadeInstallOptions { ManageIni = false })).Success);
        var managerAddon = Path.Combine(game.BinaryDirectory!, "manager-pick.addon64");
        File.WriteAllText(managerAddon, "MZ manager");
        service.AddonManagerFiles = _ => [managerAddon];
        Assert.True((await service.InstallAsync(game, new ReShadeInstallOptions { Channel = ReShadeChannel.NoAddons, ManageIni = false })).Success);
        Assert.True(File.Exists(managerAddon + ReShadeAddonSwitch.OffSuffix));
        var removed = await service.RemoveAsync(game);
        Assert.True(removed.Success, removed.Message);
        Assert.Null(store.Find(game, ManagedComponentKind.ReShade));

        service.AddonManagerFiles = _ => null; // the manager's saved list cannot be read
        var reinstalled = await service.InstallAsync(game, new ReShadeInstallOptions { Channel = ReShadeChannel.Stable, ManageIni = false });

        Assert.True(reinstalled.Success, reinstalled.Message);
        Assert.EndsWith(ReShadeAddonSwitch.ManagerUnreadableNote, reinstalled.Message, StringComparison.Ordinal);
        Assert.Equal("MZ manager", File.ReadAllText(managerAddon + ReShadeAddonSwitch.OffSuffix));

        // A fresh install with no add-on left off says nothing, even with the list unreadable.
        var (fresh, freshStore) = CreateGame("manager-fresh");
        var freshService = NewService(freshStore, new Handler());
        freshService.AddonManagerFiles = _ => null;
        var plain = await freshService.InstallAsync(fresh, new ReShadeInstallOptions { Channel = ReShadeChannel.Stable, ManageIni = false });
        Assert.True(plain.Success, plain.Message);
        Assert.DoesNotContain(ReShadeAddonSwitch.ManagerUnreadableNote, plain.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoAddons_AddonsOtherNameTaken_RefusesBeforeAnyChange()
    {
        var (game, store) = CreateGame("taken");
        var service = NewService(store, new Handler());
        Assert.True((await service.InstallAsync(game, new ReShadeInstallOptions { ManageIni = false })).Success);
        var addon = SeedAddon(game, store, ManagedComponentKind.RenoDX, "renodx-game.addon64");
        File.WriteAllText(addon + ReShadeAddonSwitch.OffSuffix, "user's own file");
        var dxgi = File.ReadAllBytes(Path.Combine(game.BinaryDirectory!, "dxgi.dll"));
        var ledger = File.ReadAllText(Path.Combine(_root, "taken", "records.json"));

        var result = await service.InstallAsync(game, new ReShadeInstallOptions { Channel = ReShadeChannel.NoAddons, ManageIni = false });

        Assert.False(result.Success);
        Assert.Equal(ReShadeAddonSwitch.NameTakenPrefix + addon + ReShadeAddonSwitch.OffSuffix, result.Message);
        Assert.Equal(dxgi, File.ReadAllBytes(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));
        Assert.Equal("MZ renodx-game.addon64", File.ReadAllText(addon));
        Assert.Equal("user's own file", File.ReadAllText(addon + ReShadeAddonSwitch.OffSuffix));
        Assert.Equal(ledger, File.ReadAllText(Path.Combine(_root, "taken", "records.json")));
    }

    [Fact]
    public async Task NoAddons_RecordSaveFails_PutsTheAddonsBack()
    {
        var (game, store) = CreateGame("save-fails");
        var service = NewService(store, new Handler());
        Assert.True((await service.InstallAsync(game, new ReShadeInstallOptions { ManageIni = false })).Success);
        var addon = SeedAddon(game, store, ManagedComponentKind.RenoDX, "renodx-game.addon64");
        store.SaveFaultForTesting = () => new IOException("disk full");

        var result = await service.InstallAsync(game, new ReShadeInstallOptions { Channel = ReShadeChannel.NoAddons, ManageIni = false });

        store.SaveFaultForTesting = null;
        Assert.False(result.Success);
        Assert.Equal("MZ renodx-game.addon64", File.ReadAllText(addon));
        Assert.False(File.Exists(addon + ReShadeAddonSwitch.OffSuffix));
        Assert.Equal(addon, Assert.Single(store.Find(game, ManagedComponentKind.RenoDX)!.Files).Path);
    }

    [Fact]
    public async Task NoAddons_CrashBeforeTheLedgerWrite_NextStartPutsTheAddonsBack()
    {
        var (game, store) = CreateGame("crash");
        Assert.True((await NewService(store, new Handler()).InstallAsync(game, new ReShadeInstallOptions { ManageIni = false })).Success);
        var addon = SeedAddon(game, store, ManagedComponentKind.RenoDX, "renodx-game.addon64");
        var journalDir = Path.Combine(_root, "crash", "journal");
        var (journal, overrides) = SimulatedDeath.NewJournal(journalDir, store);
        var service = NewService(store, new Handler(), journal, overrides);
        var ledgerPath = Path.Combine(_root, "crash", "records.json");
        var ledgerBefore = File.ReadAllText(ledgerPath);
        using var death = new SimulatedDeath(journal);
        store.SaveFaultForTesting = death.Die;

        var (died, _) = await death.RunUntilDeathAsync(() => service.InstallAsync(game, new ReShadeInstallOptions { Channel = ReShadeChannel.NoAddons, ManageIni = false }));
        Assert.True(died);
        Assert.True(File.Exists(addon + ReShadeAddonSwitch.OffSuffix)); // renamed, then the process died before the ledger write

        await SimulatedDeath.RecoverAsync(journalDir, ledgerPath);

        Assert.Equal("MZ renodx-game.addon64", File.ReadAllText(addon));
        Assert.False(File.Exists(addon + ReShadeAddonSwitch.OffSuffix));
        Assert.Equal(ledgerBefore, File.ReadAllText(ledgerPath));
    }

    [Fact]
    public async Task AddonTurnedOff_IsNotUpdatedOrReinstalled_ShowsWhy_AndRemovalDeletesItsFile()
    {
        var (game, store) = CreateGame("held");
        var service = NewService(store, new Handler());
        Assert.True((await service.InstallAsync(game, new ReShadeInstallOptions { ManageIni = false })).Success);
        var addon = SeedAddon(game, store, ManagedComponentKind.ReLimiter, "relimiter.addon64");
        Assert.True((await service.InstallAsync(game, new ReShadeInstallOptions { Channel = ReShadeChannel.NoAddons, ManageIni = false })).Success);
        var relimiter = new GitHubReShadeAddonDeploymentService(new HttpClient(new Handler { HomeUnreachable = true }), store,
            new GitHubReShadeAddonDefinition(ManagedComponentKind.ReLimiter, "ReLimiter", "https://api.github.com/repos/RankFTW/ReLimiter/releases/latest",
                "relimiter.addon64", "relimiter.addon32", 100 * 1024, ManagedComponentKind.DisplayCommander), Path.Combine(_root, "held", "relimiter-cache"));
        var managed = Managed(store, game.InstallPath, relimiter);

        var update = await managed.UpdateManagedAsync(game, ManagedComponentKind.ReLimiter, []);
        Assert.False(update.Success);
        Assert.Equal(ReShadeAddonSwitch.HeldPrefix + "ReLimiter", update.Message);
        var reinstall = await managed.InstallAsync(game, ManagedComponentKind.ReLimiter, []);
        Assert.Equal(ReShadeAddonSwitch.HeldPrefix + "ReLimiter", reinstall.Message);
        Assert.Equal("MZ relimiter.addon64", File.ReadAllText(addon + ReShadeAddonSwitch.OffSuffix));
        Assert.False(File.Exists(addon));

        var rows = new List<ManagedComponent> { new() { Kind = ManagedComponentKind.ReLimiter, DisplayName = "ReLimiter", Availability = ComponentAvailability.Installed, ActionAllowed = true } };
        managed.Decorate(game, rows);
        Assert.Equal(ReShadeAddonSwitch.OffNote, rows[0].Note);
        Assert.True(rows[0].ActionAllowed); // Remove stays offered

        var removed = await managed.RemoveAsync(game, ManagedComponentKind.ReLimiter);
        Assert.True(removed.Success, removed.Message);
        Assert.False(File.Exists(addon + ReShadeAddonSwitch.OffSuffix));
        Assert.Null(store.Find(game, ManagedComponentKind.ReLimiter));
    }

    private static string SeedAddon(DetectedGame game, LinuxDeploymentRecordStore store, ManagedComponentKind kind, string name, bool withIni = false)
    {
        var addon = Path.Combine(game.BinaryDirectory!, name);
        File.WriteAllText(addon, "MZ " + name);
        var files = new List<LinuxDeployedFile> { new() { Path = addon, Sha256 = RHI.Core.Deployment.FileHash.Sha256Hex(addon) } };
        if (withIni)
        {
            var ini = Path.Combine(game.BinaryDirectory!, Path.GetFileNameWithoutExtension(name) + ".ini");
            File.WriteAllText(ini, "[renodx]");
            files.Add(new LinuxDeployedFile { Path = ini, Sha256 = RHI.Core.Deployment.FileHash.Sha256Hex(ini), AllowUserChanges = true });
        }
        store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = kind,
            BinaryDirectory = game.BinaryDirectory!, Version = "1.0", Files = files
        });
        return addon;
    }

    // w3 review nit 7: the No Addons build has its own cache folder, so both builds of one version stay cached and a later
    // add-on install reuses its copy instead of downloading again.
    [Fact]
    public async Task NoAddonsAndAddonBuild_OfOneVersion_AreCachedApart()
    {
        var handler = new Handler { AddonVersion = "6.8.0", NoAddonsVersion = "6.8.0" };
        var (first, firstStore) = CreateGame("cache-addon");
        Assert.True((await NewService(firstStore, handler).InstallAsync(first, new ReShadeInstallOptions { ManageIni = false })).Success);
        var (second, secondStore) = CreateGame("cache-noaddons");
        Assert.True((await NewService(secondStore, handler).InstallAsync(second, new ReShadeInstallOptions { Channel = ReShadeChannel.NoAddons, ManageIni = false })).Success);

        var cache = Path.Combine(_root, "cache");
        Assert.Contains("NO-ADDON-BUILD", File.ReadAllText(Path.Combine(cache, "noaddons", "6.8.0", "ReShade64.dll"), Encoding.Latin1));
        var addonBuild = File.ReadAllText(Path.Combine(cache, "stable", "6.8.0", "ReShade64.dll"), Encoding.Latin1);
        Assert.Contains("ADDON-BUILD", addonBuild);
        Assert.DoesNotContain("NO-ADDON-BUILD", addonBuild);

        var (third, thirdStore) = CreateGame("cache-addon-again");
        Assert.True((await NewService(thirdStore, handler).InstallAsync(third, new ReShadeInstallOptions { ManageIni = false })).Success);
        Assert.Single(handler.Requests, u => u.EndsWith("/downloads/ReShade_Setup_6.8.0_Addon.exe", StringComparison.Ordinal));
        Assert.Single(handler.Requests, u => u.EndsWith("/downloads/ReShade_Setup_6.8.0.exe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddonInstall_OverNoAddonsReShade_IsRefusedAndNotOffered()
    {
        var (game, store) = CreateGame("addon-refused");
        store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.ReShade,
            BinaryDirectory = game.BinaryDirectory!, Version = "6.8.0",
            Files = [new LinuxDeployedFile { Path = Path.Combine(game.BinaryDirectory!, "dxgi.dll") }],
            Metadata = new(StringComparer.OrdinalIgnoreCase) { ["ReShade.Channel"] = nameof(ReShadeChannel.NoAddons) }
        });
        File.WriteAllText(Path.Combine(game.BinaryDirectory!, "dxgi.dll"), "MZ ReShade");
        var managed = Managed(store, game.InstallPath);

        var install = await managed.InstallAsync(game, ManagedComponentKind.RenoDX, Array.Empty<string>());
        Assert.False(install.Success);
        Assert.StartsWith(ReShadeDeploymentService.NoAddonsBlocksAddonPrefix, install.Message);

        var selection = await managed.ApplyReShadeAddonSelectionAsync(game, ["some-addon"]);
        Assert.False(selection.Success);
        Assert.StartsWith(ReShadeDeploymentService.NoAddonsBlocksAddonPrefix, selection.Message);

        var rows = Rows();
        managed.Decorate(game, rows);
        Assert.False(rows[0].ActionAllowed);
        Assert.Equal(ReShadeDeploymentService.NoAddonsBlocksAddonPrefix + "ReLimiter", rows[0].Note);

        // Back on an add-on build, the add-on is offered again with no such note.
        var record = store.Find(game, ManagedComponentKind.ReShade)!;
        record.Metadata["ReShade.Channel"] = nameof(ReShadeChannel.Stable);
        store.Upsert(record);
        rows = Rows();
        managed.Decorate(game, rows);
        Assert.DoesNotContain(ReShadeDeploymentService.NoAddonsBlocksAddonPrefix, rows[0].Note ?? "");

        static List<ManagedComponent> Rows() =>
            [new ManagedComponent { Kind = ManagedComponentKind.ReLimiter, DisplayName = "ReLimiter", Availability = ComponentAvailability.Available, ActionAllowed = true }];
    }

    [Fact]
    public void ChannelOptions_OfferNoAddons_InBothLanguages_AndEveryOptionIsAChannel()
    {
        Assert.Equal("NoAddons", OptionLists.ReShadeChannel.Value("No Addons"));
        Assert.Equal("NoAddons", OptionLists.ReShadeChannel.Value("Sem add-ons"));
        foreach (var value in OptionLists.ReShadeChannel.Values)
            Assert.True(Enum.TryParse<ReShadeChannel>(value, true, out _), value);
        foreach (var channel in Enum.GetValues<ReShadeChannel>())
            Assert.Contains(channel.ToString(), OptionLists.ReShadeChannel.Values);
    }

    // A real (offline) Addon Manager, so a missing guard shows as a different answer, not as a null reference.
    private static LinuxManagedComponentService Managed(LinuxDeploymentRecordStore records, string root, GitHubReShadeAddonDeploymentService? relimiter = null)
    {
        var http = new HttpClient(new Handler { HomeUnreachable = true });
        var addonManager = new ReShadeAddonManagerService(http, new UpstreamRhiManifestService(http), Path.Combine(root, "addon-cache"),
            Path.Combine(root, "custom-addons"), Path.Combine(root, "addon-manager.json"));
        return Managed(records, addonManager, relimiter);
    }

    private static LinuxManagedComponentService Managed(LinuxDeploymentRecordStore records, ReShadeAddonManagerService addonManager,
        GitHubReShadeAddonDeploymentService? relimiter = null)
        => new(records, reshade: null!, addonManager: addonManager, renodx: null!, renodxUpgrade: null!,
            optiscaler: null!, relimiter: relimiter!, displayCommander: null!, mfgAdaUnlock: null!, dlssg2030: null!,
            dlssEnablerStandalone: null!, rtx40Mfg: null!, dlss5Feeder: null!, dlss5Bridge: null!, d3dCompilerTools: null!,
            ultimateAsiLoader: null!, shortFuse: null!, dofFix: null!, reFramework: null!, lumaFramework: null!, lumaPolicy: null!);

    private (DetectedGame Game, LinuxDeploymentRecordStore Store) CreateGame(string name)
    {
        var bin = Path.Combine(_root, name, "game", "bin");
        var prefix = Path.Combine(_root, name, "pfx");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(prefix);
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\Other] 1\n\"x\"=\"y\"\n");
        var game = new DetectedGame
        {
            Name = "Game " + name, InstallPath = Path.GetDirectoryName(bin)!, BinaryDirectory = bin, ExecutablePath = exe,
            PrefixPath = prefix, Runner = "Proton", GraphicsApi = GraphicsApi.DirectX12
        };
        return (game, new LinuxDeploymentRecordStore(Path.Combine(_root, name, "records.json")));
    }

    // One cache for every service in a test, as in the app: the outage test needs the add-on build seeded there.
    private ReShadeDeploymentService NewService(LinuxDeploymentRecordStore store, Handler handler,
        RHI.Core.Deployment.DeploymentJournal? journal = null, WineDllOverrideService? overrides = null)
    {
        var http = new HttpClient(handler);
        var cache = Path.Combine(_root, "cache");
        return new ReShadeDeploymentService(http, new TransactionalFileDeploymentService(), store, overrides ?? new WineDllOverrideService(),
            new MarkingExtractor(), cache,
            new ReShadeShaderPackService(http, Path.Combine(cache, "shader-packs"), customRoot: Path.Combine(cache, "custom-shaders")),
            journal: journal);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string AddonVersion { get; set; } = "6.8.0";
        public string NoAddonsVersion { get; set; } = "6.8.0";
        public bool HomeUnreachable { get; set; }
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            lock (Requests) Requests.Add(uri.AbsoluteUri);
            if (uri.Host == "reshade.me" && uri.AbsolutePath == "/")
            {
                if (HomeUnreachable) throw new HttpRequestException("Connection refused");
                // reshade.me lists both builds, the add-on one first.
                var html = $"<a href=\"/downloads/ReShade_Setup_{AddonVersion}_Addon.exe\">Add-on</a> <a href=\"/downloads/ReShade_Setup_{NoAddonsVersion}.exe\">Standard</a>";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) });
            }
            if (HomeUnreachable) throw new HttpRequestException("Connection refused");
            var setup = new byte[600_000];
            setup[0] = (byte)'M';
            setup[1] = (byte)'Z';
            Encoding.ASCII.GetBytes(uri.AbsolutePath.Contains("_Addon", StringComparison.Ordinal) ? "ADDON-BUILD" : "NO-ADDON-BUILD").CopyTo(setup, 1024);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(setup) });
        }
    }

    // Writes DLLs that carry the build marker of the setup they were extracted from.
    private sealed class MarkingExtractor : IReShadeArchiveExtractor
    {
        public Task ExtractDllsAsync(string setupExe, string outputDirectory, CancellationToken cancellationToken = default)
        {
            var marker = File.ReadAllText(setupExe, Encoding.Latin1).Contains("NO-ADDON-BUILD", StringComparison.Ordinal) ? "NO-ADDON-BUILD" : "ADDON-BUILD";
            Directory.CreateDirectory(outputDirectory);
            Write(Path.Combine(outputDirectory, "ReShade64.dll"), 0x8664, marker);
            Write(Path.Combine(outputDirectory, "ReShade32.dll"), 0x014c, marker);
            return Task.CompletedTask;
        }

        private static void Write(string path, ushort machine, string marker)
        {
            PeArchitectureDetectorTests.WritePe(path, machine, 1_100_000);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            stream.Position = 512;
            stream.Write(Encoding.ASCII.GetBytes("ReShade crosire reshade.me " + marker));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
