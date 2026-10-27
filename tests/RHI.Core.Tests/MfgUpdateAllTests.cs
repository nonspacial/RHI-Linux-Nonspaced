using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// Lane w12/mfg through the app's real services (<see cref="ViewModelFixture"/>, offline but for the fake releases each
/// test serves): Universal MFG (#236), MFG Ada Unlock (#237), DLSSG 20/30 Unlock and Ultimate ASI Loader (#238) as Update
/// All families — update, history, a game that was running caught up at the next pass, removed files never put back.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class MfgUpdateAllTests
{
    // ---- Universal MFG (#236) ------------------------------------------------------------------------------------

    /// <summary>A Universal MFG release the way GitHub serves it (JSON, zip, SHA256SUMS.txt), switchable between versions.</summary>
    internal sealed class UniversalMfgRelease
    {
        private readonly Dictionary<string, (string Prefix, byte[] Zip)> _versions = new(StringComparer.Ordinal);
        public string Active { get; set; } = "";

        public UniversalMfgRelease Add(string version, string prefix, string dllName, byte marker)
        {
            _versions[version] = (prefix, ZipOf((dllName, Pe(marker))));
            Active = version;
            return this;
        }

        public static byte[] Pe(byte marker) => CacheForeignFileRejectionTests.Pe(0x8664, 512 * 1024, marker);
        private string AssetName => $"{_versions[Active].Prefix}v{Active}.zip";
        private string AssetUrl => $"https://github.com/rhi-tests/mfg-unlocker/{AssetName}";
        private string SumsUrl => $"https://github.com/rhi-tests/mfg-unlocker/v{Active}/SHA256SUMS.txt";
        private string Digest => Convert.ToHexString(SHA256.HashData(_versions[Active].Zip)).ToLowerInvariant();

        public HttpResponseMessage? Answer(HttpRequestMessage request)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url == Rtx40MfgStandaloneDeploymentService.ReleasesApiUrl)
                return Text($$"""
                    {"tag_name":"v{{Active}}","draft":false,"prerelease":false,"assets":[
                      {"name":"{{AssetName}}","browser_download_url":"{{AssetUrl}}","digest":"sha256:{{Digest}}"},
                      {"name":"SHA256SUMS.txt","browser_download_url":"{{SumsUrl}}"}]}
                    """);
            if (url == AssetUrl) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_versions[Active].Zip) };
            if (url == SumsUrl) return Text($"{new string('c', 64)}  MFGUnlocker.dll\n{Digest}  {AssetName}\n");
            return null;
        }
    }

    internal static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    internal static byte[] ZipOf(params (string Name, byte[] Bytes)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, bytes) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var output = entry.Open();
                output.Write(bytes);
            }
        return memory.ToArray();
    }

    /// <summary>A Windows x64 DX12 game in a Wine prefix with the game's own Streamline DLSS Frame Generation.</summary>
    internal static DetectedGame StreamlineGame(string root, string name)
    {
        var dir = Directory.CreateDirectory(Path.Combine(root, "games", name)).FullName;
        var prefix = Directory.CreateDirectory(Path.Combine(root, "prefixes", name)).FullName;
        var exe = Path.Combine(dir, "Game.exe");
        File.WriteAllBytes(exe, CacheForeignFileRejectionTests.Pe(0x8664, 64 * 1024, 0x11));
        File.WriteAllText(Path.Combine(dir, "sl.interposer.dll"), "streamline");
        File.WriteAllBytes(Path.Combine(dir, "nvngx_dlssg.dll"), [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\Other] 1\n\"x\"=\"y\"\n");
        return new DetectedGame
        {
            Name = name, Source = "Manual", Runner = "Wine", InstallPath = dir, ExecutablePath = exe, BinaryDirectory = dir,
            PrefixPath = prefix, GraphicsApi = GraphicsApi.DirectX12
        };
    }

    internal static IReadOnlyList<ComponentUpdateLogEntry> History(ViewModelFixture fx)
        => new ComponentUpdateLogStore(Path.Combine(Path.GetDirectoryName(fx.Settings.SettingsPath)!, "prefs", "update-log.json")).GetAll();

    internal static ManagedComponent Row(ViewModelFixture fx, DetectedGame game, ManagedComponentKind kind)
    {
        var files = Directory.EnumerateFiles(game.InstallPath!, "*", SearchOption.AllDirectories).ToArray();
        var components = ComponentProbe.Probe(game, files);
        fx.Managed.Decorate(game, components);
        return components.Single(component => component.Kind == kind);
    }

    internal sealed class SwitchableRunning : IGameRunningProbe
    {
        public bool Running { get; set; }
        public bool IsRunning(DetectedGame game) => Running;
    }

    // #236: a v1.4.2 install (RTXMFG.dll) updated by Update All to v1.7.0 (MFGUnlocker.dll) keeps its proxy name and Wine
    // override, and the history reads 1.4.2 → 1.7.0.
    [Fact]
    public async Task UniversalMfg_UpdateAll_V142ToV170_KeepsTheProxyName_AndHistoryReadsTheVersions()
    {
        var release = new UniversalMfgRelease().Add("1.4.2", "RTXMFG-", "RTXMFG.dll", 0x51);
        release.Add("1.7.0", "MFGUnlocker-", "MFGUnlocker.dll", 0x52);
        release.Active = "1.4.2";
        using var fx = await ViewModelFixture.StartAsync([], httpResponse: release.Answer);
        var game = StreamlineGame(fx.Root, "Universal MFG Game");
        Assert.True(fx.Managed.SetRtx40MfgPreferences(game, "dinput8.dll").Success);
        var install = await fx.Managed.InstallAsync(game, ManagedComponentKind.Rtx40MfgStandalone, []);
        Assert.True(install.Success, install.Message);
        release.Active = "1.7.0";

        var result = await new LinuxUpdateAllService(fx.Records, fx.Managed, new SwitchableRunning()).RunAsync([game], _ => [], LinuxUpdateRunMode.Manual);

        Assert.Equal((1, 0, 0), (result.ChangedCount, result.FailedCount, result.UncheckedCount));
        Assert.Equal(UniversalMfgRelease.Pe(0x52), File.ReadAllBytes(Path.Combine(game.BinaryDirectory!, "dinput8.dll")));
        Assert.Equal("1.7.0", fx.Records.Find(game, ManagedComponentKind.Rtx40MfgStandalone)!.Version);
        var update = Assert.Single(History(fx), entry => entry.Action == ComponentUpdateAction.Update);
        Assert.Equal((ManagedComponentKind.Rtx40MfgStandalone, "1.4.2", "1.7.0", true),
            (update.Component, update.FromVersion, update.ToVersion, update.Success));
    }

    // #236 (upstream catches games up "next time"): a game running during a pass is deferred, then updated at the next pass.
    [Fact]
    public async Task UniversalMfg_GameRunningDuringAPass_IsDeferred_ThenUpdatedAtTheNextPass()
    {
        var release = new UniversalMfgRelease().Add("1.4.1", "RTXMFG-", "RTXMFG.dll", 0x53);
        release.Add("1.6.0", "MFGUnlocker-", "MFGUnlocker.dll", 0x54);
        release.Active = "1.4.1";
        using var fx = await ViewModelFixture.StartAsync([], httpResponse: release.Answer);
        var game = StreamlineGame(fx.Root, "Running MFG Game");
        Assert.True((await fx.Managed.InstallAsync(game, ManagedComponentKind.Rtx40MfgStandalone, [])).Success);
        var proxy = fx.Records.Find(game, ManagedComponentKind.Rtx40MfgStandalone)!.Files.Single().Path;
        release.Active = "1.6.0";
        var running = new SwitchableRunning { Running = true };
        var updateAll = new LinuxUpdateAllService(fx.Records, fx.Managed, running);

        var first = await updateAll.RunAsync([game], _ => [], LinuxUpdateRunMode.Background);
        Assert.Equal((0, 1), (first.ChangedCount, first.DeferredCount));
        Assert.Equal(UniversalMfgRelease.Pe(0x53), File.ReadAllBytes(proxy));

        running.Running = false;
        var second = await updateAll.RunAsync([game], _ => [], LinuxUpdateRunMode.Background);

        Assert.Equal((1, 0, 0), (second.ChangedCount, second.DeferredCount, second.FailedCount));
        Assert.Equal(UniversalMfgRelease.Pe(0x54), File.ReadAllBytes(proxy));
    }

    // #236: the proxy removed outside RHI is never put back; Update All says why (could not be checked, never failed),
    // the row offers Remove (RHI's record alone makes it Installed) with a note, and Remove clears the record and the
    // Wine override.
    [Fact]
    public async Task UniversalMfg_ProxyRemovedOutsideRhi_IsNotPutBack_AndTheRowOffersRemove()
    {
        var release = new UniversalMfgRelease().Add("1.4.0", "RTXMFG-", "RTXMFG.dll", 0x55);
        release.Add("1.5.0", "MFGUnlocker-", "MFGUnlocker.dll", 0x56);
        release.Active = "1.4.0";
        using var fx = await ViewModelFixture.StartAsync([], httpResponse: release.Answer);
        var game = StreamlineGame(fx.Root, "Removed MFG Game");
        Assert.True((await fx.Managed.InstallAsync(game, ManagedComponentKind.Rtx40MfgStandalone, [])).Success);
        var proxy = fx.Records.Find(game, ManagedComponentKind.Rtx40MfgStandalone)!.Files.Single().Path;
        File.Delete(proxy);
        release.Active = "1.5.0";

        foreach (var mode in new[] { LinuxUpdateRunMode.Manual, LinuxUpdateRunMode.Background })
        {
            var result = await new LinuxUpdateAllService(fx.Records, fx.Managed, new SwitchableRunning()).RunAsync([game], _ => [], mode);
            Assert.Equal((0, 0, 0, 1), (result.ChangedCount, result.CurrentCount, result.FailedCount, result.UncheckedCount));
            Assert.StartsWith(ManagedUpdateFileGuard.RemovedOutsideRhiPrefix, Assert.Single(result.Items).Message);
        }
        Assert.False(File.Exists(proxy));

        var row = Row(fx, game, ManagedComponentKind.Rtx40MfgStandalone);
        Assert.True(new ComponentRowViewModel(row, null!).OffersRemove);
        Assert.Equal(ManagedUpdateFileGuard.RemovedOutsideRhiRowNote, row.Note);

        var remove = await fx.Managed.RemoveAsync(game, ManagedComponentKind.Rtx40MfgStandalone);
        Assert.True(remove.Success, remove.Message);
        Assert.Null(fx.Records.Find(game, ManagedComponentKind.Rtx40MfgStandalone));
        Assert.Null(WineDllOverrideService.TryReadOverride(File.ReadAllText(Path.Combine(game.PrefixPath!, "user.reg")),
            Path.GetFileNameWithoutExtension(proxy), "Game.exe"));
    }
    // ---- MFG Ada Unlock (#237) and DLSSG 20/30 (#238) rows ------------------------------------------------------------

    // A file the user removed is never put back by an update: the row stays Installed through RHI's record, offers Remove
    // (Install refused or re-created it behind the record before) and says why; Remove clears the record.
    [Theory]
    [InlineData(ManagedComponentKind.MfgAdaUnlock)]
    [InlineData(ManagedComponentKind.Dlssg2030Unlock)]
    public async Task RowWithItsFileRemovedOutsideRhi_OffersRemove_WithANote_AndRemoveClearsTheRecord(ManagedComponentKind kind)
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = StreamlineGame(fx.Root, "Row " + kind);
        var missing = Path.Combine(game.BinaryDirectory!, kind == ManagedComponentKind.MfgAdaUnlock ? "renodx-mfgunlock.addon64" : "winmm.dll");
        var record = new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = kind, BinaryDirectory = game.BinaryDirectory!,
            Version = "0.5", Files = [new LinuxDeployedFile { Path = missing, Sha256 = new string('a', 64) }]
        };
        if (kind == ManagedComponentKind.Dlssg2030Unlock) record.Metadata["FG2030.ProxyName"] = "winmm.dll";
        fx.Records.Upsert(record);

        var row = Row(fx, game, kind);

        Assert.Equal(ComponentAvailability.Installed, row.Availability);
        Assert.True(new ComponentRowViewModel(row, null!).OffersRemove);
        Assert.Equal(ManagedUpdateFileGuard.RemovedOutsideRhiRowNote, row.Note);
        var remove = await fx.Managed.RemoveAsync(game, kind);
        Assert.True(remove.Success, remove.Message);
        Assert.Null(fx.Records.Find(game, kind));
        Assert.False(File.Exists(missing));
    }

    // ---- Ultimate ASI Loader (#238) through the app's services ---------------------------------------------------------

    private sealed class UalRelease
    {
        public string Active { get; set; } = "";
        private readonly Dictionary<string, byte[]> _zips = new(StringComparer.Ordinal);

        public UalRelease Add(string version, byte marker)
        {
            _zips[version] = ZipOf(("dinput8.dll", CacheForeignFileRejectionTests.Pe(0x8664, 64 * 1024, marker)));
            Active = version;
            return this;
        }

        private string Url => $"https://github.com/rhi-tests/ual-w12/{Active}/Ultimate-ASI-Loader_x64.zip";

        public HttpResponseMessage? Answer(HttpRequestMessage request)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url == UltimateAsiLoaderDeploymentService.GitHubApiUrl)
                return Text($$"""
                    {"tag_name":"v{{Active}}","assets":[{"name":"Ultimate-ASI-Loader_x64.zip","browser_download_url":"{{Url}}",
                      "digest":"sha256:{{Convert.ToHexString(SHA256.HashData(_zips[Active])).ToLowerInvariant()}}"}]}
                    """);
            return url == Url ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_zips[Active]) } : null;
        }
    }

    // The owner's one ASI Loader install: Update All now updates it (it used to be left out), keeps the proxy name, and the
    // history reads the versions; with automatic updates on, the background pass does the same.
    [Fact]
    public async Task AsiLoader_UpdateAll_UpdatesItUnderItsProxyName_AndHistoryReadsTheVersions()
    {
        var release = new UalRelease().Add("9.8.0-w12mfg", 0x61).Add("9.8.1-w12mfg", 0x62);
        release.Active = "9.8.0-w12mfg";
        using var fx = await ViewModelFixture.StartAsync([], httpResponse: release.Answer);
        var game = StreamlineGame(fx.Root, "ASI Loader Game");
        var install = await fx.Managed.InstallUltimateAsiLoaderAsync(game, "winmm.dll");
        Assert.True(install.Success, install.Message);
        release.Active = "9.8.1-w12mfg";

        var result = await new LinuxUpdateAllService(fx.Records, fx.Managed, new SwitchableRunning()).RunAsync([game], _ => [], LinuxUpdateRunMode.Background);

        Assert.Equal((1, 0, 0), (result.ChangedCount, result.FailedCount, result.UncheckedCount));
        Assert.Equal(CacheForeignFileRejectionTests.Pe(0x8664, 64 * 1024, 0x62), File.ReadAllBytes(Path.Combine(game.BinaryDirectory!, "winmm.dll")));
        var update = Assert.Single(History(fx), entry => entry.Action == ComponentUpdateAction.Update);
        Assert.Equal((ManagedComponentKind.UltimateAsiLoader, "9.8.0-w12mfg", "9.8.1-w12mfg"), (update.Component, update.FromVersion, update.ToVersion));
    }
}
