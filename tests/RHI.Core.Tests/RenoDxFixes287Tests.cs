using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// #287 (upstream 2.8.9–2.9.3): GitHub fallback for modder-hosted add-ons, second-download names, the Unreal mod for
// games RHI's data knows are UE4/UE5.
public sealed class RenoDxFixes287Tests : IDisposable
{
    private const string UeExtendedPages = "https://marat569.github.io/renodx/renodx-ue-extended.addon64";
    private const string UeExtendedRelease = "https://github.com/marat569/renodx/releases/download/snapshot/renodx-ue-extended.addon64";
    private const string ManifestUrl = "https://example.test/manifest.json";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-renodx-287-" + Guid.NewGuid().ToString("N"));

    public RenoDxFixes287Tests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    // Upstream AddonFileNamesTests (2.9.3), the same names.
    [Theory]
    [InlineData("renodx-ue-extended (1).addon64", "renodx-ue-extended.addon64")]
    [InlineData("renodx-ue-extended(2).addon64", "renodx-ue-extended.addon64")]
    [InlineData("renodx-cyberpunk2077 (12).addon64", "renodx-cyberpunk2077.addon64")]
    [InlineData("renodx-game - Copy.addon64", "renodx-game.addon64")]
    [InlineData("renodx-game - Copy (2).addon32", "renodx-game.addon32")]
    [InlineData("RENODX-GAME (1).ADDON64", "RENODX-GAME.ADDON64")]
    [InlineData("renodx-ue-extended.addon64", "renodx-ue-extended.addon64")]
    [InlineData("renodx-game (beta).addon64", "renodx-game (beta).addon64")]
    [InlineData("renodx-game-1.addon64", "renodx-game-1.addon64")]
    [InlineData("renodx-game (1).dll", "renodx-game (1).dll")]
    [InlineData("", "")]
    public void SecondDownloadNames_GiveTheAddonsOwnName(string name, string expected)
        => Assert.Equal(expected, RenoDxAddonFileNames.Canonical(name));

    [Fact]
    public void SecondDownload_IsTheSameAddon_AnotherModIsNot()
    {
        Assert.True(RenoDxAddonFileNames.IsDuplicateOf("Renodx-UE-Extended (1).addon64", "renodx-ue-extended.addon64"));
        Assert.False(RenoDxAddonFileNames.IsDuplicateOf("renodx-game2 (1).addon64", "renodx-game.addon64"));
        Assert.False(RenoDxAddonFileNames.IsDuplicateOf("renodx-game.addon64", "renodx-game.addon64"));
    }

    [Fact]
    public void ModderHostedAddon_FallsBackToTheModdersGitHubRelease_ThenShortFuses()
        => Assert.Equal(
            [UeExtendedPages, UeExtendedRelease, "https://github.com/clshortfuse/renodx/releases/download/snapshot/renodx-ue-extended.addon64"],
            RenoDxDownloadFallback.Candidates(UeExtendedPages));

    [Fact]
    public void GitHubReleaseLink_HasNoFallback()
        => Assert.Equal([UeExtendedRelease], RenoDxDownloadFallback.Candidates(UeExtendedRelease));

    [Fact]
    public async Task UeExtended_WhenTheModdersSiteIsUnreachable_InstallsTheSameFileFromTheirGitHub()
    {
        var game = Game("Hogwarts Legacy");
        var handler = new Handler { Manifest = $$"""{ "version": 2, "snapshotOverrides": { "Hogwarts Legacy": "{{UeExtendedPages}}" } }""" };
        handler.Unreachable.Add(UeExtendedPages);
        var (service, store) = Service(handler);

        var result = await service.InstallAsync(game, [game.ExecutablePath!]);

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, handler.Gets(UeExtendedRelease));
        Assert.Equal(UeExtendedPages, store.Find(game, ManagedComponentKind.RenoDX)!.SourceUrl);
    }

    [Fact]
    public async Task Install_SetsASecondDownloadAside_KeepsIt_AndSaysWhere()
    {
        var game = Game("Hogwarts Legacy");
        var duplicate = Path.Combine(game.BinaryDirectory!, "renodx-ue-extended (1).addon64");
        PeArchitectureDetectorTests.WritePe(duplicate, 0x8664, 4096);
        var bytes = File.ReadAllBytes(duplicate);
        var other = Path.Combine(game.BinaryDirectory!, "renodx-other (1).addon64");
        var handler = new Handler { Manifest = $$"""{ "version": 2, "snapshotOverrides": { "Hogwarts Legacy": "{{UeExtendedPages}}" } }""" };
        var (service, store) = Service(handler);

        var installed = await service.InstallAsync(game, [game.ExecutablePath!]);
        Assert.True(installed.Success, installed.Message); // before #287: "Foreign RenoDX protection"

        Assert.False(File.Exists(duplicate));
        var aside = duplicate + RenoDxAddonFileNames.SetAsideSuffix;
        Assert.Equal(bytes, File.ReadAllBytes(aside));
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "renodx-ue-extended.addon64")));
        Assert.Contains(RenoDxDeploymentService.DuplicatesSetAsidePrefix + aside, installed.Message);
        Assert.Equal(aside, store.Find(game, ManagedComponentKind.RenoDX)!.Metadata[RenoDxDeploymentService.DuplicatesSetAsideMetadataKey]);
        Assert.StartsWith("Um segundo download deste add-on foi renomeado", UiLocalizationService.PresentFor("pt-BR", RenoDxDeploymentService.DuplicatesSetAsidePrefix));

        // Another mod's second download is still someone else's add-on.
        PeArchitectureDetectorTests.WritePe(other, 0x8664, 4096);
        var update = await service.UpdateAsync(game, [game.ExecutablePath!]);
        Assert.False(update.Success);
        Assert.Contains("Foreign RenoDX protection", update.Message);
    }

    [Fact]
    public async Task DroppedSecondDownload_InstallsUnderTheAddonsOwnName()
    {
        var game = Game("Foo Game");
        var downloads = Directory.CreateDirectory(Path.Combine(_root, "Downloads")).FullName;
        var dropped = RenoDxLocalAddonTests.Addon(downloads, "renodx-foogame (1).addon64");
        var (service, store) = Service(new Handler());

        var result = await service.InstallLocalAddonAsync(game, dropped);

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "renodx-foogame.addon64")));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "renodx-foogame (1).addon64")));
        Assert.Equal("renodx-foogame.addon64", store.Find(game, ManagedComponentKind.RenoDX)!.Version);
        Assert.True(File.Exists(dropped)); // the source stays
    }

    [Theory]
    [InlineData("Unreal Engine 5.4", true)]
    [InlineData("Unreal Engine 4", true)]
    [InlineData("Unreal Engine 3", false)]
    [InlineData("Unreal Engine 45", false)]
    [InlineData(null, false)]
    public async Task NoUnrealFiles_ButAUe4OrUe5EngineHint_GetsTheGenericUnrealMod(string? hint, bool unreal)
    {
        using var http = new HttpClient(new Handler());
        var catalog = new RenoDxCatalogService(http);
        var game = new DetectedGame { Name = "Phantom Blade Zero", InstallPath = "/tmp/pbz", EngineHint = hint };

        var entry = await catalog.ResolveAsync(game, ["/tmp/pbz/PBZ.exe"]);

        Assert.Equal(unreal ? RenoDxCatalogService.GenericUnrealUrl : null, entry?.SnapshotUrl64);
    }

    [Fact]
    public async Task NoUnrealFiles_ButTheManifestHintOrTheUeExtendedDatabase_GetsTheGenericUnrealMod()
    {
        var handler = new Handler
        {
            Manifest = """{ "version": 2, "engineHintOverrides": { "Phantom Blade Zero": "Unreal Engine 5.5" } }""",
            UnrealDb = """[ { "Name": "Kaku Ancient Seal", "Status": "Working" } ]""",
        };
        using var http = new HttpClient(handler);
        var catalog = new RenoDxCatalogService(http, new UpstreamRhiManifestService(http, ManifestUrl))
        {
            UnrealDatabase = new RenoDxUnrealDatabaseService(http, Path.Combine(_root, "unreal.json")),
        };

        var fromManifest = await catalog.ResolveAsync(new DetectedGame { Name = "Phantom Blade Zero", InstallPath = "/tmp/a" }, ["/tmp/a/PBZ.exe"]);
        var fromDatabase = await catalog.ResolveAsync(new DetectedGame { Name = "Kaku Ancient Seal", InstallPath = "/tmp/b" }, ["/tmp/b/Kaku.exe"]);
        var neither = await catalog.ResolveAsync(new DetectedGame { Name = "Some Other Game", InstallPath = "/tmp/c" }, ["/tmp/c/Game.exe"]);

        Assert.Equal(RenoDxCatalogService.GenericUnrealUrl, fromManifest?.SnapshotUrl64);
        Assert.Equal(RenoDxCatalogService.GenericUnrealUrl, fromDatabase?.SnapshotUrl64);
        Assert.Null(neither);
    }

    private (RenoDxDeploymentService Service, LinuxDeploymentRecordStore Store) Service(Handler handler)
    {
        var http = new HttpClient(handler);
        var store = new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json"));
        var manifest = new UpstreamRhiManifestService(http, ManifestUrl);
        return (new RenoDxDeploymentService(http, store, new RenoDxCatalogService(http, manifest), Path.Combine(_root, "cache"), manifest), store);
    }

    private DetectedGame Game(string name) => RenoDxLocalAddonTests.Game(_root, name, 0x8664);

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Dictionary<string, int> _gets = new(StringComparer.Ordinal);
        public string? Manifest { get; set; }
        public string? UnrealDb { get; set; }
        public HashSet<string> Unreachable { get; } = new(StringComparer.Ordinal);

        public int Gets(string url) { lock (_gets) return _gets.GetValueOrDefault(url); }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url == ManifestUrl && Manifest is not null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Manifest, Encoding.UTF8, "application/json") });
            if (url == RenoDxUnrealDatabaseService.LiveUrl && UnrealDb is not null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(UnrealDb, Encoding.UTF8, "application/json") });
            if (!url.EndsWith(".addon64", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (Unreachable.Contains(url)) throw new HttpRequestException("Name or service not known");
            if (request.Method == HttpMethod.Get) lock (_gets) _gets[url] = _gets.GetValueOrDefault(url) + 1;
            var bytes = new byte[120 * 1024];
            bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
