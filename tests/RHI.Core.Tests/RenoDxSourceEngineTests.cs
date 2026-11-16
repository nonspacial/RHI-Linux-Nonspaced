using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

// #286 (upstream 2.9.3 870f4a9): RenoDX's Source engine mod for the games of RenoDXdb-source.json. The one-click DXVK setup
// waits for an owner game test (design note on the issue); this pins the matching, upstream's folder rules and the guard.
public sealed class RenoDxSourceEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-renodx-source-" + Guid.NewGuid().ToString("N"));

    public RenoDxSourceEngineTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private void Exe(string relative, ushort machine)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PeArchitectureDetectorTests.WritePe(path, machine);
    }

    private void Engine(string relative)
    {
        var dir = Path.Combine(_root, relative);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "engine.dll"), "MZ");
    }

    // Upstream SourceEngineSetupTests (2.9.3), the same layouts.
    [Fact]
    public void ThirtyTwoBitGame_UsesBin()
    {
        Exe("hl2.exe", 0x014C);
        Engine("bin");

        var target = Assert.Single(RenoDxSourceEngine.DetectTargets(_root));
        Assert.Equal(Path.Combine(_root, "bin"), target.Dir);
        Assert.False(target.Is64Bit);
        Assert.Equal("bin", RenoDxSourceEngine.PrimarySubfolder(_root));
    }

    [Fact]
    public void SixtyFourBitGame_UsesTheMainFolder()
    {
        Exe("tf_win64.exe", 0x8664);
        Engine(Path.Combine("bin", "x64"));

        var target = Assert.Single(RenoDxSourceEngine.DetectTargets(_root));
        Assert.Equal(_root, target.Dir);
        Assert.True(target.Is64Bit);
        Assert.Null(RenoDxSourceEngine.PrimarySubfolder(_root));
    }

    [Fact]
    public void GarrysMod_GetsBothBuilds_NothingInTheMainFolder()
    {
        Exe("gmod.exe", 0x014C);
        Exe("gmod_win64.exe", 0x8664);
        Engine("bin");
        Engine(Path.Combine("bin", "win64"));

        Assert.Equal(
            [new RenoDxSourceEngine.Target(Path.Combine(_root, "bin", "win64"), true), new RenoDxSourceEngine.Target(Path.Combine(_root, "bin"), false)],
            RenoDxSourceEngine.DetectTargets(_root));
        Assert.Equal(Path.Combine("bin", "win64"), RenoDxSourceEngine.PrimarySubfolder(_root));
    }

    [Fact]
    public void ThirtyTwoBitGame_WithA64BitBuildInBinWin64_GetsBoth_FolderNamesIgnoringCase()
    {
        Exe("game.exe", 0x014C);
        Engine("Bin");
        Exe(Path.Combine("Bin", "Win64", "game.exe"), 0x8664);
        Engine(Path.Combine("Bin", "Win64"));

        Assert.Equal(
            [new RenoDxSourceEngine.Target(Path.Combine(_root, "Bin"), false), new RenoDxSourceEngine.Target(Path.Combine(_root, "Bin", "Win64"), true)],
            RenoDxSourceEngine.DetectTargets(_root));
    }

    [Fact]
    public void IgnoresTheUninstaller_AndAMissingEngine()
    {
        Exe("unins000.exe", 0x8664);
        Exe("game.exe", 0x014C);

        var target = Assert.Single(RenoDxSourceEngine.DetectTargets(_root));
        Assert.Equal(_root, target.Dir);
        Assert.False(target.Is64Bit);
    }

    [Fact]
    public void DatabaseLookup_ExactThenIgnoringPunctuation()
    {
        var database = RenoDxSourceDatabase.Parse(Encoding.UTF8.GetBytes(RenoDxSourceDatabaseTests.PinnedFixture))!;

        Assert.Equal("Counter Strike: Source", RenoDxSourceEngine.Find(database, "Counter-Strike: Source")?.Name);
        Assert.Equal("Black Mesa", RenoDxSourceEngine.Find(database, "black mesa")?.Name);
        Assert.Null(RenoDxSourceEngine.Find(database, "Portal Stories"));
        Assert.Null(RenoDxSourceEngine.Find(null, "Black Mesa"));
    }

    [Fact]
    public async Task SourceGame_GetsCommandGeniussSourceMod_WhenNoNamedModMatches()
    {
        var catalog = Catalog();

        var css = await catalog.ResolveAsync(new DetectedGame { Name = "Counter-Strike: Source", InstallPath = "/tmp/css" }, ["/tmp/css/hl2.exe"]);
        var blackMesa = await catalog.ResolveAsync(new DetectedGame { Name = "Black Mesa", InstallPath = "/tmp/bm" }, ["/tmp/bm/bms.exe"]);
        var other = await catalog.ResolveAsync(new DetectedGame { Name = "Not A Source Game", InstallPath = "/tmp/x" }, ["/tmp/x/game.exe"]);

        Assert.Equal(RenoDxSourceEngine.ModName, css?.Name);
        Assert.Equal(RenoDxSourceEngine.AddonUrl64, css!.UrlFor(PeArchitecture.X64));
        Assert.Equal(RenoDxSourceEngine.AddonUrl32, css.UrlFor(PeArchitecture.X86));
        Assert.Equal("CommandGenius", css.Author);
        Assert.True(css.IsGeneric);
        Assert.Equal("Some of the maps are LDR, so you won't see many highlights on them.", css.Notes);
        Assert.True(blackMesa!.IsWorkInProgress);
        Assert.Null(other);
    }

    [Fact]
    public async Task Install_OfTheSourceMod_IsRefusedWithThePlainReason_BeforeAnyDownload()
    {
        var game = RenoDxLocalAddonTests.Game(_root, "Counter-Strike: Source", 0x014C);
        var requests = new List<string>();
        var catalog = Catalog(requests);
        using var http = new HttpClient(new Handler(requests));
        var service = new RenoDxDeploymentService(http, new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json")), catalog, Path.Combine(_root, "cache"));

        var result = await service.InstallAsync(game, [game.ExecutablePath!]);

        Assert.False(result.Success);
        Assert.Equal(RenoDxSourceEngine.SetupNotYetMessage, result.Message);
        Assert.DoesNotContain(requests, url => url.Contains(".addon", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(game.BinaryDirectory!, "*.addon*"));
        Assert.StartsWith("O mod do RenoDX para a engine Source", UiLocalizationService.PresentFor("pt-BR", result.Message));
    }

    private RenoDxCatalogService Catalog(List<string>? requests = null)
    {
        var http = new HttpClient(new Handler(requests ?? []));
        return new RenoDxCatalogService(http)
        {
            SourceDatabase = new RenoDxSourceDatabaseService(http, Path.Combine(_root, "source-" + Guid.NewGuid().ToString("N") + ".json")),
        };
    }

    private sealed class Handler(List<string> requests) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            lock (requests) requests.Add(url);
            return Task.FromResult(url == RenoDxSourceDatabaseService.LiveUrl
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(RenoDxSourceDatabaseTests.PinnedFixture, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
