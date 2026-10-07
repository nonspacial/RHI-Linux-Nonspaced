using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class ShortFuseDeploymentServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-shortfuse-tests-" + Guid.NewGuid().ToString("N"));

    public ShortFuseDeploymentServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ParseRelease_SelectsNewestShortFuseZipAssetAndVersion()
    {
        var json = """
        [
          { "tag_name":"renodx-dlss5-0.9", "assets":[{"name":"renodx-dlss5.addon64","browser_download_url":"https://example.invalid/old"}] },
          { "tag_name":"renodx-dlss-SF-0.51", "assets":[{"name":"renodx-dlss.addon64","browser_download_url":"https://example.invalid/sf-direct"}] },
          { "tag_name":"renodx-dlss-SF-0.52", "assets":[{"name":"renodx-dlss_SF_0.52.zip","browser_download_url":"https://example.invalid/sf.zip"}] }
        ]
        """;
        var release = ShortFuseDeploymentService.ParseReleaseJson(json);
        Assert.Equal("0.52", release.Version);
        Assert.Equal("https://example.invalid/sf.zip", release.AssetUrl);
        Assert.True(release.IsArchive);
        Assert.Equal("renodx-dlss_SF_0.52.zip", release.AssetName);
    }

    [Fact]
    public void ParseRelease_PinnedTagSelectsExactPublishedShortFuseRelease()
    {
        var json = """
        [
          { "tag_name":"renodx-dlss-SF-0.51", "assets":[{"name":"renodx-dlss.addon64","browser_download_url":"https://example.invalid/sf51"}] },
          { "tag_name":"renodx-dlss-SF-0.52", "assets":[{"name":"renodx-dlss.addon64","browser_download_url":"https://example.invalid/sf52"}] }
        ]
        """;

        var release = ShortFuseDeploymentService.ParseReleaseJson(json, "renodx-dlss-SF-0.51");

        Assert.Equal("0.51", release.Version);
        Assert.Equal("renodx-dlss-SF-0.51", release.Tag);
        Assert.Equal("https://example.invalid/sf51", release.AssetUrl);
    }

    [Fact]
    public void ParseRelease_StripsGithubSha256DigestPrefix_AndKeepsDirectAssetCompatibility()
    {
        var digest = new string('a', 64);
        var json = $$"""[{"tag_name":"renodx-dlss-SF-2.0","assets":[{"name":"renodx-dlss.addon64","browser_download_url":"https://example.invalid/sf","digest":"sha256:{{digest}}"}]}]""";
        var release = ShortFuseDeploymentService.ParseReleaseJson(json);
        Assert.Equal(digest, release.AssetSha256);
        Assert.False(release.IsArchive);
    }

    [Fact]
    public void Settings_DefaultAutoConfigIsOff()
    {
        using var fixture = CreateGame("settings-default");
        var store = new ShortFuseSettingsStore(Path.Combine(fixture.Root, "sf-settings.json"));
        Assert.False(store.IsAutoConfigEnabled(fixture.Game));
    }

    [Fact]
    public void Settings_EnabledPreferencePersistsAndCanBeDisabled()
    {
        using var fixture = CreateGame("settings-persist");
        var path = Path.Combine(fixture.Root, "sf-settings.json");
        var store = new ShortFuseSettingsStore(path);
        store.SetAutoConfigEnabled(fixture.Game, true);
        Assert.True(new ShortFuseSettingsStore(path).IsAutoConfigEnabled(fixture.Game));
        store.SetAutoConfigEnabled(fixture.Game, false);
        Assert.False(new ShortFuseSettingsStore(path).IsAutoConfigEnabled(fixture.Game));
    }

    [Fact]
    public void Settings_EnabledPreferenceWritesVersionedOptInDocument()
    {
        using var fixture = CreateGame("settings-format");
        var path = Path.Combine(fixture.Root, "sf-settings.json");
        var store = new ShortFuseSettingsStore(path);
        store.SetAutoConfigEnabled(fixture.Game, true);

        var text = File.ReadAllText(path);
        Assert.Contains("\"version\": 2", text, StringComparison.Ordinal);
        Assert.Contains("\"enabledGames\"", text, StringComparison.Ordinal);
        Assert.True(new ShortFuseSettingsStore(path).IsAutoConfigEnabled(fixture.Game));
    }

    [Fact]
    public void Settings_LegacyDisabledArrayMigratesFailClosedToOptInOff()
    {
        using var fixture = CreateGame("settings-legacy");
        var path = Path.Combine(fixture.Root, "sf-settings.json");
        File.WriteAllText(path, "[\"some-old-disabled-game\"]");
        Assert.False(new ShortFuseSettingsStore(path).IsAutoConfigEnabled(fixture.Game));
    }

    [Fact]
    public void Settings_CorruptJsonFailsClosedToOptInOff()
    {
        using var fixture = CreateGame("settings-corrupt");
        var path = Path.Combine(fixture.Root, "sf-settings.json");
        File.WriteAllText(path, "{ not-json");
        Assert.False(new ShortFuseSettingsStore(path).IsAutoConfigEnabled(fixture.Game));
    }

    [Fact]
    public void ComponentProbe_DetectsReShadeAsiAndShortFuseSeparately()
    {
        using var fixture = CreateGame("probe");
        var asi = Path.Combine(fixture.Bin, "ReShade64.asi");
        var sf = Path.Combine(fixture.Bin, ShortFuseDeploymentService.AddonFileName);
        File.WriteAllBytes(asi, CreatePe(0x8664, 128 * 1024, 0x51));
        File.WriteAllBytes(sf, Enumerable.Repeat((byte)0x73, 64 * 1024).ToArray());
        var files = new[] { asi, sf, fixture.Game.ExecutablePath! };
        var components = ComponentProbe.Probe(fixture.Game, files);
        Assert.Equal(ComponentAvailability.Installed, components.Single(c => c.Kind == ManagedComponentKind.ReShade).Availability);
        Assert.Equal(ComponentAvailability.Installed, components.Single(c => c.Kind == ManagedComponentKind.ShortFuse).Availability);
    }

    [Fact]
    public async Task Install_BlocksOpenGlEvenWhenExecutableIsX64()
    {
        using var fixture = CreateGame("opengl");
        fixture.Game.GraphicsApi = GraphicsApi.OpenGL;
        var ctx = CreateContext(fixture);
        AddManagedReShade(ctx.Records, fixture);

        var result = await ctx.ShortFuse.InstallAsync(fixture.Game);

        Assert.False(result.Success);
        Assert.Contains("OpenGL", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Install_BlocksWithoutRhiManagedReShade()
    {
        using var fixture = CreateGame("nors");
        var ctx = CreateContext(fixture);
        var result = await ctx.ShortFuse.InstallAsync(fixture.Game);
        Assert.False(result.Success);
        Assert.Contains("RHI-managed ReShade", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Install_BlocksManagedOptiScalerChain()
    {
        using var fixture = CreateGame("opti");
        var ctx = CreateContext(fixture);
        AddManagedReShade(ctx.Records, fixture);
        ctx.Records.Upsert(NewRecord(fixture, ManagedComponentKind.OptiScaler));
        var result = await ctx.ShortFuse.InstallAsync(fixture.Game);
        Assert.False(result.Success);
        Assert.Contains("OptiScaler", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Install_BlocksManagedFeederStack()
    {
        using var fixture = CreateGame("feeder");
        var ctx = CreateContext(fixture);
        AddManagedReShade(ctx.Records, fixture);
        ctx.Records.Upsert(NewRecord(fixture, ManagedComponentKind.Dlss5Feeder));
        var result = await ctx.ShortFuse.InstallAsync(fixture.Game);
        Assert.False(result.Success);
        Assert.Contains("Feeder", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Install_BlocksManagedDx11BridgeStack()
    {
        using var fixture = CreateGame("bridge");
        var ctx = CreateContext(fixture);
        AddManagedReShade(ctx.Records, fixture);
        ctx.Records.Upsert(NewRecord(fixture, ManagedComponentKind.Dlss5Dx11Bridge));
        var result = await ctx.ShortFuse.InstallAsync(fixture.Game);
        Assert.False(result.Success);
        Assert.Contains("Bridge", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Install_ProtectsExternalOrStaleShortFuseAddon()
    {
        using var fixture = CreateGame("external-sf");
        var ctx = CreateContext(fixture);
        AddManagedReShade(ctx.Records, fixture);
        var addon = Path.Combine(fixture.Bin, ShortFuseDeploymentService.AddonFileName);
        File.WriteAllText(addon, "external-shortfuse");
        var result = await ctx.ShortFuse.InstallAsync(fixture.Game);
        Assert.False(result.Success);
        Assert.Contains("external or stale", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("external-shortfuse", File.ReadAllText(addon));
    }

    [Fact]
    public async Task Install_BlocksExistingDirectDlssOriginalOwnershipCycle()
    {
        using var fixture = CreateGame("direct-swap");
        var ctx = CreateContext(fixture);
        AddManagedReShade(ctx.Records, fixture);
        var dlss = Path.Combine(fixture.Bin, "nvngx_dlss.dll");
        File.WriteAllBytes(dlss, CreatePe(0x8664, 80 * 1024, 0x22));
        File.WriteAllBytes(dlss + TransactionalFileDeploymentService.BackupExtension, CreatePe(0x8664, 80 * 1024, 0x21));
        var result = await ctx.ShortFuse.InstallAsync(fixture.Game);
        Assert.False(result.Success);
        Assert.Contains(".original ownership cycle", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallAndRemove_RestoresReShadeIniUalOverrideAndDlssStack()
    {
        using var fixture = CreateGame("roundtrip");
        var ctx = CreateContext(fixture, fullNetwork: true);
        var reshadePath = AddManagedReShade(ctx.Records, fixture, withWineOverride: true);
        var reshadeBytes = File.ReadAllBytes(reshadePath);
        var ini = Path.Combine(fixture.Bin, "ReShade.ini");
        File.WriteAllText(ini, "[INSTALL]\nHookStreamline=0\nOtherSetting=KeepMe\n");

        var install = await ctx.ShortFuse.InstallAsync(fixture.Game, autoConfigEnabled: true);
        Assert.True(install.Success, install.Message);
        var asi = Path.Combine(fixture.Bin, ShortFuseDeploymentService.ReShadeAsiName);
        Assert.True(File.Exists(asi));
        Assert.False(File.Exists(reshadePath));
        Assert.Equal(reshadeBytes, File.ReadAllBytes(asi));
        Assert.Contains("HookStreamline=1", File.ReadAllText(ini));
        Assert.Contains("HookDirectX=1", File.ReadAllText(ini));
        Assert.Equal("winmm.dll", ctx.Ual.InstalledProxyName(fixture.Game));
        Assert.Null(WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "dxgi", "Game.exe"));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "winmm", "Game.exe"));
        Assert.True(File.Exists(Path.Combine(fixture.Bin, "nvngx_dlssg.dll")));
        Assert.NotNull(ctx.Records.Find(fixture.Game, ManagedComponentKind.ShortFuse));

        var remove = await ctx.ShortFuse.RemoveAsync(fixture.Game);
        Assert.True(remove.Success, remove.Message);
        Assert.True(File.Exists(reshadePath));
        Assert.Equal(reshadeBytes, File.ReadAllBytes(reshadePath));
        Assert.False(File.Exists(asi));
        var restoredIni = File.ReadAllText(ini);
        Assert.Contains("HookStreamline=0", restoredIni);
        Assert.DoesNotContain("hookdirectx=", restoredIni.ToLowerInvariant());
        Assert.Contains("OtherSetting=KeepMe", restoredIni);
        Assert.Null(ctx.Ual.GetRecord(fixture.Game));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "dxgi", "Game.exe"));
        Assert.Null(WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "winmm", "Game.exe"));
        Assert.False(File.Exists(Path.Combine(fixture.Bin, "nvngx_dlssg.dll")));
        Assert.Null(ctx.Records.Find(fixture.Game, ManagedComponentKind.ShortFuse));
    }

    [Fact]
    public async Task Remove_ModifiedShortFuseOwnedFile_BlocksBeforeTeardown()
    {
        using var fixture = CreateGame("modified");
        var ctx = CreateContext(fixture, fullNetwork: true);
        var reshadePath = AddManagedReShade(ctx.Records, fixture, withWineOverride: true);
        var install = await ctx.ShortFuse.InstallAsync(fixture.Game);
        Assert.True(install.Success, install.Message);
        var addon = Path.Combine(fixture.Bin, ShortFuseDeploymentService.AddonFileName);
        File.AppendAllText(addon, "external-change");

        var remove = await ctx.ShortFuse.RemoveAsync(fixture.Game);
        Assert.False(remove.Success);
        Assert.Contains("modified outside RHI", remove.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(fixture.Bin, ShortFuseDeploymentService.ReShadeAsiName)));
        Assert.False(File.Exists(reshadePath));
        Assert.NotNull(ctx.Records.Find(fixture.Game, ManagedComponentKind.ShortFuse));
    }


    [Fact]
    public async Task Remove_ModifiedOwnedDlssPayload_BlocksBeforeTeardown()
    {
        using var fixture = CreateGame("modified-dlss");
        var ctx = CreateContext(fixture, fullNetwork: true);
        AddManagedReShade(ctx.Records, fixture, withWineOverride: true);
        var install = await ctx.ShortFuse.InstallAsync(fixture.Game);
        Assert.True(install.Success, install.Message);
        var dlss = Path.Combine(fixture.Bin, "nvngx_dlssg.dll");
        File.AppendAllText(dlss, "external-change");

        var remove = await ctx.ShortFuse.RemoveAsync(fixture.Game);
        Assert.False(remove.Success);
        Assert.Contains("modified outside RHI", remove.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(fixture.Bin, ShortFuseDeploymentService.ReShadeAsiName)));
        Assert.NotNull(ctx.Records.Find(fixture.Game, ManagedComponentKind.ShortFuse));
    }

    [Fact]
    public async Task Remove_ModifiedOwnedStreamlinePayload_BlocksBeforeTeardown()
    {
        using var fixture = CreateGame("modified-streamline");
        var ctx = CreateContext(fixture, fullNetwork: true);
        AddManagedReShade(ctx.Records, fixture, withWineOverride: true);
        var install = await ctx.ShortFuse.InstallAsync(fixture.Game);
        Assert.True(install.Success, install.Message);
        var streamline = Path.Combine(fixture.Bin, "sl.common.dll");
        File.AppendAllText(streamline, "external-change");

        var remove = await ctx.ShortFuse.RemoveAsync(fixture.Game);
        Assert.False(remove.Success);
        Assert.Contains("modified outside RHI", remove.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(fixture.Bin, ShortFuseDeploymentService.ReShadeAsiName)));
        Assert.NotNull(ctx.Records.Find(fixture.Game, ManagedComponentKind.ShortFuse));
    }

    [Fact]
    public async Task InstallAndRemove_PreexistingManagedUal_IsPreserved()
    {
        using var fixture = CreateGame("existing-ual");
        var ctx = CreateContext(fixture, fullNetwork: true);
        AddManagedReShade(ctx.Records, fixture, withWineOverride: true);

        var ualInstall = await ctx.Ual.InstallAsync(fixture.Game, "version.dll");
        Assert.True(ualInstall.Success, ualInstall.Message);
        var ualRecordBefore = ctx.Ual.GetRecord(fixture.Game);
        Assert.NotNull(ualRecordBefore);
        var ualPath = Path.Combine(fixture.Bin, "version.dll");
        var ualHash = Sha256(ualPath);

        var install = await ctx.ShortFuse.InstallAsync(fixture.Game);
        Assert.True(install.Success, install.Message);
        var sfRecord = ctx.Records.Find(fixture.Game, ManagedComponentKind.ShortFuse);
        Assert.NotNull(sfRecord);
        Assert.Equal("0", sfRecord!.Metadata[ShortFuseDeploymentService.MetaUalInstalledByShortFuse]);
        Assert.Equal("version.dll", sfRecord.Metadata[ShortFuseDeploymentService.MetaUalProxyName]);

        var remove = await ctx.ShortFuse.RemoveAsync(fixture.Game);
        Assert.True(remove.Success, remove.Message);
        Assert.NotNull(ctx.Ual.GetRecord(fixture.Game));
        Assert.True(File.Exists(ualPath));
        Assert.Equal(ualHash, Sha256(ualPath));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(
            File.ReadAllText(Path.Combine(fixture.Prefix, "user.reg")), "version", "Game.exe"));
    }

    // ── ZZZ load order (upstream 2.7.9) ──────────────────────────────────────

    [Fact]
    public void Settings_ZzzLoadOrderDefaultsOffAndPersistsIndependentlyOfAutoConfig()
    {
        using var fixture = CreateGame("zzz-settings");
        var path = Path.Combine(fixture.Root, "sf-settings.json");
        var store = new ShortFuseSettingsStore(path);
        Assert.False(store.IsZzzLoadOrderEnabled(fixture.Game));

        store.SetZzzLoadOrderEnabled(fixture.Game, true);
        store.SetAutoConfigEnabled(fixture.Game, true);
        store.SetAutoConfigEnabled(fixture.Game, false);

        var reloaded = new ShortFuseSettingsStore(path);
        Assert.True(reloaded.IsZzzLoadOrderEnabled(fixture.Game));
        Assert.False(reloaded.IsAutoConfigEnabled(fixture.Game));
        reloaded.SetZzzLoadOrderEnabled(fixture.Game, false);
        Assert.False(new ShortFuseSettingsStore(path).IsZzzLoadOrderEnabled(fixture.Game));
    }

    [Fact]
    public void ComponentProbe_RecognisesZzzShortFuseName()
    {
        using var fixture = CreateGame("zzz-probe");
        var sf = Path.Combine(fixture.Bin, ShortFuseDeploymentService.ZzzAddonFileName);
        File.WriteAllBytes(sf, new byte[16]);
        var components = ComponentProbe.Probe(fixture.Game, new[] { sf, fixture.Game.ExecutablePath! });
        Assert.Equal(ComponentAvailability.Installed, components.Single(c => c.Kind == ManagedComponentKind.ShortFuse).Availability);
    }

    [Fact]
    public async Task Install_WithZzzPreference_DeploysOnlyZzzNameAndRemoveLeavesNeither()
    {
        using var fixture = CreateGame("zzz-install");
        var ctx = CreateContext(fixture, fullNetwork: true);
        AddManagedReShade(ctx.Records, fixture, withWineOverride: true);
        Assert.True((await ctx.ShortFuse.SetZzzLoadOrderAsync(fixture.Game, true)).Success);

        var install = await ctx.ShortFuse.InstallAsync(fixture.Game);
        Assert.True(install.Success, install.Message);
        var (normal, zzz) = AddonPaths(fixture);
        Assert.False(File.Exists(normal));
        Assert.True(File.Exists(zzz));
        Assert.Contains(ctx.Records.Find(fixture.Game, ManagedComponentKind.ShortFuse)!.Files, f => f.Path == zzz);

        var remove = await ctx.ShortFuse.RemoveAsync(fixture.Game);
        Assert.True(remove.Success, remove.Message);
        Assert.False(File.Exists(normal));
        Assert.False(File.Exists(zzz));
    }

    [Fact]
    public async Task ToggleOnInstalledShortFuse_RenamesFileAndRecordInBothDirections()
    {
        using var fixture = CreateGame("zzz-toggle");
        var ctx = CreateContext(fixture, fullNetwork: true);
        AddManagedReShade(ctx.Records, fixture, withWineOverride: true);
        Assert.True((await ctx.ShortFuse.InstallAsync(fixture.Game)).Success);
        var (normal, zzz) = AddonPaths(fixture);
        var hash = Sha256(normal);

        var on = await ctx.ShortFuse.SetZzzLoadOrderAsync(fixture.Game, true);
        Assert.True(on.Success, on.Message);
        Assert.False(File.Exists(normal));
        Assert.Equal(hash, Sha256(zzz));
        var entry = ctx.Records.Find(fixture.Game, ManagedComponentKind.ShortFuse)!.Files.Single(f => ShortFuseDeploymentService.IsAddonFileName(Path.GetFileName(f.Path)));
        Assert.Equal(zzz, entry.Path);
        Assert.Equal(hash, entry.Sha256);
        Assert.True(ctx.ShortFuse.GetZzzLoadOrderEnabled(fixture.Game));

        var off = await ctx.ShortFuse.SetZzzLoadOrderAsync(fixture.Game, false);
        Assert.True(off.Success, off.Message);
        Assert.True(File.Exists(normal));
        Assert.False(File.Exists(zzz));
        Assert.Contains(ctx.Records.Find(fixture.Game, ManagedComponentKind.ShortFuse)!.Files, f => f.Path == normal);
        Assert.False(ctx.ShortFuse.GetZzzLoadOrderEnabled(fixture.Game));
    }

    [Fact]
    public async Task Toggle_ModifiedAddon_IsBlockedAndLeavesFileRecordAndPreference()
    {
        using var fixture = CreateGame("zzz-modified");
        var ctx = CreateContext(fixture, fullNetwork: true);
        AddManagedReShade(ctx.Records, fixture, withWineOverride: true);
        Assert.True((await ctx.ShortFuse.InstallAsync(fixture.Game)).Success);
        var (normal, zzz) = AddonPaths(fixture);
        File.AppendAllText(normal, "external-change");

        var result = await ctx.ShortFuse.SetZzzLoadOrderAsync(fixture.Game, true);

        Assert.False(result.Success);
        Assert.Contains("modified outside RHI", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(normal));
        Assert.False(File.Exists(zzz));
        Assert.False(ctx.ShortFuse.GetZzzLoadOrderEnabled(fixture.Game));
    }

    [Fact]
    public async Task ExternalAddonUnderOtherName_BlocksInstallAndRename()
    {
        using var fixture = CreateGame("zzz-external");
        var ctx = CreateContext(fixture, fullNetwork: true);
        AddManagedReShade(ctx.Records, fixture, withWineOverride: true);
        var (normal, zzz) = AddonPaths(fixture);
        File.WriteAllText(zzz, "external-zzz");
        var install = await ctx.ShortFuse.InstallAsync(fixture.Game);
        Assert.False(install.Success);
        Assert.Contains("external or stale", install.Message, StringComparison.OrdinalIgnoreCase);

        File.Delete(zzz);
        Assert.True((await ctx.ShortFuse.InstallAsync(fixture.Game)).Success);
        File.WriteAllText(zzz, "external-zzz");
        var rename = await ctx.ShortFuse.SetZzzLoadOrderAsync(fixture.Game, true);
        Assert.False(rename.Success);
        Assert.True(File.Exists(normal));
        Assert.Equal("external-zzz", File.ReadAllText(zzz));
    }

    [Fact]
    public async Task Remove_FollowsAddonWhenRecordMissedTheRename()
    {
        using var fixture = CreateGame("zzz-stale-record");
        var ctx = CreateContext(fixture, fullNetwork: true);
        AddManagedReShade(ctx.Records, fixture, withWineOverride: true);
        Assert.True((await ctx.ShortFuse.InstallAsync(fixture.Game)).Success);
        var (normal, zzz) = AddonPaths(fixture);
        File.Move(normal, zzz); // crash between rename and record write

        var remove = await ctx.ShortFuse.RemoveAsync(fixture.Game);

        Assert.True(remove.Success, remove.Message);
        Assert.False(File.Exists(normal));
        Assert.False(File.Exists(zzz));
    }

    [Fact]
    public async Task Toggle_CorruptLedger_RefusesBeforeTouchingTheAddon()
    {
        using var fixture = CreateGame("zzz-corrupt-ledger");
        var ctx = CreateContext(fixture, fullNetwork: true);
        AddManagedReShade(ctx.Records, fixture, withWineOverride: true);
        Assert.True((await ctx.ShortFuse.InstallAsync(fixture.Game)).Success);
        var (normal, zzz) = AddonPaths(fixture);
        File.WriteAllText(LedgerPath(fixture), "{ not json");

        var result = await ctx.ShortFuse.SetZzzLoadOrderAsync(fixture.Game, true);

        Assert.False(result.Success);
        Assert.Contains("ledger is unreadable", result.Message, StringComparison.Ordinal);
        Assert.Contains("nothing was renamed", result.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(normal));
        Assert.False(File.Exists(zzz));
        Assert.False(ctx.ShortFuse.GetZzzLoadOrderEnabled(fixture.Game));
        AssertPresentedInPortuguese(result.Message);
    }

    [Fact]
    public async Task Toggle_RecordWriteFails_RenamesBackAndFileAndRecordAgree()
    {
        using var fixture = CreateGame("zzz-ledger-readonly");
        var ctx = CreateContext(fixture, fullNetwork: true);
        AddManagedReShade(ctx.Records, fixture, withWineOverride: true);
        Assert.True((await ctx.ShortFuse.InstallAsync(fixture.Game)).Success);
        var (normal, zzz) = AddonPaths(fixture);

        ComponentOperationResult result;
        using (ReadOnlyLedgerDirectory(fixture))
            result = await ctx.ShortFuse.SetZzzLoadOrderAsync(fixture.Game, true);

        Assert.False(result.Success);
        Assert.Contains($"renamed back to {ShortFuseDeploymentService.AddonFileName}", result.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(normal));
        Assert.False(File.Exists(zzz));
        // Both the ledger on disk and the store's in-memory cache still name the file that exists.
        Assert.Equal(normal, AddonEntryPath(new LinuxDeploymentRecordStore(LedgerPath(fixture)), fixture));
        Assert.Equal(normal, AddonEntryPath(ctx.Records, fixture));
        Assert.False(ctx.ShortFuse.GetZzzLoadOrderEnabled(fixture.Game));
        Assert.False(ctx.ShortFuse.GetZzzLoadOrderInEffect(fixture.Game));
        AssertPresentedInPortuguese(result.Message);
    }

    [Fact]
    public async Task Toggle_RecordWriteAndRollbackFail_ReportsTheSplitStateHonestly()
    {
        using var fixture = CreateGame("zzz-rollback-fails");
        var ctx = CreateContext(fixture, fullNetwork: true);
        AddManagedReShade(ctx.Records, fixture, withWineOverride: true);
        Assert.True((await ctx.ShortFuse.InstallAsync(fixture.Game)).Success);
        var (normal, zzz) = AddonPaths(fixture);
        var realMove = ctx.ShortFuse.MoveAddonFile;
        var moves = 0;
        ctx.ShortFuse.MoveAddonFile = (from, to) =>
        {
            if (++moves == 2) throw new IOException("simulated rollback failure");
            realMove(from, to);
        };

        ComponentOperationResult result;
        using (ReadOnlyLedgerDirectory(fixture))
            result = await ctx.ShortFuse.SetZzzLoadOrderAsync(fixture.Game, true);

        Assert.False(result.Success);
        Assert.Contains($"now named {ShortFuseDeploymentService.ZzzAddonFileName}", result.Message, StringComparison.Ordinal);
        Assert.Contains($"still says {ShortFuseDeploymentService.AddonFileName}", result.Message, StringComparison.Ordinal);
        Assert.Contains("simulated rollback failure", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("renamed back", result.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(normal));
        Assert.True(File.Exists(zzz));
        Assert.Equal(normal, AddonEntryPath(new LinuxDeploymentRecordStore(LedgerPath(fixture)), fixture));
        // The checkbox follows the file ReShade will load, not the stale preference.
        Assert.False(ctx.ShortFuse.GetZzzLoadOrderEnabled(fixture.Game));
        Assert.True(ctx.ShortFuse.GetZzzLoadOrderInEffect(fixture.Game));
        AssertPresentedInPortuguese(result.Message);

        // Once the ledger is writable, switching the toggle back heals file, record and preference.
        ctx.ShortFuse.MoveAddonFile = realMove;
        var heal = await ctx.ShortFuse.SetZzzLoadOrderAsync(fixture.Game, false);
        Assert.True(heal.Success, heal.Message);
        Assert.True(File.Exists(normal));
        Assert.False(File.Exists(zzz));
        Assert.Equal(normal, AddonEntryPath(new LinuxDeploymentRecordStore(LedgerPath(fixture)), fixture));
    }

    [Fact]
    public async Task Toggle_AfterInterruptedRename_PersistsRepairedRecord()
    {
        using var fixture = CreateGame("zzz-interrupted-toggle");
        var ctx = CreateContext(fixture, fullNetwork: true);
        AddManagedReShade(ctx.Records, fixture, withWineOverride: true);
        Assert.True((await ctx.ShortFuse.InstallAsync(fixture.Game)).Success);
        var (normal, zzz) = AddonPaths(fixture);
        var hash = Sha256(normal);
        File.Move(normal, zzz); // crash between rename and record write: record and preference still say normal
        Assert.False(ctx.ShortFuse.GetZzzLoadOrderEnabled(fixture.Game));
        Assert.True(ctx.ShortFuse.GetZzzLoadOrderInEffect(fixture.Game));

        var on = await ctx.ShortFuse.SetZzzLoadOrderAsync(fixture.Game, true);

        Assert.True(on.Success, on.Message);
        Assert.True(File.Exists(zzz));
        Assert.False(File.Exists(normal));
        Assert.Equal(hash, Sha256(zzz));
        Assert.Equal(zzz, AddonEntryPath(new LinuxDeploymentRecordStore(LedgerPath(fixture)), fixture));
        Assert.True(ctx.ShortFuse.GetZzzLoadOrderEnabled(fixture.Game));

        var off = await ctx.ShortFuse.SetZzzLoadOrderAsync(fixture.Game, false);
        Assert.True(off.Success, off.Message);
        Assert.True(File.Exists(normal));
        Assert.False(File.Exists(zzz));
        Assert.Equal(normal, AddonEntryPath(new LinuxDeploymentRecordStore(LedgerPath(fixture)), fixture));
    }

    [Fact]
    public async Task Toggle_BackToRecordedNameAfterInterruptedRename_NeedsNoLedgerWrite()
    {
        using var fixture = CreateGame("zzz-interrupted-back");
        var ctx = CreateContext(fixture, fullNetwork: true);
        AddManagedReShade(ctx.Records, fixture, withWineOverride: true);
        Assert.True((await ctx.ShortFuse.InstallAsync(fixture.Game)).Success);
        var (normal, zzz) = AddonPaths(fixture);
        File.Move(normal, zzz); // record still says normal

        ComponentOperationResult off;
        using (ReadOnlyLedgerDirectory(fixture))
            off = await ctx.ShortFuse.SetZzzLoadOrderAsync(fixture.Game, false);

        Assert.True(off.Success, off.Message);
        Assert.True(File.Exists(normal));
        Assert.False(File.Exists(zzz));
        Assert.Equal(normal, AddonEntryPath(new LinuxDeploymentRecordStore(LedgerPath(fixture)), fixture));
        Assert.False(ctx.ShortFuse.GetZzzLoadOrderInEffect(fixture.Game));
    }

    [Fact]
    public async Task Toggle_BothNamesPresent_RefusesWithoutDataLoss()
    {
        using var fixture = CreateGame("zzz-both-names");
        var ctx = CreateContext(fixture, fullNetwork: true);
        AddManagedReShade(ctx.Records, fixture, withWineOverride: true);
        Assert.True((await ctx.ShortFuse.InstallAsync(fixture.Game)).Success);
        var (normal, zzz) = AddonPaths(fixture);
        var hash = Sha256(normal);
        File.WriteAllText(zzz, "user-zzz");

        foreach (var enabled in new[] { true, false })
        {
            var result = await ctx.ShortFuse.SetZzzLoadOrderAsync(fixture.Game, enabled);
            Assert.False(result.Success);
            Assert.Contains("ReShade would load ShortFuse twice", result.Message, StringComparison.Ordinal);
            AssertPresentedInPortuguese(result.Message);
        }

        Assert.Equal(hash, Sha256(normal));
        Assert.Equal("user-zzz", File.ReadAllText(zzz));
        Assert.Equal(normal, AddonEntryPath(new LinuxDeploymentRecordStore(LedgerPath(fixture)), fixture));
        Assert.False(ctx.ShortFuse.GetZzzLoadOrderEnabled(fixture.Game));
    }

    [Fact]
    public async Task Remove_AfterInterruptedRename_PersistsRepairedRecordEvenWhenBlockedLater()
    {
        using var fixture = CreateGame("zzz-remove-repair");
        var ctx = CreateContext(fixture, fullNetwork: true);
        AddManagedReShade(ctx.Records, fixture, withWineOverride: true);
        Assert.True((await ctx.ShortFuse.InstallAsync(fixture.Game)).Success);
        var (normal, zzz) = AddonPaths(fixture);
        File.Move(normal, zzz);
        ctx.Records.Remove(fixture.Game, ManagedComponentKind.ReShade); // makes Remove block after the follow

        var remove = await ctx.ShortFuse.RemoveAsync(fixture.Game);

        Assert.False(remove.Success);
        Assert.True(File.Exists(zzz));
        Assert.Equal(zzz, AddonEntryPath(new LinuxDeploymentRecordStore(LedgerPath(fixture)), fixture));
    }

    private static string LedgerPath(GameFixture fixture) => Path.Combine(fixture.Root, "deployments.json");

    private static string AddonEntryPath(LinuxDeploymentRecordStore store, GameFixture fixture)
        => store.Find(fixture.Game, ManagedComponentKind.ShortFuse)!.Files
            .Single(f => ShortFuseDeploymentService.IsAddonFileName(Path.GetFileName(f.Path))).Path;

    private static void AssertPresentedInPortuguese(string message)
    {
        var pt = UiLocalizationService.PresentFor("pt-BR", message);
        Assert.NotEqual(UiLocalizationService.TextFor("pt-BR", "Ui.UnlocalizedPlatformMessage"), pt);
        Assert.NotEqual(message, pt);
    }

    /// <summary>Makes the ledger's directory read-only so the record write fails; the game directory beneath it stays writable.</summary>
    private static IDisposable ReadOnlyLedgerDirectory(GameFixture fixture)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Unix permission test");
        var mode = File.GetUnixFileMode(fixture.Root);
        File.SetUnixFileMode(fixture.Root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        return new Restore(() => { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(fixture.Root, mode); });
    }

    private sealed class Restore(Action undo) : IDisposable { public void Dispose() => undo(); }

    private static (string Normal, string Zzz) AddonPaths(GameFixture fixture)
    {
        var dir = RenoDxDeploymentService.ResolveAddonDeployPath(fixture.Bin);
        return (Path.Combine(dir, ShortFuseDeploymentService.AddonFileName), Path.Combine(dir, ShortFuseDeploymentService.ZzzAddonFileName));
    }

    private Context CreateContext(GameFixture fixture, bool fullNetwork = false)
    {
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var deployment = new TransactionalFileDeploymentService();
        var overrides = new WineDllOverrideService();
        var handler = fullNetwork ? CreateFullHandler() : new MapHandler(new Dictionary<string, ResponseTemplate>());
        var http = new HttpClient(handler);
        var dlss = new DlssManagerService(http, deployment, Path.Combine(fixture.Root, "cache-dlss"));
        var ual = new UltimateAsiLoaderDeploymentService(http, records, overrides, Path.Combine(fixture.Root, "cache-ual"));
        // Own directory, so tests can make the ledger's directory read-only without blocking settings writes.
        var settingsDir = Directory.CreateDirectory(Path.Combine(fixture.Root, "settings")).FullName;
        var settings = new ShortFuseSettingsStore(Path.Combine(settingsDir, "sf-settings.json"));
        var sf = new ShortFuseDeploymentService(http, deployment, records, overrides, ual, dlss, Path.Combine(fixture.Root, "cache-sf"), settings);
        return new(records, ual, sf, http);
    }

    private string AddManagedReShade(LinuxDeploymentRecordStore records, GameFixture fixture, bool withWineOverride = false)
    {
        var path = Path.Combine(fixture.Bin, "dxgi.dll");
        var bytes = CreatePe(0x8664, 128 * 1024, 0x44);
        File.WriteAllBytes(path, bytes);
        WineOverrideRecord? wine = null;
        if (withWineOverride)
        {
            var svc = new WineDllOverrideService();
            wine = svc.EnsureNativeBuiltin(fixture.Prefix, "dxgi.dll", "Game.exe");
        }
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(fixture.Game), GameName = fixture.Game.Name,
            Component = ManagedComponentKind.ReShade, BinaryDirectory = fixture.Bin, Version = "6.5.1",
            WineOverride = wine,
            Files = [new LinuxDeployedFile { Path = path, ExistedBeforeInstall = false, Sha256 = Sha256(path) }],
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["ReShade.Channel"] = "Stable" }
        });
        return path;
    }

    private static LinuxDeploymentRecord NewRecord(GameFixture fixture, ManagedComponentKind kind) => new()
    {
        GameKey = LinuxDeploymentRecordStore.GameKey(fixture.Game), GameName = fixture.Game.Name,
        Component = kind, BinaryDirectory = fixture.Bin, Files = []
    };

    private GameFixture CreateGame(string name)
    {
        var dir = Path.Combine(_root, name + "-" + Guid.NewGuid().ToString("N"));
        var bin = Path.Combine(dir, "game");
        var prefix = Path.Combine(dir, "pfx");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(prefix);
        var exe = Path.Combine(bin, "Game.exe");
        File.WriteAllBytes(exe, CreatePe(0x8664, 80 * 1024, 0x11));
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\Other] 1\n\"x\"=\"y\"\n");
        return new GameFixture(dir, bin, prefix, new DetectedGame
        {
            Name = "ShortFuse Test " + name, InstallPath = dir, Source = "Test", Runner = "Wine",
            PrefixPath = prefix, ExecutablePath = exe, BinaryDirectory = bin, GraphicsApi = GraphicsApi.DirectX12
        });
    }

    private static HttpMessageHandler CreateFullHandler()
    {
        var sf = Enumerable.Range(0, 80 * 1024).Select(i => (byte)(i * 31 + 7)).ToArray();
        var sfZip = CreateZip(("payload/renodx-dlss.addon64", sf));
        var sfHash = Convert.ToHexString(SHA256.HashData(sfZip)).ToLowerInvariant();
        var ualZip = CreateZip(("dinput8.dll", CreatePe(0x8664, 80 * 1024, 0x62)));
        var ualHash = Convert.ToHexString(SHA256.HashData(ualZip)).ToLowerInvariant();
        var sr = CreateZip(("nvngx_dlss.dll", CreatePe(0x8664, 80 * 1024, 0x71)));
        var rr = CreateZip(("nvngx_dlssd.dll", CreatePe(0x8664, 80 * 1024, 0x72)));
        var fg = CreateZip(("nvngx_dlssg.dll", CreatePe(0x8664, 80 * 1024, 0x73)));
        var nr = CreateZip(("nvngx_dlssnr.dll", CreatePe(0x8664, 80 * 1024, 0x74)));
        var sl = CreateZip(
            ("sl.interposer.dll", CreatePe(0x8664, 80 * 1024, 0x75)),
            ("sl.common.dll", CreatePe(0x8664, 80 * 1024, 0x76)));
        var sfUrl = "https://example.invalid/renodx-dlss_SF_1.4.2.zip";
        var ualUrl = "https://example.invalid/ual.zip";
        var srUrl = "https://github.com/example/rhi/releases/download/1/sr.zip";
        var rrUrl = "https://github.com/example/rhi/releases/download/1/rr.zip";
        var fgUrl = "https://github.com/example/rhi/releases/download/1/fg.zip";
        var nrUrl = "https://github.com/example/rhi/releases/download/1/nr.zip";
        var slUrl = "https://github.com/example/rhi/releases/download/1/sl.zip";
        var sfJson = $$"""[{"tag_name":"renodx-dlss-SF-1.4.2","assets":[{"name":"renodx-dlss_SF_1.4.2.zip","browser_download_url":"{{sfUrl}}","digest":"sha256:{{sfHash}}"}]}]""";
        var ualJson = $$"""{"tag_name":"v9.7.4","assets":[{"name":"Ultimate-ASI-Loader_x64.zip","browser_download_url":"{{ualUrl}}","digest":"sha256:{{ualHash}}"}]}""";
        var manifest = $$"""
        {"dlss":[{"version":"9.0","url":"{{srUrl}}"}],"dlssd":[{"version":"9.0","url":"{{rrUrl}}"}],"dlssg":[{"version":"9.0","url":"{{fgUrl}}"}],"dlssnr":[{"version":"9.0","url":"{{nrUrl}}"}],"streamline":[{"version":"2.9","url":"{{slUrl}}"}]}
        """;
        return new MapHandler(new Dictionary<string, ResponseTemplate>
        {
            [ShortFuseDeploymentService.ReleasesApiUrl] = ResponseTemplate.FromJson(sfJson),
            [sfUrl] = ResponseTemplate.FromBytes(sfZip),
            [UltimateAsiLoaderDeploymentService.GitHubApiUrl] = ResponseTemplate.FromJson(ualJson),
            [ualUrl] = ResponseTemplate.FromBytes(ualZip),
            [DlssManagerService.ManifestUrl] = ResponseTemplate.FromJson(manifest),
            [srUrl] = ResponseTemplate.FromBytes(sr), [rrUrl] = ResponseTemplate.FromBytes(rr), [fgUrl] = ResponseTemplate.FromBytes(fg), [nrUrl] = ResponseTemplate.FromBytes(nr), [slUrl] = ResponseTemplate.FromBytes(sl)
        });
    }

    private static byte[] CreateZip(params (string Name, byte[] Bytes)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = zip.CreateEntry(file.Name, CompressionLevel.Fastest);
                using var stream = entry.Open();
                stream.Write(file.Bytes);
            }
        }
        return ms.ToArray();
    }

    private static byte[] CreatePe(ushort machine, int size, byte marker)
    {
        var bytes = Enumerable.Range(0, size).Select(i => (byte)(marker + i * 13)).ToArray();
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
        bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E'; bytes[0x82] = 0; bytes[0x83] = 0;
        BitConverter.GetBytes(machine).CopyTo(bytes, 0x84);
        return bytes;
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    [Fact]
    public async Task EnsureCachedAddon_ForeignDirectAddonUnderCachedName_IsReplacedNeverDeployed()
    {
        // #142 review: a direct .addon64 release with no published digest used to be reused by name alone.
        var fixture = CreateGame("foreign-cache");
        var genuine = CreatePe(0x8664, 80 * 1024, 0x5A);
        const string url = "https://example.invalid/sf/renodx-dlss.addon64";
        var gets = 0;
        using var http = new HttpClient(new CountingHandler(url, genuine, () => Interlocked.Increment(ref gets)));
        var records = new LinuxDeploymentRecordStore(Path.Combine(fixture.Root, "deployments.json"));
        var overrides = new WineDllOverrideService();
        var deployment = new TransactionalFileDeploymentService();
        var cacheRoot = Path.Combine(fixture.Root, "cache-sf");
        var sf = new ShortFuseDeploymentService(http, deployment, records, overrides,
            new UltimateAsiLoaderDeploymentService(http, records, overrides, Path.Combine(fixture.Root, "cache-ual")),
            new DlssManagerService(http, deployment, Path.Combine(fixture.Root, "cache-dlss")),
            cacheRoot, new ShortFuseSettingsStore(Path.Combine(fixture.Root, "sf-settings.json")));
        var release = new ShortFuseReleaseInfo("2.0", "renodx-dlss-SF-2.0", url, AssetSha256: null, IsArchive: false, ShortFuseDeploymentService.AddonFileName);

        // A plausible foreign add-on (PE, above the size floor) planted where the cache would put it,
        // plus the legacy provenance sidecar name, but no stamp.
        var planted = Path.Combine(cacheRoot, "2.0", ShortFuseDeploymentService.AddonFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(planted)!);
        var foreign = CreatePe(0x8664, 80 * 1024, 0x66);
        File.WriteAllBytes(planted, foreign);
        File.WriteAllText(planted + ".source.json", $$"""{"AssetUrl":"{{url}}","AssetSha256":null,"ExtractedSha256":"{{Sha256(planted)}}"}""");

        var staged = await sf.EnsureCachedAddonAsync(release, CancellationToken.None);

        Assert.Equal(planted, staged);
        Assert.Equal(1, gets);
        Assert.Equal(genuine, File.ReadAllBytes(staged));
        Assert.True(CacheStamp.IsValid(staged, url, "2.0"));
        Assert.False(File.Exists(planted + ".source.json"));

        // The stamped download is reused; a foreign swap after that is caught by the content hash.
        await sf.EnsureCachedAddonAsync(release, CancellationToken.None);
        Assert.Equal(1, gets);
        File.WriteAllBytes(staged, foreign);
        await sf.EnsureCachedAddonAsync(release, CancellationToken.None);
        Assert.Equal(2, gets);
        Assert.Equal(genuine, File.ReadAllBytes(staged));
    }

    private sealed class CountingHandler(string url, byte[] body, Action onGet) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsoluteUri != url) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            onGet();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    [Fact]
    public void SelectArchiveAddonEntry_SingleRenamedAddon64IsAccepted()
    {
        using var stream = new MemoryStream();
        using (var writer = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            writer.CreateEntry("payload/shortfuse-build.addon64");

        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var selected = ShortFuseDeploymentService.SelectArchiveAddonEntry(zip, "shortfuse.zip");

        Assert.Equal("shortfuse-build.addon64", selected.Name);
    }

    [Fact]
    public void SelectArchiveAddonEntry_MultipleFallbackAddon64EntriesFailClosed()
    {
        using var stream = new MemoryStream();
        using (var writer = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            writer.CreateEntry("a.addon64");
            writer.CreateEntry("b.addon64");
        }

        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var ex = Assert.Throws<InvalidDataException>(() =>
            ShortFuseDeploymentService.SelectArchiveAddonEntry(zip, "ambiguous.zip"));

        Assert.Contains("multiple ambiguous", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed record GameFixture(string Root, string Bin, string Prefix, DetectedGame Game) : IDisposable { public void Dispose() { } }
    private sealed record Context(LinuxDeploymentRecordStore Records, UltimateAsiLoaderDeploymentService Ual, ShortFuseDeploymentService ShortFuse, HttpClient Http) : IDisposable { public void Dispose() => Http.Dispose(); }
    private sealed record ResponseTemplate(HttpStatusCode Status, string? Json, byte[]? Bytes)
    {
        public static ResponseTemplate FromJson(string json) => new(HttpStatusCode.OK, json, null);
        public static ResponseTemplate FromBytes(byte[] bytes) => new(HttpStatusCode.OK, null, bytes);
    }
    private sealed class MapHandler : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<string, ResponseTemplate> _responses;
        public MapHandler(IReadOnlyDictionary<string, ResponseTemplate> responses) => _responses = responses;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!_responses.TryGetValue(request.RequestUri!.ToString(), out var template))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var response = new HttpResponseMessage(template.Status);
            response.Content = template.Json is not null
                ? new StringContent(template.Json, Encoding.UTF8, "application/json")
                : new ByteArrayContent(template.Bytes ?? []);
            return Task.FromResult(response);
        }
    }
}
