using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;
using RHI.Platform.Linux.Ux;

namespace RHI.Core.Tests;

public sealed class LinuxNativeUxTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-linux-ux-" + Guid.NewGuid().ToString("N"));

    public LinuxNativeUxTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Settings_UxFieldsRoundTripThroughXdgStore()
    {
        var settingsPath = Path.Combine(_root, "config", "settings.json");
        var store = new LinuxSettingsStore(settingsPath, Path.Combine(_root, "cache"), Path.Combine(_root, "data"));
        store.Save(new LinuxAppSettings
        {
            AddonWatcherEnabled = true,
            AddonWatchFolder = " /tmp/RHI Watch ",
            ViewMode = "compact",
            TrayIconEnabled = true,
            CloseToTray = true,
            ShowMotdOnStartup = false
        });

        var loaded = store.Load();
        Assert.True(loaded.AddonWatcherEnabled);
        Assert.Equal("/tmp/RHI Watch", loaded.AddonWatchFolder);
        Assert.Equal("Compact", loaded.ViewMode);
        Assert.True(loaded.TrayIconEnabled);
        Assert.True(loaded.CloseToTray);
        Assert.False(loaded.ShowMotdOnStartup);
    }

    [Fact]
    public void Settings_CloseToTrayCannotSurviveWhenTrayIsDisabled()
    {
        var settings = new LinuxAppSettings { TrayIconEnabled = false, CloseToTray = true }.Normalize();
        Assert.False(settings.CloseToTray);
        Assert.Equal("Detail", settings.ViewMode);
    }

    [Theory]
    [InlineData("foo.addon32", LinuxWatchedFileKind.Addon)]
    [InlineData("foo.ADDON64", LinuxWatchedFileKind.Addon)]
    [InlineData("Luma.addon", LinuxWatchedFileKind.Addon)]
    [InlineData("renodx-game.zip", LinuxWatchedFileKind.Archive)]
    [InlineData("Luma_Mod.7z", LinuxWatchedFileKind.Archive)]
    [InlineData("renodx-pack.rar", LinuxWatchedFileKind.Archive)]
    [InlineData("random.zip", LinuxWatchedFileKind.Unsupported)]
    public void AddonWatcher_ClassifiesOnlyRelevantNonDestructiveCandidates(string fileName, LinuxWatchedFileKind expected)
        => Assert.Equal(expected, LinuxAddonFileWatcher.Classify(Path.Combine(_root, fileName)));

    [Theory]
    [InlineData("game.exe", LinuxDroppedFileKind.WindowsGameExecutable)]
    [InlineData("custom.addon32", LinuxDroppedFileKind.ReShadeAddon)]
    [InlineData("custom.addon64", LinuxDroppedFileKind.ReShadeAddon)]
    [InlineData("custom.addon", LinuxDroppedFileKind.ReShadeAddon)]
    [InlineData("Luma.addon", LinuxDroppedFileKind.LumaAddon)]
    [InlineData("luma-hdr.addon64", LinuxDroppedFileKind.LumaAddon)]
    [InlineData("mod.zip", LinuxDroppedFileKind.ModArchive)]
    [InlineData("Luma_Mod.zip", LinuxDroppedFileKind.LumaArchive)]
    [InlineData("mod.7z", LinuxDroppedFileKind.ModArchive)]
    [InlineData("notes.txt", LinuxDroppedFileKind.Unsupported)]
    public void DragDrop_ClassifierRoutesOnlySupportedFileFamilies(string fileName, LinuxDroppedFileKind expected)
        => Assert.Equal(expected, LinuxDroppedFileClassifier.Classify(Path.Combine(_root, fileName)));

    [Fact]
    public void ManualInference_UnrealBinariesWin64ReturnsProjectRoot()
    {
        var root = Path.Combine(_root, "ProjectRoot");
        var binaries = Path.Combine(root, "Binaries", "Win64");
        Directory.CreateDirectory(binaries);
        var exe = Path.Combine(binaries, "Project-Win64-Shipping.exe");
        File.WriteAllBytes(exe, [0]);

        Assert.Equal(Path.GetFullPath(root), ManualGamePathInference.InferInstallRoot(exe));
    }

    [Fact]
    public void ManualInference_UnrealProjectBesideEngine_ReturnsTheGameFolder()
    {
        var game = Path.Combine(_root, "The Outer Worlds 2");
        var binaries = Path.Combine(game, "TheOuterWorlds2", "Binaries", "Win64");
        Directory.CreateDirectory(binaries);
        Directory.CreateDirectory(Path.Combine(game, "Engine", "Plugins", "Runtime", "Nvidia", "DLSS", "Binaries", "ThirdParty", "Win64"));
        var exe = Path.Combine(binaries, "TheOuterWorlds2-Win64-Shipping.exe");
        File.WriteAllBytes(exe, [0]);
        var engineExe = Path.Combine(game, "Engine", "Binaries", "Win64", "CrashReportClient.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(engineExe)!);
        File.WriteAllBytes(engineExe, [0]);

        Assert.Equal(Path.GetFullPath(game), ManualGamePathInference.InferInstallRoot(exe));
        Assert.Equal(Path.GetFullPath(game), ManualGamePathInference.InferInstallRoot(engineExe));
        Assert.Equal("The Outer Worlds 2", ManualGamePathInference.InferGameName(exe, ManualGamePathInference.InferInstallRoot(exe)));
    }

    [Fact]
    public void ManualInference_WalksUpToSteamMarker()
    {
        var root = Path.Combine(_root, "SteamGame");
        var nested = Path.Combine(root, "bin", "x64");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(root, "steam_appid.txt"), "123");
        var exe = Path.Combine(nested, "Game.exe");
        File.WriteAllBytes(exe, [0]);

        Assert.Equal(Path.GetFullPath(root), ManualGamePathInference.InferInstallRoot(exe));
    }

    [Fact]
    public void ManualInference_RemovesUnrealShippingSuffixFromGameName()
    {
        var root = Path.Combine(_root, "ProjectRoot");
        Directory.CreateDirectory(root);
        var exe = Path.Combine(root, "Expedition33-Win64-Shipping.exe");
        Assert.Equal("Expedition33", ManualGamePathInference.InferGameName(exe, root));
    }

    [Fact]
    public void NativeRyubingBinary_IsNotMisrepresentedAsWindowsAddonBundleTarget()
    {
        Assert.Equal(LinuxDroppedFileKind.Unsupported, LinuxDroppedFileClassifier.Classify(Path.Combine(_root, "Ryujinx")));
        Assert.Equal(LinuxDroppedFileKind.WindowsGameExecutable, LinuxDroppedFileClassifier.Classify(Path.Combine(_root, "Ryujinx.exe")));
    }

    [Fact]
    public async Task Motd_FirstMessageIsReturnedThenSuppressedAfterMarkSeen()
    {
        var state = Path.Combine(_root, "motd-state.json");
        using var http = new HttpClient(new StaticHandler(HttpStatusCode.OK, "  MOTD hello  "));
        var service = new LinuxMotdService(http, state, "https://example.test/motd.md");

        var first = await service.FetchAsync();
        Assert.NotNull(first);
        Assert.Equal("MOTD hello", first!.Text);
        Assert.False(first.AlreadySeen);
        Assert.Equal(64, first.Sha256.Length);
        service.MarkSeen(first);

        Assert.Null(await service.FetchAsync());
        Assert.True(File.Exists(state));
    }

    [Fact]
    public async Task Motd_ManualOpenCanReturnAlreadySeenMessage()
    {
        var state = Path.Combine(_root, "motd-manual.json");
        using var http = new HttpClient(new StaticHandler(HttpStatusCode.OK, "Same message"));
        var service = new LinuxMotdService(http, state, "https://example.test/motd.md");
        var first = Assert.IsType<LinuxMotdMessage>(await service.FetchAsync());
        service.MarkSeen(first);

        var repeated = Assert.IsType<LinuxMotdMessage>(await service.FetchAsync(includeSeen: true));
        Assert.True(repeated.AlreadySeen);
        Assert.Equal(first.Sha256, repeated.Sha256);
    }

    [Fact]
    public async Task Motd_NetworkFailureIsNonFatal()
    {
        using var http = new HttpClient(new StaticHandler(HttpStatusCode.ServiceUnavailable, "offline"));
        var service = new LinuxMotdService(http, Path.Combine(_root, "motd-offline.json"), "https://example.test/motd.md");
        Assert.Null(await service.FetchAsync());
    }

    [Fact]
    public async Task Motd_MissingFileIsNoMessage_EvenForAManualOpen()
    {
        using var http = new HttpClient(new StaticHandler(HttpStatusCode.NotFound, "404: Not Found"));
        var service = new LinuxMotdService(http, Path.Combine(_root, "motd-404.json"), "https://example.test/motd.md");
        Assert.Null(await service.FetchAsync(includeSeen: true));
    }

    [Fact]
    public async Task Motd_FileWithOnlyMaintainerCommentsShowsNothing()
    {
        using var http = new HttpClient(new StaticHandler(HttpStatusCode.OK, "<!--\nNotes for maintainers.\n-->\n\n<!-- second -->\n"));
        var service = new LinuxMotdService(http, Path.Combine(_root, "motd-comment.json"), "https://example.test/motd.md");
        Assert.Null(await service.FetchAsync(includeSeen: true));
    }

    [Fact]
    public async Task Motd_CommentsAreStrippedFromTheShownText()
    {
        using var http = new HttpClient(new StaticHandler(HttpStatusCode.OK, "<!-- how to edit -->\nHello from RHI Linux.\n"));
        var service = new LinuxMotdService(http, Path.Combine(_root, "motd-text.json"), "https://example.test/motd.md");
        Assert.Equal("Hello from RHI Linux.", Assert.IsType<LinuxMotdMessage>(await service.FetchAsync()).Text);
    }

    [Fact]
    public void DesktopOpener_UriCandidatesKeepWholeUrlAsOneArgument()
    {
        var uri = new Uri("https://example.test/path%20with%20space?q=a%20b");
        var candidates = LinuxDesktopOpener.BuildUriCandidates(uri);
        Assert.NotEmpty(candidates);
        Assert.Equal(uri.AbsoluteUri, candidates[0].ArgumentList.Single());
        Assert.Contains(candidates, candidate => candidate.FileName == "gio" && candidate.ArgumentList.Count == 2 && candidate.ArgumentList[1] == uri.AbsoluteUri);
    }

    [Fact]
    public void DesktopOpener_RejectsNonHttpSchemesBeforeSpawningAnything()
    {
        Assert.Throws<ArgumentException>(() => LinuxDesktopOpener.BuildUriCandidates(new Uri("file:///tmp/unsafe")));
        Assert.False(LinuxDesktopOpener.TryOpenUri("file:///tmp/unsafe", out var error));
        Assert.Contains("Unsupported", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CustomAddonImport_CopiesValidPayloadButPreservesSource()
    {
        var service = AddonManager("import-ok");
        var source = Path.Combine(_root, "my-hdr-tool.addon64");
        PeArchitectureDetectorTests.WritePe(source, 0x8664, 4096);

        var result = service.ImportCustomAddonFile(source);
        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(Path.Combine(service.CustomRoot, Path.GetFileName(source))));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(service.CustomRoot, Path.GetFileName(source))));
    }

    [Fact]
    public void CustomAddonImport_IsIdempotentForSameHash()
    {
        var service = AddonManager("import-idempotent");
        var source = Path.Combine(_root, "same.addon64");
        PeArchitectureDetectorTests.WritePe(source, 0x8664, 4096);
        Assert.True(service.ImportCustomAddonFile(source).Success);
        var second = service.ImportCustomAddonFile(source);
        Assert.True(second.Success);
        Assert.Contains("already present", second.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CustomAddonImport_DoesNotOverwriteDifferentExistingPayload()
    {
        var service = AddonManager("import-conflict");
        var source = Path.Combine(_root, "conflict.addon64");
        PeArchitectureDetectorTests.WritePe(source, 0x8664, 4096);
        var target = Path.Combine(service.CustomRoot, Path.GetFileName(source));
        PeArchitectureDetectorTests.WritePe(target, 0x8664, 4096);
        var bytes = File.ReadAllBytes(target); bytes[^1] = 0x5a; File.WriteAllBytes(target, bytes);
        var before = File.ReadAllBytes(target);

        var result = service.ImportCustomAddonFile(source);
        Assert.False(result.Success);
        Assert.Equal(before, File.ReadAllBytes(target));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void CustomAddonImport_RejectsDedicatedComponentPayloadsIncludingShortFuse()
    {
        Assert.True(ReShadeAddonManagerService.IsReservedCustomFileName("ReLimiter.addon64"));
        Assert.True(ReShadeAddonManagerService.IsReservedCustomFileName("renodx-upgrade.addon64"));
        Assert.True(ReShadeAddonManagerService.IsReservedCustomFileName("renodx-dlss.addon64"));
        Assert.True(ReShadeAddonManagerService.IsReservedCustomFileName("dlss5-feed.addon64"));
    }

    [Fact]
    public void CustomAddonImport_RejectsArchitectureMismatch()
    {
        var service = AddonManager("import-arch");
        var source = Path.Combine(_root, "wrong.addon64");
        PeArchitectureDetectorTests.WritePe(source, 0x014c, 4096);
        var result = service.ImportCustomAddonFile(source);
        Assert.False(result.Success);
        Assert.Contains("architecture mismatch", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(service.CustomRoot, Path.GetFileName(source))));
    }

    private ReShadeAddonManagerService AddonManager(string name)
    {
        var cache = Path.Combine(_root, name, "cache");
        var custom = Path.Combine(_root, name, "custom");
        var state = Path.Combine(_root, name, "state.json");
        var http = new HttpClient(new StaticHandler(HttpStatusCode.NotFound, ""));
        return new ReShadeAddonManagerService(http, new UpstreamRhiManifestService(http, cachePath: Path.Combine(cache, "manifest.json")), cache, custom, state);
    }

    private sealed class StaticHandler(HttpStatusCode status, string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(content, Encoding.UTF8, "text/plain") });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
