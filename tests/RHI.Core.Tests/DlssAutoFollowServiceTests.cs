using System.IO.Compression;
using System.Net;
using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

/// <summary>
/// #177 (audit §3 "#95"): following the newest DLSS / Streamline. A two-version manifest (previous newest A, newest B),
/// games on A, on an older version and on DLSS 1.x: only files exactly on A move to B; the first pass only seeds; a
/// fetch that only the cached copy answered never moves the baseline; a running game waits for the next pass; files an
/// RHI component recorded (OptiScaler's) are never touched; Cancel after the first write finishes that game and stops.
/// </summary>
public sealed class DlssAutoFollowServiceTests : IDisposable
{
    private const string A = "310.4.0";
    private const string B = "310.5.2";
    private const string SlA = "2.14.1.0";
    private const string SlB = "2.14.16.0";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-dlss-follow-" + Guid.NewGuid().ToString("N"));

    public DlssAutoFollowServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        RHI.Core.Deployment.OperationCancellation.WritePhaseObserverForTesting = null;
        foreach (var dir in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories).Prepend(_root))
            try { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { }
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public async Task NewerVersion_MovesOnlyFilesOnThePreviousNewest_AndRecordsEachSwap()
    {
        var onPrevious = Game("on-previous", sr: A, rr: A);
        var older = Game("older", sr: "310.3.0");
        var dlss1 = Game("dlss-1x", sr: "1.0.13");
        var fx = Fixture(baseline: A);

        var result = await fx.Service.RunAsync([onPrevious, older, dlss1], new DlssFollowChoice(Dlss: true, Streamline: false));

        Assert.Equal(DlssFollowOutcome.Followed, result.Outcome);
        Assert.Equal(2, result.UpdatedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(B + ".0", DlssAutoFollowService.PeFileVersion(Sr(onPrevious)));
        Assert.Equal(B + ".0", DlssAutoFollowService.PeFileVersion(Rr(onPrevious)));
        Assert.True(File.Exists(Sr(onPrevious) + ".original"));
        Assert.Equal("310.3.0.0", DlssAutoFollowService.PeFileVersion(Sr(older)));
        Assert.Equal("1.0.13.0", DlssAutoFollowService.PeFileVersion(Sr(dlss1)));
        Assert.False(File.Exists(Sr(older) + ".original"));
        Assert.Equal(B, fx.Store.Load()!["dlss"].Newest);

        var history = fx.Log.GetAll();
        Assert.Equal(2, history.Count);
        var sr = Assert.Single(history, entry => entry.ComponentName == "DLSS SR (nvngx_dlss.dll)");
        Assert.Equal((A, B, true, ComponentUpdateAction.Update, ManagedComponentKind.Dlss), (sr.FromVersion, sr.ToVersion, sr.Success, sr.Action, sr.Component));
        Assert.Equal(DlssAutoFollowService.AutomaticDlssNote, sr.Message);
        Assert.Contains(history, entry => entry.ComponentName == "DLSS RR (nvngx_dlssd.dll)");
    }

    [Fact]
    public async Task FirstPass_OnlySeedsTheBaseline_AndDownloadsNoPayload()
    {
        var game = Game("seed", sr: A);
        var fx = Fixture(baseline: null);

        var result = await fx.Service.RunAsync([game], new DlssFollowChoice(Dlss: true, Streamline: false));

        Assert.Equal(DlssFollowOutcome.Seeded, result.Outcome);
        Assert.Equal(A + ".0", DlssAutoFollowService.PeFileVersion(Sr(game)));
        var saved = fx.Store.Load()!;
        Assert.Equal(B, saved["dlss"].Newest);
        Assert.Equal(B, saved["dlssd"].Newest);
        Assert.Equal(B, saved["dlssg"].Newest);
        Assert.False(saved.ContainsKey("streamline")); // its toggle is off
        Assert.Equal(1, fx.Http.RequestCount); // the manifest only
        Assert.Empty(fx.Log.GetAll());
    }

    [Fact]
    public async Task CachedCopyAnswered_IsCouldNotCheck_AndNeverMovesTheBaseline()
    {
        var game = Game("offline", sr: A);
        var fx = Fixture(baseline: A);
        await fx.Dlss.FetchManifestAsync(); // the cache now holds a manifest whose newest is B
        fx.Http.Offline = true;

        var result = await fx.Service.RunAsync([game], new DlssFollowChoice(Dlss: true, Streamline: false));

        Assert.Equal(DlssFollowOutcome.CouldNotCheck, result.Outcome);
        Assert.Equal(A, fx.Store.Load()!["dlss"].Newest);
        Assert.Equal(A + ".0", DlssAutoFollowService.PeFileVersion(Sr(game)));
        Assert.Empty(fx.Log.GetAll());
    }

    [Fact]
    public async Task UnchangedManifest_AnsweredWith304_StillCounts()
    {
        var game = Game("not-modified", sr: A);
        var fx = Fixture(baseline: A);
        await fx.Dlss.FetchManifestAsync(); // cached with its ETag: the follower's fetch is answered with a 304

        var result = await fx.Service.RunAsync([game], new DlssFollowChoice(Dlss: true, Streamline: false));

        Assert.Equal(DlssFollowOutcome.Followed, result.Outcome);
        Assert.Equal(B + ".0", DlssAutoFollowService.PeFileVersion(Sr(game)));
    }

    [Fact]
    public async Task ManifestUnreachableWithoutACache_IsCouldNotCheck()
    {
        var fx = Fixture(baseline: A);
        fx.Http.Offline = true;

        var result = await fx.Service.RunAsync([Game("no-cache", sr: A)], new DlssFollowChoice(Dlss: true, Streamline: false));

        Assert.Equal(DlssFollowOutcome.CouldNotCheck, result.Outcome);
        Assert.Equal(A, fx.Store.Load()!["dlss"].Newest);
    }

    [Fact]
    public async Task BothTogglesOff_ForgetTheBaselines_WithoutFetching()
    {
        var fx = Fixture(baseline: A);

        var result = await fx.Service.RunAsync([Game("off", sr: A)], new DlssFollowChoice(false, false));

        Assert.Equal(DlssFollowOutcome.Off, result.Outcome);
        Assert.Empty(fx.Store.Load()!);
        Assert.Equal(0, fx.Http.RequestCount);
    }

    [Fact]
    public async Task RunningGame_IsLeftForTheNextPass_WhichStillFollowsItFromThePreviousNewest()
    {
        var game = Game("running", sr: A);
        var running = true;
        var fx = Fixture(baseline: A, isRunning: _ => running);

        var first = await fx.Service.RunAsync([game], new DlssFollowChoice(true, false));

        Assert.Equal((1, 0), (first.DeferredGames, first.UpdatedCount));
        Assert.Equal(A + ".0", DlssAutoFollowService.PeFileVersion(Sr(game)));
        Assert.Equal(B, fx.Store.Load()!["dlss"].Newest);
        Assert.Equal([A], fx.Store.Load()!["dlss"].HeldFrom);

        running = false;
        var second = await fx.Service.RunAsync([game], new DlssFollowChoice(true, false));

        Assert.Equal(1, second.UpdatedCount);
        Assert.Equal(B + ".0", DlssAutoFollowService.PeFileVersion(Sr(game)));
        Assert.Empty(fx.Store.Load()!["dlss"].HeldFrom);
    }

    [Fact]
    public async Task XboxTitleItsInstallerAppIsWorkingOn_IsLeftForTheNextPass()
    {
        var game = Game("xbox-busy", sr: A);
        var jobs = Path.Combine(_root, "installer-state", "jobs.json");
        Directory.CreateDirectory(Path.GetDirectoryName(jobs)!);
        File.WriteAllText(jobs, System.Text.Json.JsonSerializer.Serialize(new { queue = new[] { new { kind = "Launch", titleId = "9XBX" } } }));
        game.XboxPackage = new XboxPackageInfo("9XBX", XboxInstallerApp.DisplayName, null, jobs);
        var fx = Fixture(baseline: A);

        var result = await fx.Service.RunAsync([game], new DlssFollowChoice(true, false));

        Assert.Equal((1, 0), (result.DeferredGames, result.UpdatedCount));
        Assert.Equal(A + ".0", DlssAutoFollowService.PeFileVersion(Sr(game)));
        Assert.Equal([A], fx.Store.Load()!["dlss"].HeldFrom);
    }

    [Fact]
    public async Task FilesAnRhiComponentRecorded_AndTheOptiScalerFolder_AreNeverTouched()
    {
        var game = Game("opti", sr: A, fg: A);
        var optiFolder = Directory.CreateDirectory(Path.Combine(game.InstallPath, "OptiScaler")).FullName;
        var optiDll = Path.Combine(optiFolder, "nvngx_dlss.dll");
        File.WriteAllBytes(optiDll, Pe(A));
        var fx = Fixture(baseline: A);
        Record(fx.Records, game, ManagedComponentKind.OptiScaler, Sr(game));
        Record(fx.Records, game, ManagedComponentKind.RenoDX, Fg(game)); // any RHI record, not only the NR owners'

        var result = await fx.Service.RunAsync([game], new DlssFollowChoice(true, false));

        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(A + ".0", DlssAutoFollowService.PeFileVersion(Sr(game)));
        Assert.Equal(A + ".0", DlssAutoFollowService.PeFileVersion(Fg(game)));
        Assert.Equal(A + ".0", DlssAutoFollowService.PeFileVersion(optiDll));
        Assert.Equal(B, fx.Store.Load()!["dlss"].Newest);
    }

    [Fact]
    public async Task FailedSwap_KeepsTheFile_StillAdvancesTheBaseline_AndIsRecordedAsFailed()
    {
        var game = Game("read-only", sr: A);
        var fx = Fixture(baseline: A);
        File.SetUnixFileMode(game.InstallPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var result = await fx.Service.RunAsync([game], new DlssFollowChoice(true, false));

        File.SetUnixFileMode(game.InstallPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.Equal((0, 1), (result.UpdatedCount, result.FailedCount));
        Assert.Equal(A + ".0", DlssAutoFollowService.PeFileVersion(Sr(game)));
        Assert.Equal(B, fx.Store.Load()!["dlss"].Newest);
        var entry = Assert.Single(fx.Log.GetAll());
        Assert.False(entry.Success);
    }

    [Fact]
    public async Task GameWithRecordsLeftAtAnOldFolder_IsRefused_NotSwapped()
    {
        var game = Game("moved", sr: A);
        game.SteamAppId = 4242; // store key: the record still matches the game after the move
        var fx = Fixture(baseline: A);
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.ReShade,
            BinaryDirectory = Path.Combine(_root, "old-library", "moved"), Version = "6.0"
        });

        var result = await fx.Service.RunAsync([game], new DlssFollowChoice(true, false));

        var swap = Assert.Single(result.Swaps);
        Assert.False(swap.Success);
        Assert.StartsWith(DlssAutoFollowService.RefusedPrefix + DeploymentRelocation.GuardPrefix, swap.Error);
        Assert.Equal(A + ".0", DlssAutoFollowService.PeFileVersion(Sr(game)));
    }

    [Fact]
    public async Task OneDotXBaseline_IsNeverFollowedFrom()
    {
        var game = Game("one-x", sr: "1.0.13");
        var fx = Fixture(baseline: "1.0.13");

        var result = await fx.Service.RunAsync([game], new DlssFollowChoice(true, false));

        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal("1.0.13.0", DlssAutoFollowService.PeFileVersion(Sr(game)));
    }

    [Fact]
    public async Task Streamline_FollowsOnlyWithItsOwnToggle()
    {
        var game = Game("streamline");
        var sl = Directory.CreateDirectory(Path.Combine(game.InstallPath, "streamline")).FullName;
        File.WriteAllBytes(Path.Combine(sl, "sl.interposer.dll"), Pe(SlA));
        File.WriteAllBytes(Path.Combine(sl, "sl.common.dll"), Pe(SlA));
        var fx = Fixture(baseline: A, streamlineBaseline: SlA);

        var dlssOnly = await fx.Service.RunAsync([game], new DlssFollowChoice(true, false));
        Assert.Equal(0, dlssOnly.UpdatedCount);
        Assert.Equal(SlA, DlssAutoFollowService.PeFileVersion(Path.Combine(sl, "sl.interposer.dll")));

        fx.Store.Save(new(StringComparer.OrdinalIgnoreCase) { ["streamline"] = new() { Newest = SlA } });
        var withStreamline = await fx.Service.RunAsync([game], new DlssFollowChoice(false, true));

        Assert.Equal(1, withStreamline.UpdatedCount);
        Assert.Equal(SlB, DlssAutoFollowService.PeFileVersion(Path.Combine(sl, "sl.interposer.dll")));
        Assert.Equal(SlB, DlssAutoFollowService.PeFileVersion(Path.Combine(sl, "sl.common.dll")));
        Assert.Equal("Streamline (sl.*.dll)", Assert.Single(fx.Log.GetAll()).ComponentName);
    }

    // w2/vmsplit rule: one game is one unit. Cancel pressed once the first game's swap began lets that game finish (both
    // of its files), stops before the next, downloads nothing after the first write, and leaves the baseline for the
    // next pass (the swapped game is no longer on the previous newest, so it is not swapped twice).
    [Fact]
    public async Task CancelAfterTheFirstWrite_FinishesThatGame_ThenStops()
    {
        var first = Game("cancel-first", sr: A, rr: A);
        var second = Game("cancel-second", sr: A);
        var fx = Fixture(baseline: A);
        using var outer = new OperationCancellation();
        var accepted = new List<bool>();
        OperationCancellation.WritePhaseObserverForTesting = _ =>
        {
            fx.Gate.CloseGate();
            accepted.Add(outer.TryCancel());
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fx.Service.RunAsync([first, second], new DlssFollowChoice(true, false), cancellationToken: outer.Token));

        Assert.Equal([true], accepted);
        Assert.Empty(fx.Gate.LateRequests);
        Assert.Equal(B + ".0", DlssAutoFollowService.PeFileVersion(Sr(first)));
        Assert.Equal(B + ".0", DlssAutoFollowService.PeFileVersion(Rr(first)));
        Assert.Equal(A + ".0", DlssAutoFollowService.PeFileVersion(Sr(second)));
        Assert.False(File.Exists(Sr(second) + ".original"));
        Assert.Equal(A, fx.Store.Load()!["dlss"].Newest);
    }

    [Theory]
    [InlineData("310.9.16", "310,9,16,0", true)]
    [InlineData("310.9.16", "310.9.16.0", true)]
    [InlineData("2.14.16.0", "2.14.16", true)]
    [InlineData("310.9.1", "310.9.16", false)]
    [InlineData("310.8.3 (50xx)", "310.8.3.0", true)]
    [InlineData("", "310.9.16", false)]
    public void SameVersion_ComparesNumbersPartByPart(string a, string b, bool same)
        => Assert.Equal(same, DlssAutoFollowService.SameVersion(a, b));

    // ---- fixture ----

    private sealed record Setup(DlssAutoFollowService Service, DlssManagerService Dlss, DlssAutoFollowStore Store,
        ComponentUpdateLogStore Log, LinuxDeploymentRecordStore Records, ManifestHandler Http, GateWatchingHandler Gate);

    private Setup Fixture(string? baseline, string? streamlineBaseline = null, Func<DetectedGame, bool>? isRunning = null)
    {
        var http = new ManifestHandler();
        var gate = new GateWatchingHandler(http);
        var dlss = new DlssManagerService(new HttpClient(gate), new TransactionalFileDeploymentService(), Path.Combine(_root, "cache-" + Guid.NewGuid().ToString("N")));
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "records-" + Guid.NewGuid().ToString("N") + ".json"));
        var store = new DlssAutoFollowStore(Path.Combine(_root, "follow-" + Guid.NewGuid().ToString("N") + ".json"));
        var log = new ComponentUpdateLogStore(Path.Combine(_root, "log-" + Guid.NewGuid().ToString("N") + ".json"));
        var tracks = new Dictionary<string, DlssFollowTrack>(StringComparer.OrdinalIgnoreCase);
        if (baseline is not null)
            foreach (var family in new[] { "dlss", "dlssd", "dlssg" }) tracks[family] = new DlssFollowTrack { Newest = baseline };
        if (streamlineBaseline is not null) tracks["streamline"] = new DlssFollowTrack { Newest = streamlineBaseline };
        if (tracks.Count > 0) store.Save(tracks);
        var service = new DlssAutoFollowService(dlss, new LinuxDlssDefaultsService(dlss, records, new Running(_ => false)), records, store, log,
            new Running(isRunning ?? (_ => false)));
        return new(service, dlss, store, log, records, http, gate);
    }

    private DetectedGame Game(string name, string? sr = null, string? rr = null, string? fg = null)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "games", name)).FullName;
        var exe = Path.Combine(dir, name + ".exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        if (sr is not null) File.WriteAllBytes(Path.Combine(dir, "nvngx_dlss.dll"), Pe(sr));
        if (rr is not null) File.WriteAllBytes(Path.Combine(dir, "nvngx_dlssd.dll"), Pe(rr));
        if (fg is not null) File.WriteAllBytes(Path.Combine(dir, "nvngx_dlssg.dll"), Pe(fg));
        return new DetectedGame { Name = name, InstallPath = dir, BinaryDirectory = dir, ExecutablePath = exe, Source = "Test", Runner = "Wine" };
    }

    private static string Sr(DetectedGame game) => Path.Combine(game.InstallPath, "nvngx_dlss.dll");
    private static string Rr(DetectedGame game) => Path.Combine(game.InstallPath, "nvngx_dlssd.dll");
    private static string Fg(DetectedGame game) => Path.Combine(game.InstallPath, "nvngx_dlssg.dll");

    private static void Record(LinuxDeploymentRecordStore records, DetectedGame game, ManagedComponentKind kind, string file)
        => records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = kind,
            BinaryDirectory = game.BinaryDirectory!, Version = "test",
            Files = [new LinuxDeployedFile { Path = file, Sha256 = "00" }]
        });

    /// <summary>A DLL with a version resource, larger than the DLSS cache's readable-payload floor (64 KiB).</summary>
    internal static byte[] Pe(string version) => PeVersionReaderTests.BuildVersionedPe(version, version, size: 80_000);

    private sealed class Running(Func<DetectedGame, bool> isRunning) : IGameRunningProbe
    {
        public bool IsRunning(DetectedGame game) => isRunning(game);
    }

    /// <summary>The DLSS manifest (previous newest A, newest B; Streamline likewise) and one zip per family.</summary>
    internal sealed class ManifestHandler : HttpMessageHandler
    {
        private const string Base = "https://github.com/rhi-tests/dlss/releases/download/";
        private const string ManifestTag = "\"follow-test\"";
        public bool Offline { get; set; }
        public int RequestCount { get; private set; }

        public static string Manifest => $$"""
            {
              "dlss": [{ "version": "{{B}}", "url": "{{Base}}b/dlss.zip" }, { "version": "{{A}}", "url": "{{Base}}a/dlss.zip" }],
              "dlssd": [{ "version": "{{B}}", "url": "{{Base}}b/dlssd.zip" }, { "version": "{{A}}", "url": "{{Base}}a/dlssd.zip" }],
              "dlssg": [{ "version": "{{B}}", "url": "{{Base}}b/dlssg.zip" }, { "version": "{{A}}", "url": "{{Base}}a/dlssg.zip" }],
              "dlssnr": [{ "version": "{{B}}", "url": "{{Base}}b/dlssnr.zip" }],
              "streamline": [{ "version": "{{SlB}}", "url": "{{Base}}b/streamline.zip" }, { "version": "{{SlA}}", "url": "{{Base}}a/streamline.zip" }]
            }
            """;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (Offline) throw new HttpRequestException("offline test");
            return Task.FromResult(Answer(request)!);
        }

        /// <summary>The manifest and the family zips; null for any other request.</summary>
        public static HttpResponseMessage? Answer(HttpRequestMessage request)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url != DlssManagerService.ManifestUrl && !url.StartsWith(Base, StringComparison.Ordinal)) return null;
            if (url == DlssManagerService.ManifestUrl)
            {
                // ETag revalidation as GitHub's raw host answers it: an unchanged manifest costs a 304.
                var answer = request.Headers.IfNoneMatch.Any(tag => tag.Tag == ManifestTag)
                    ? new HttpResponseMessage(HttpStatusCode.NotModified)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(Manifest)) };
                answer.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(ManifestTag);
                return answer;
            }
            byte[] body = url == DlssManagerService.ManifestUrl
                ? Encoding.UTF8.GetBytes(Manifest)
                : Zip(url.EndsWith("streamline.zip", StringComparison.Ordinal)
                    ? [("sl.interposer.dll", SlB), ("sl.common.dll", SlB)]
                    : [(Path.GetFileNameWithoutExtension(url) switch
                    {
                        "dlssd" => "nvngx_dlssd.dll",
                        "dlssg" => "nvngx_dlssg.dll",
                        "dlssnr" => "nvngx_dlssnr.dll",
                        _ => "nvngx_dlss.dll"
                    }, url.Contains("/b/", StringComparison.Ordinal) ? B : A)]);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        }

        private static byte[] Zip(IEnumerable<(string Name, string Version)> files)
        {
            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var (name, version) in files)
                {
                    var entry = zip.CreateEntry(name);
                    entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    using var stream = entry.Open();
                    stream.Write(Pe(version));
                }
            return output.ToArray();
        }
    }
}
