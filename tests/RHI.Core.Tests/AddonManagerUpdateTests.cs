using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #194 (parity T-19, upstream AddonPackService.CheckAndUpdateAllAsync): the Addon Manager's add-ons are checked and
/// updated by Update All and the background pass, against what each add-on was downloaded as (release tag, server
/// validators, or a Custom add-on's file hash); a check that cannot tell is "could not be checked"; an add-on turned off
/// for No Addons is held. Plus the start-up shader-pack prefetch (upstream CacheAllShaders).
/// </summary>
public sealed class AddonManagerUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-addon-updates-" + Guid.NewGuid().ToString("N"));

    public AddonManagerUpdateTests() => Directory.CreateDirectory(_root);

    private string P(params string[] parts) => Path.Combine([_root, .. parts]);

    private const string ApiUrl = "https://api.github.com/repos/rhi-tests/generic/releases/latest";
    private const string HeadUrl = "https://github.com/rhi-tests/direct/raw/main/direct.addon64";

    /// <summary>The add-on list, the release API, the downloads and HEAD answers, all changeable mid-test.</summary>
    private sealed class Remote : HttpMessageHandler
    {
        public string Tag { get; set; } = "v1";
        public byte Marker { get; set; } = 1;
        public string? ETag { get; set; } = "\"etag-1\"";
        public DateTimeOffset? LastModified { get; set; }
        public bool Offline { get; set; }
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Answer(request));

        private HttpResponseMessage Answer(HttpRequestMessage request)
        {
            var url = request.RequestUri!.AbsoluteUri;
            lock (Requests) Requests.Add(request.Method + " " + url);
            if (Offline) throw new HttpRequestException("offline (test)");
            if (url == ReShadeAddonManagerService.AddonsIniUrl)
                return Ok(new StringContent($"[01]\nPackageName=Generic\nReleaseApiUrl={ApiUrl}\n\n[02]\nPackageName=Direct\nDownloadUrl64={HeadUrl}\n", Encoding.UTF8));
            if (url == "https://manifest.test/manifest.json") return Ok(new StringContent("{\"version\":2}", Encoding.UTF8));
            if (url == ApiUrl)
                return Ok(new StringContent(JsonSerializer.Serialize(new
                {
                    tag_name = Tag,
                    assets = new[] { new { name = "generic.addon64", browser_download_url = $"https://github.com/rhi-tests/generic/releases/download/{Tag}/generic.addon64" } }
                }), Encoding.UTF8));
            if (url.StartsWith("https://github.com/rhi-tests/generic/releases/download/", StringComparison.Ordinal))
                return Ok(new ByteArrayContent(CacheForeignFileRejectionTests.Pe(0x8664, 4096, Marker)));
            if (url == HeadUrl)
            {
                var response = Ok(request.Method == HttpMethod.Head ? new ByteArrayContent([]) : new ByteArrayContent(CacheForeignFileRejectionTests.Pe(0x8664, 4096, (byte)(Marker + 100))));
                if (ETag is not null) response.Headers.ETag = new EntityTagHeaderValue(ETag);
                if (LastModified is not null) response.Content.Headers.LastModified = LastModified;
                return response;
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Ok(HttpContent content) => new(HttpStatusCode.OK) { Content = content };
    }

    private sealed record Setup(ReShadeAddonManagerService Manager, LinuxManagedComponentService Managed, LinuxDeploymentRecordStore Records, DetectedGame Game, Remote Remote, string Custom)
    {
        public string Addon(string name) => Path.Combine(Game.BinaryDirectory!, name + ".addon64");
        public LinuxDeploymentRecord Record => Records.Find(Game, ManagedComponentKind.ReShadeAddonManager)!;
    }

    private Setup Fixture()
    {
        var game = P("game");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "dxgi.dll"), "ReShade by crosire");
        PeArchitectureDetectorTests.WritePe(Path.Combine(game, "Game.exe"), 0x8664, 64 * 1024);
        var custom = P("custom");
        Directory.CreateDirectory(custom);
        File.WriteAllBytes(Path.Combine(custom, "Mine.addon64"), CacheForeignFileRejectionTests.Pe(0x8664, 4096, 50));
        var remote = new Remote();
        var http = new HttpClient(remote);
        var manifest = new UpstreamRhiManifestService(http, "https://manifest.test/manifest.json", null, P("manifest-cache.json"));
        var records = new LinuxDeploymentRecordStore(P("deployments.json"));
        var manager = new ReShadeAddonManagerService(http, manifest, P("addon-cache"), custom, P("state", "addon-manager.json"), records);
        var managed = new LinuxManagedComponentService(records, reshade: null!, addonManager: manager, renodx: null!, renodxUpgrade: null!,
            optiscaler: null!, relimiter: null!, displayCommander: null!, mfgAdaUnlock: null!, dlssg2030: null!,
            dlssEnablerStandalone: null!, rtx40Mfg: null!, dlss5Feeder: null!, dlss5Bridge: null!, d3dCompilerTools: null!,
            ultimateAsiLoader: null!, shortFuse: null!, dofFix: null!, reFramework: null!, lumaFramework: null!, lumaPolicy: null!);
        return new(manager, managed, records, new DetectedGame
        {
            Name = "Addon Game", Source = "Steam", SteamAppId = 4243, InstallPath = game, BinaryDirectory = game,
            ExecutablePath = Path.Combine(game, "Game.exe"), Runner = "Proton"
        }, remote, Path.Combine(custom, "Mine.addon64"));
    }

    private static Task<LinuxUpdateAllResult> UpdateAll(Setup s, LinuxUpdateRunMode mode = LinuxUpdateRunMode.Manual)
        => new LinuxUpdateAllService(s.Records, s.Managed, new NeverRunning()).RunAsync([s.Game], _ => [], mode);

    private sealed class NeverRunning : IGameRunningProbe
    {
        public bool IsRunning(DetectedGame game) => false;
    }

    [Fact]
    public async Task ReleaseTag_SameIsCurrent_NewIsAnUpdate_AndUpdateAllReappliesIt()
    {
        var s = Fixture();
        Assert.True((await s.Manager.ApplySelectionAsync(s.Game, ["01"])).Success);
        var before = File.ReadAllBytes(s.Addon("generic"));
        Assert.Contains("v1", s.Record.Version);

        var current = await s.Managed.CheckManagedUpdateAsync(s.Game, ManagedComponentKind.ReShadeAddonManager, []);
        Assert.True(current.Managed);
        Assert.False(current.UpdateAvailable, current.Message);
        Assert.False(current.CouldNotCheck, current.Message);
        Assert.Equal(ReShadeAddonManagerService.CurrentMessage, current.Message);

        s.Remote.Tag = "v2";
        s.Remote.Marker = 2;
        var check = await s.Managed.CheckManagedUpdateAsync(s.Game, ManagedComponentKind.ReShadeAddonManager, []);
        Assert.True(check.UpdateAvailable, check.Message);
        Assert.Equal(ReShadeAddonManagerService.UpdateAvailablePrefix + "Generic", check.Message);

        var result = await UpdateAll(s, LinuxUpdateRunMode.Background);

        Assert.Equal(1, result.ChangedCount);
        Assert.NotEqual(before, File.ReadAllBytes(s.Addon("generic")));
        Assert.Contains("v2", s.Record.Version);
        Assert.Equal(["01"], ReShadeAddonManagerService.SelectedIdsOf(s.Record));
        var again = await UpdateAll(s);
        Assert.Equal((0, 1), (again.ChangedCount, again.CurrentCount));
    }

    [Fact]
    public async Task ServerValidators_SameETagIsCurrent_ChangedIsAnUpdate_NoneCannotTell()
    {
        var s = Fixture();
        Assert.True((await s.Manager.ApplySelectionAsync(s.Game, ["02"])).Success);

        Assert.False((await s.Manager.CheckUpdateAsync(s.Game)).UpdateAvailable);

        s.Remote.ETag = "\"etag-2\"";
        Assert.True((await s.Manager.CheckUpdateAsync(s.Game)).UpdateAvailable);

        s.Remote.ETag = null; // nothing to compare against
        var unknown = await s.Manager.CheckUpdateAsync(s.Game);
        Assert.False(unknown.UpdateAvailable);
        Assert.True(unknown.CouldNotCheck, unknown.Message);
        Assert.Equal(ReShadeAddonManagerService.CouldNotCheckPrefix + ReShadeAddonManagerService.NoValidatorPrefix + "Direct", unknown.Message);
    }

    // Validators that do not overlap (recorded with an ETag, served now with only a date) cannot tell versions apart.
    [Fact]
    public async Task ServerValidators_ThatDoNotOverlap_CannotTell()
    {
        var s = Fixture();
        Assert.True((await s.Manager.ApplySelectionAsync(s.Game, ["02"])).Success);
        s.Remote.ETag = null;
        s.Remote.LastModified = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

        var check = await s.Manager.CheckUpdateAsync(s.Game);

        Assert.False(check.UpdateAvailable, check.Message);
        Assert.True(check.CouldNotCheck, check.Message);
    }

    // w6/crash review 7: add-ons selected but no add-on file recorded (an older copy imported with an empty file list)
    // gives nothing to compare, even with the list live: could not be checked, never current.
    [Fact]
    public async Task SelectionWithNoRecordedFiles_IsCouldNotBeChecked_NeverCurrent()
    {
        var s = Fixture();
        Assert.True((await s.Manager.ApplySelectionAsync(s.Game, ["01"])).Success);
        var record = s.Record;
        record.Files.Clear();
        s.Records.Upsert(record);
        Assert.Equal(["01"], ReShadeAddonManagerService.SelectedIdsOf(s.Record));

        var check = await s.Manager.CheckUpdateAsync(s.Game);

        Assert.False(check.UpdateAvailable, check.Message);
        Assert.True(check.CouldNotCheck, check.Message);
        Assert.Equal(ReShadeAddonManagerService.CouldNotCheckPrefix + ReShadeAddonManagerService.NoFilesRecordedReason, check.Message);
    }

    [Fact]
    public async Task ListUnreachable_IsCouldNotBeChecked_NeverCurrent()
    {
        var s = Fixture();
        Assert.True((await s.Manager.ApplySelectionAsync(s.Game, ["01"])).Success);
        s.Remote.Offline = true;

        var check = await s.Manager.CheckUpdateAsync(s.Game);

        Assert.False(check.UpdateAvailable);
        Assert.True(check.CouldNotCheck);
        Assert.Equal(ReShadeAddonManagerService.CouldNotCheckPrefix + ReShadeAddonManagerService.ListUnavailableReason, check.Message);
        var result = await UpdateAll(s);
        Assert.Equal((0, 0, 1), (result.ChangedCount, result.CurrentCount, result.UncheckedCount));
    }

    [Fact]
    public async Task CustomAddonReplacedInTheCustomFolder_IsAnUpdate_EvenOffline()
    {
        var s = Fixture();
        Assert.True((await s.Manager.ApplySelectionAsync(s.Game, ["custom-Mine"])).Success);
        Assert.False((await s.Manager.CheckUpdateAsync(s.Game)).UpdateAvailable);

        File.WriteAllBytes(s.Custom, CacheForeignFileRejectionTests.Pe(0x8664, 4096, 51));
        s.Remote.Offline = true; // a local file: no list needed

        var check = await s.Manager.CheckUpdateAsync(s.Game);
        Assert.True(check.UpdateAvailable, check.Message);
        var update = await s.Manager.UpdateAsync(s.Game);
        Assert.True(update.Success, update.Message);
        Assert.Equal(ReShadeAddonManagerService.UpdatedPrefix + "1", update.Message);
        Assert.Equal(File.ReadAllBytes(s.Custom), File.ReadAllBytes(s.Addon("Mine")));
    }

    [Fact]
    public async Task AnAddonWithNoRecordedVersion_IsRefreshedOnce_ToRecordOne()
    {
        var s = Fixture();
        Assert.True((await s.Manager.ApplySelectionAsync(s.Game, ["01"])).Success);
        var record = s.Record;
        record.Metadata.Remove("AddonManager.Versions"); // as applied before #194 or imported from an older build's copy
        s.Records.Upsert(record);

        Assert.True((await s.Manager.CheckUpdateAsync(s.Game)).UpdateAvailable);
        Assert.True((await s.Manager.UpdateAsync(s.Game)).Success);
        Assert.False((await s.Manager.CheckUpdateAsync(s.Game)).UpdateAvailable);
    }

    [Fact]
    public async Task AnAddonTurnedOffForNoAddons_IsHeld_AndItsUpdateRefused()
    {
        var s = Fixture();
        Assert.True((await s.Manager.ApplySelectionAsync(s.Game, ["01"])).Success);
        var record = s.Record;
        File.Move(s.Addon("generic"), s.Addon("generic") + ReShadeAddonSwitch.OffSuffix);
        record.Files[0].Path += ReShadeAddonSwitch.OffSuffix; // what ReShade's switch to No Addons writes
        s.Records.Upsert(record);
        s.Remote.Tag = "v2";

        var check = await s.Managed.CheckManagedUpdateAsync(s.Game, ManagedComponentKind.ReShadeAddonManager, []);

        Assert.False(check.UpdateAvailable);
        Assert.Equal(LinuxManagedComponentService.ReShadeNoAddonsName, check.HeldBy);
        var update = await s.Managed.UpdateManagedAsync(s.Game, ManagedComponentKind.ReShadeAddonManager, []);
        Assert.False(update.Success);
        Assert.StartsWith(ReShadeAddonSwitch.HeldPrefix, update.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(s.Addon("generic") + ReShadeAddonSwitch.OffSuffix));
    }

    [Theory]
    [InlineData("https://github.com/o/r/releases/download/v1.2/x.addon64", "v1.2")]
    [InlineData("https://github.com/o/r/releases/download/latest/x.addon64", null)]
    [InlineData("https://github.com/o/r/releases/download/snapshot/x.addon64", null)]
    [InlineData("https://o.github.io/r/x.addon64", null)]
    public void ReleaseTagInALink_RollingTagsTellNothing(string url, string? tag)
        => Assert.Equal(tag, ReShadeAddonManagerService.ReleaseTagIn(url));

    [Fact]
    public void TheAddonManager_IsAnUpdateAllFamily_AfterReShade()
        => Assert.Contains(ManagedComponentKind.ReShadeAddonManager, LinuxUpdateAllService.SupportedComponents);

    // ---- Shader packs at start-up (upstream CacheAllShaders) ---------------------------------------------------------

    private sealed class PackHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request.RequestUri!.AbsoluteUri);
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, content) in new[] { ("pack-main/Shaders/Test.fx", "// test " + new string('x', 600)), ("pack-main/Textures/Test.png", new string('p', 600)),
                             ("pack-main/Shaders/ReShade.fxh", new string('h', 600)), ("pack-main/Shaders/ReShadeUI.fxh", new string('u', 600)) })
                {
                    using var entry = new StreamWriter(zip.CreateEntry(name).Open());
                    entry.Write(content);
                }
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(stream.ToArray()) });
        }
    }

    [Fact]
    public async Task Prefetch_StagesMissingPacks_LeavesAStagedOneAsItIs_AndTouchesNoGame()
    {
        var handler = new PackHandler();
        using var http = new HttpClient(handler);
        var cache = P("shader-cache");
        var service = new ReShadeShaderPackService(http, cache, customRoot: P("custom-shaders"));
        var game = P("shader-game");
        Directory.CreateDirectory(game);
        await service.SyncAsync(game, ["CrosireMaster"]); // stages CrosireMaster
        var staged = Directory.GetFiles(Path.Combine(cache, "CrosireMaster", "extracted"), "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.GetLastWriteTimeUtc);
        var gameBefore = CancelAtWritePhase.Snapshot(game);
        handler.Requests.Clear();

        var fetched = await service.PrefetchMissingAsync();

        Assert.True(fetched > 0);
        Assert.All(staged, pair => Assert.Equal(pair.Value, File.GetLastWriteTimeUtc(pair.Key))); // the staged pack is left alone
        Assert.True(Directory.GetDirectories(cache).Length > 1, string.Join(", ", Directory.GetDirectories(cache)));
        CancelAtWritePhase.AssertUnchanged(gameBefore, game); // cache only

        // Everything it could stage is staged now: a second start stages nothing new.
        Assert.Equal(0, await service.PrefetchMissingAsync());
    }

    /// <summary>One GitHub-release pack (PumboAutoHDR) whose release moves on with every lookup; everything else 404s.</summary>
    private sealed class MovingReleaseHandler : HttpMessageHandler
    {
        private const string Api = "https://api.github.com/repos/Filoppi/PumboAutoHDR/releases/latest";
        private int _release;
        public int ReleaseLookups => Volatile.Read(ref _release);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url == Api)
            {
                var tag = "v" + Interlocked.Increment(ref _release);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        tag_name = tag,
                        assets = new[] { new { name = "Pumbo.zip", browser_download_url = $"https://github.com/Filoppi/PumboAutoHDR/releases/download/{tag}/Pumbo.zip" } }
                    }), Encoding.UTF8)
                });
            }
            const string download = "https://github.com/Filoppi/PumboAutoHDR/releases/download/";
            if (!url.StartsWith(download, StringComparison.Ordinal)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var release = url[download.Length..].Split('/')[0];
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var entry = new StreamWriter(zip.CreateEntry("Pumbo-main/Shaders/Pumbo.fx", CompressionLevel.NoCompression).Open());
                entry.Write($"// {release} " + new string('x', 600));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(stream.ToArray()) });
        }
    }

    // w6/crash review 4: the "already staged" check is made under the pack cache gate. An install that stages a pack while
    // the prefetch waits for the gate (and a new release appears meanwhile) is seen, so the prefetch never replaces the
    // tree that install is copying from.
    [Fact]
    public async Task Prefetch_ChecksForAStagedPackUnderTheCacheGate_AnInstallStagingItMeanwhileKeepsItsTree()
    {
        var handler = new MovingReleaseHandler();
        using var http = new HttpClient(handler);
        var cache = P("race-cache");
        var service = new ReShadeShaderPackService(http, cache, customRoot: P("race-custom"));
        string? installStaged = null;
        Exception? installFailed = null;
        service.BeforePrefetchGateForTesting = async pack =>
        {
            if (pack.Id != "PumboAutoHDR") return;
            try { installStaged = await service.EnsurePackAsync(pack, CancellationToken.None); } // an install, just before the prefetch's turn
            catch (Exception ex) { installFailed = ex; }
        };

        var fetched = await service.PrefetchMissingAsync();

        Assert.Null(installFailed);
        Assert.NotNull(installStaged);
        Assert.Equal(1, handler.ReleaseLookups); // the install's lookup only: the prefetch left the staged pack alone
        Assert.Equal(0, fetched);
        var shader = Assert.Single(Directory.GetFiles(installStaged!, "Pumbo.fx", SearchOption.AllDirectories));
        Assert.StartsWith("// v1 ", File.ReadAllText(shader), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}

/// <summary>#194: the app stages the shader packs at start-up only with CacheAllShaders on (a VM test, so in the UI-language collection).</summary>
[Collection(UiLanguageCollection.Name)]
public sealed class ShaderPackPrefetchStartUpTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartUp_PrefetchesShaderPacks_OnlyWithTheSettingOn(bool on)
    {
        var requests = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var fx = await ViewModelFixture.StartAsync([], httpFailure: request =>
        {
            requests.Enqueue(request.RequestUri!.AbsoluteUri);
            return new HttpRequestException("offline (test)");
        }, cacheAllShaders: on);

        if (on)
        {
            Assert.NotNull(fx.Vm.ShaderPackPrefetch);
            await fx.Vm.ShaderPackPrefetch!.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Contains(requests, url => url.Contains("reshade-shaders", StringComparison.OrdinalIgnoreCase)); // crosire's pack among them
        }
        else
        {
            Assert.Null(fx.Vm.ShaderPackPrefetch);
        }
    }

    // w6/crash review 8: closing the window (disposing the view model) cancels the start-up staging; a stalled download
    // does not keep running after it.
    [Fact]
    public async Task StartUp_Prefetch_StopsWhenTheViewModelIsDisposed()
    {
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fx = await ViewModelFixture.StartAsync([], cacheAllShaders: true, hangWhen: request =>
        {
            if (!request.RequestUri!.AbsoluteUri.Contains("reshade-shaders", StringComparison.OrdinalIgnoreCase)) return false;
            stalled.TrySetResult();
            return true;
        });
        Assert.NotNull(fx.Vm.ShaderPackPrefetch);
        await stalled.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.False(fx.Vm.ShaderPackPrefetch!.IsCompleted);

        fx.Vm.Dispose();

        await fx.Vm.ShaderPackPrefetch.WaitAsync(TimeSpan.FromSeconds(10));
    }

}
