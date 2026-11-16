using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #271 custom OptiScaler builds (upstream 2.9.2 OsCustomBuildService, OsCustomBuildTests): added from a link, an archive, a
/// folder or a bare OptiScaler.dll into RHI's data folder; a version like the built-ins, never updated automatically;
/// Replace, Rename, Remove and the DLSS NR DLL tick.
/// </summary>
public sealed partial class OptiScalerCustomBuildTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-os-custom-" + Guid.NewGuid().ToString("N"));
    private readonly OptiScalerCustomBuildStore _store;
    private readonly IDisposable _flow;

    public OptiScalerCustomBuildTests()
    {
        Directory.CreateDirectory(_root);
        _store = new OptiScalerCustomBuildStore(Path.Combine(_root, "data", "optiscaler-custom"));
        _flow = OptiScalerCustomBuilds.UseInThisFlow(_store);
    }

    public void Dispose()
    {
        _flow.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    // ── Upstream's OsCustomBuildTests ─────────────────────────────────────────

    [Fact]
    public void BuiltInVersions_KeepTheirLabelsAndTypes()
    {
        Assert.Equal("F5 DLSSNR", OptiScalerVersions.Label(OptiScalerVariant.F5));
        Assert.Equal("DLSS NR", OptiScalerVersions.Label(OptiScalerVariant.DlssNr));
        Assert.Equal("Stable", OptiScalerVersions.Label(default));
        Assert.True(OptiScalerVersions.IsOs10(OptiScalerVariant.Nightly));
        Assert.False(OptiScalerVersions.IsOs10(OptiScalerVariant.Stable));
        Assert.True(OptiScalerVersions.NeedsNrDll(OptiScalerVariant.F5));
        Assert.False(OptiScalerVersions.NeedsNrDll(OptiScalerVariant.Nightly));
        Assert.True(OptiScalerVersions.IsKnown(OptiScalerVariant.Stable));
    }

    [Fact]
    public void CustomIds_AreRecognised_AndMissingOnesAreUnknown()
    {
        var missing = OptiScalerVariant.Custom("does-not-exist");
        Assert.True(missing.IsCustom);
        Assert.False(OptiScalerVariant.Stable.IsCustom);
        Assert.False(OptiScalerVersions.IsKnown(missing));
        Assert.Equal("Custom build (removed)", OptiScalerVersions.Label(missing));
        // An unknown custom build is flat: Stable's INI templates.
        var choice = new OptiScalerTemplateChoice(OptiScalerGpuType.Nvidia, true);
        Assert.Equal(OptiScalerIniTemplates.FileName(OptiScalerVariant.Stable, choice), OptiScalerIniTemplates.FileName(missing, choice));
        Assert.Equal("OptiScaler_f5.nvidia.ini", OptiScalerIniTemplates.FileName(OptiScalerVariant.F5, choice));
    }

    [Fact]
    public void FindOptiScalerDll_PicksTheShallowestWithinThreeFolders()
    {
        var root = Path.Combine(_root, "find");
        Directory.CreateDirectory(Path.Combine(root, "a", "b", "OptiScaler"));
        File.WriteAllText(Path.Combine(root, "a", "b", "OptiScaler.dll"), "x");
        File.WriteAllText(Path.Combine(root, "a", "b", "OptiScaler", "OptiScaler.dll"), "deeper");
        Assert.Equal(Path.Combine(root, "a", "b", "OptiScaler.dll"), OptiScalerCustomBuildStore.FindOptiScalerDll(root));

        var deep = Path.Combine(root, "deep", "1", "2", "3", "4");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "OptiScaler.dll"), "x");
        Assert.Null(OptiScalerCustomBuildStore.FindOptiScalerDll(Path.Combine(root, "deep")));
    }

    [Theory]
    [InlineData("https://cdn.discordapp.com/attachments/1/2/Venom-1107.7z?ex=abc&is=def", "Venom-1107")]
    [InlineData("/home/me/Downloads/v0.1.37.2-Optiscaler-F5.zip", "v0.1.37.2-Optiscaler-F5")]
    [InlineData("/home/me/Downloads/OptiScaler.dll", "OptiScaler build")]
    public void SuggestName_UsesTheFileName(string source, string expected)
        => Assert.Equal(expected, OptiScalerCustomBuildStore.SuggestName(source));

    [Fact]
    public void IsLink_OnlyAcceptsWebLinks()
    {
        Assert.True(OptiScalerCustomBuildStore.IsLink("https://github.com/x/y/releases/download/v1/a.zip", out _));
        Assert.False(OptiScalerCustomBuildStore.IsLink("/home/me/builds/a.zip", out _));
        Assert.False(OptiScalerCustomBuildStore.IsLink("file:///home/me/a.zip", out _));
    }

    // ── Adding ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddFolder_CopiesTheBuildIntoRhisDataFolder_AsAVersion()
    {
        var source = Path.Combine(_root, "unpacked");
        var buildRoot = WriteBuild(Path.Combine(source, "Venom 1107", "release"), os10: true, forwarder: true);

        var result = await Import(source, "Venom 1107");

        Assert.True(result.Succeeded, result.Error);
        var build = result.Build!;
        Assert.Equal(("venom-1107", "Venom 1107", "Custom: Venom 1107"), (build.Slug, build.Name, build.Label));
        Assert.Equal(Path.Combine(_store.Root, "venom-1107", "build"), build.Directory);
        Assert.True(File.Exists(Path.Combine(build.Directory, "OptiScaler", "libxess.dll")));
        Assert.True(build.IsOs10);
        Assert.True(build.NeedsNrDll); // the F5-style forwarder ticks it
        Assert.Equal(Path.GetFullPath(source), build.Origin);
        Assert.Null(result.Note);
        Assert.True(Directory.Exists(buildRoot)); // the user's folder is only read
        // A version everywhere, read again from the folder by a new store.
        var reread = new OptiScalerCustomBuildStore(_store.Root).Get(OptiScalerVariant.Custom("venom-1107"))!;
        Assert.Equal(build with { AddedUtc = reread.AddedUtc }, reread);
        Assert.Contains(OptiScalerVariant.Custom("venom-1107"), OptiScalerVersions.All());
        Assert.True(OptiScalerVersions.NeedsNrDll(build.Variant));
        Assert.True(OptiScalerVersions.IsOs10(build.Variant));
        Assert.False(OptiScalerVersions.IsUpdatable(build.Variant));
    }

    [Fact]
    public async Task AddBareDll_IsAFlatBuild_ASecondOfTheSameNameGetsItsOwnSlug()
    {
        var dll = Path.Combine(_root, "OptiScaler.dll");
        WritePe(dll, "not OptiScaler at all");

        var first = await Import(dll, "My build");
        var second = await Import(dll, "My build");

        Assert.True(first.Succeeded, first.Error);
        Assert.False(first.Build!.IsOs10);
        Assert.Equal(["OptiScaler.dll"], Directory.GetFiles(first.Build.Directory).Select(Path.GetFileName));
        Assert.Equal(OptiScalerCustomBuildStore.NotOptiScalerNote, first.Note); // added anyway, with a note
        Assert.False(first.Build.LooksLikeOptiScaler);
        Assert.Equal("my-build-2", second.Build!.Slug);
    }

    [Fact]
    public async Task AddArchive_IsUnpacked_ByTheHostExtractor()
    {
        var zip = Path.Combine(_root, "OptiScaler-Custom.zip");
        var content = WriteBuild(Path.Combine(_root, "zip-src", "OptiScaler-Custom"), os10: false);
        ZipFile.CreateFromDirectory(Path.Combine(_root, "zip-src"), zip);

        var result = await Import(zip, OptiScalerCustomBuildStore.SuggestName(zip));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("OptiScaler-Custom", result.Build!.Name);
        Assert.True(File.Exists(Path.Combine(result.Build.Directory, "OptiScaler.ini")));
        Assert.True(Directory.Exists(content));
    }

    [Fact]
    public async Task Refusals_ChangeNothing()
    {
        var notPe = Path.Combine(_root, "fake", "OptiScaler.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(notPe)!);
        File.WriteAllBytes(notPe, new byte[200_000]);
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);

        Assert.Equal(OptiScalerCustomBuildStore.NotWindowsDllMessage, (await Import(Path.GetDirectoryName(notPe)!, "x")).Error);
        Assert.Equal(OptiScalerCustomBuildStore.NoDllMessage, (await Import(empty, "x")).Error);
        Assert.Equal(OptiScalerCustomBuildStore.NotFoundMessage, (await Import(Path.Combine(_root, "nowhere"), "x")).Error);
        Assert.Equal(OptiScalerCustomBuildStore.NotHttpsMessage, (await Import("http://example.com/OptiScaler.zip", "x")).Error);
        Assert.Equal(OptiScalerCustomBuildStore.NameRequiredMessage, (await Import(empty, "  ")).Error);
        Assert.Empty(_store.All);
    }

    [Fact]
    public async Task AddLink_DownloadsOverHttps_TakesTheServersFileName_AndKeepsNoQueryString()
    {
        var zipBytes = ZipOf(WriteBuild(Path.Combine(_root, "dl-src", "build"), os10: true));
        var handler = new LinkHandler
        {
            Respond = request => request.RequestUri!.Host == "cdn.discordapp.com"
                ? Served(zipBytes, "application/octet-stream", "Venom-1107.zip")
                : new HttpResponseMessage(HttpStatusCode.NotFound)
        };
        using var http = new HttpClient(handler);

        var result = await _store.ImportAsync("https://cdn.discordapp.com/attachments/1/2/download?ex=abc&is=def", "Venom", null, http, new ZipExtractor());

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("https://cdn.discordapp.com/attachments/1/2/download", result.Build!.Origin); // signatures are not kept
        Assert.True(result.Build.IsOs10);
    }

    [Fact]
    public async Task AddLink_ThatOpensAWebPage_OrRedirectsElsewhere_IsRefused()
    {
        using var page = new HttpClient(new LinkHandler { Respond = _ => Served(Encoding.UTF8.GetBytes("<html/>"), "text/html", null) });
        Assert.Equal(OptiScalerCustomBuildStore.WebPageMessage,
            (await _store.ImportAsync("https://example.com/build.zip", "x", null, page, new ZipExtractor())).Error);

        // A redirect to another host than the link's own (or GitHub's release hosts) is refused before the body is read.
        var redirected = new LinkHandler
        {
            Respond = request =>
            {
                var response = Served([1, 2, 3], "application/zip", null);
                response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.example/build.zip");
                return response;
            }
        };
        using var http = new HttpClient(redirected);
        var refused = await _store.ImportAsync("https://example.com/build.zip", "x", null, http, new ZipExtractor());
        Assert.StartsWith(OptiScalerCustomBuildStore.CouldNotAddPrefix + DownloadSources.OptiScalerCustomBuild.RefusalPrefix, refused.Error, StringComparison.Ordinal);
        Assert.Empty(_store.All);
    }

    // ── Editing ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Replace_KeepsTheNameIdAndTick_Rename_Tick_AndRemove()
    {
        var first = (await Import(WriteBuild(Path.Combine(_root, "v1"), os10: false), "Venom")).Build!;
        Assert.True(_store.SetNeedsNrDll(first.Variant, true));
        var changed = 0;
        _store.Changed += () => changed++;

        var replaced = await _store.ImportAsync(WriteBuild(Path.Combine(_root, "v2"), os10: true, marker: "v2"), "ignored", first.Variant,
            new HttpClient(new LinkHandler()), new ZipExtractor());

        Assert.True(replaced.Succeeded, replaced.Error);
        Assert.Equal((first.Slug, "Venom", true, true), (replaced.Build!.Slug, replaced.Build.Name, replaced.Build.NeedsNrDll, replaced.Build.IsOs10));
        Assert.Contains("v2", File.ReadAllText(Path.Combine(replaced.Build.Directory, "marker.dll")));
        Assert.Single(_store.All);
        Assert.False(Directory.Exists(Path.Combine(_store.Root, first.Slug + ".tmp")));
        Assert.False(Directory.Exists(Path.Combine(_store.Root, first.Slug + ".old")));

        Assert.True(_store.Rename(first.Variant, "Venom 1108"));
        Assert.Equal("Custom: Venom 1108", OptiScalerVersions.Label(first.Variant));
        Assert.False(_store.Rename(first.Variant, " "));
        Assert.True(_store.SetNeedsNrDll(first.Variant, false));
        Assert.False(OptiScalerVersions.NeedsNrDll(first.Variant));
        Assert.True(_store.Remove(first.Variant));
        Assert.Empty(_store.All);
        Assert.False(Directory.Exists(Path.Combine(_store.Root, first.Slug)));
        Assert.Equal(4, changed);
        // Replacing a build that is gone is refused.
        Assert.Equal(OptiScalerCustomBuildStore.UnknownBuildMessage,
            (await _store.ImportAsync(Path.Combine(_root, "v2"), "x", first.Variant, new HttpClient(new LinkHandler()), new ZipExtractor())).Error);
    }

    // ── Installing ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CustomBuild_InstallsLikeABuiltIn_IsNeverUpdated_AndReplaceRedeploysItKeepingTheIni()
    {
        var build = (await Import(WriteBuild(Path.Combine(_root, "c1"), os10: true, marker: "v1"), "Mine")).Build!;
        var (game, store) = Game("redeploy");
        var provider = new OptiScalerPackageProvider(new HttpClient(new LinkHandler()), new ZipExtractor(), Path.Combine(_root, "cache"));
        var service = new OptiScalerDeploymentService(new TransactionalFileDeploymentService(), store, new WineDllOverrideService(), provider);

        var installed = await service.InstallAsync(game, build.Variant);

        Assert.True(installed.Success, installed.Message);
        Assert.Contains("Custom: Mine", installed.Message, StringComparison.Ordinal);
        var record = store.Find(game, ManagedComponentKind.OptiScaler)!;
        Assert.Equal(build.Variant, OptiScalerDeploymentService.GetInstalledVariant(record));
        Assert.Equal(build.RecordedVersion, record.Version);
        var ini = Path.Combine(game.BinaryDirectory!, "OptiScaler.ini");
        File.AppendAllText(ini, "\n[User]\nMine=1\n");

        // Never checked or updated automatically.
        var check = await service.CheckUpdateAsync(game);
        Assert.False(check.UpdateAvailable);
        Assert.StartsWith(OptiScalerDeploymentService.CustomBuildNotUpdatedPrefix, check.Message, StringComparison.Ordinal);
        await _store.ImportAsync(WriteBuild(Path.Combine(_root, "c2"), os10: true, marker: "v2"), "", build.Variant, new HttpClient(new LinkHandler()), new ZipExtractor());
        var update = await service.UpdateAsync(game);
        Assert.StartsWith(OptiScalerDeploymentService.CustomBuildNotUpdatedPrefix, update.Message, StringComparison.Ordinal);
        Assert.Contains("v1", File.ReadAllText(Path.Combine(game.BinaryDirectory!, "marker.dll")));

        // Replace › Update: the new files go in, the user's INI stays.
        var redeployed = await service.RedeployCustomBuildAsync(game, build.Variant);
        Assert.True(redeployed.Success, redeployed.Message);
        Assert.Contains("v2", File.ReadAllText(Path.Combine(game.BinaryDirectory!, "marker.dll")));
        Assert.Contains("Mine=1", File.ReadAllText(ini));
        Assert.StartsWith(OptiScalerDeploymentService.NotThisCustomBuildPrefix,
            (await service.RedeployCustomBuildAsync(game, OptiScalerVariant.Custom("other"))).Message, StringComparison.Ordinal);

        // Removed from RHI: the game keeps its files; reinstalling with it says it is gone.
        _store.Remove(build.Variant);
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetLatestAsync(build.Variant));
    }

    [Fact]
    public async Task BareDll_InstallsWithTheFreshInstallTemplate()
    {
        var dll = Path.Combine(_root, "bare", "OptiScaler.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
        WritePe(dll, "optiscaler/OptiScaler bare");
        var build = (await Import(dll, "Bare")).Build!;
        var (game, store) = Game("bare-install");
        var provider = new OptiScalerPackageProvider(new HttpClient(new LinkHandler()), new ZipExtractor(), Path.Combine(_root, "cache"));
        var service = new OptiScalerDeploymentService(new TransactionalFileDeploymentService(), store, new WineDllOverrideService(), provider);

        var refused = await service.InstallAsync(game, build.Variant); // no template service: nothing to put beside it
        Assert.False(refused.Success);
        Assert.Contains(OptiScalerDeploymentService.CustomBuildNeedsIniMessage, refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));

        service.IniTemplates = new OptiScalerIniTemplates(Path.Combine(_root, "inis"), () => new OptiScalerTemplateChoice(OptiScalerGpuType.Nvidia, true));
        var installed = await service.InstallAsync(game, build.Variant);
        Assert.True(installed.Success, installed.Message);
        Assert.True(File.Exists(Path.Combine(_root, "inis", "OptiScaler.nvidia.ini"))); // a flat build: Stable's template was seeded
        var ini = Path.Combine(game.BinaryDirectory!, "OptiScaler.ini");
        Assert.Contains(store.Find(game, ManagedComponentKind.OptiScaler)!.Files, file => file.Path == ini && file.AllowUserChanges);
        Assert.Contains("[Upscalers]", File.ReadAllText(ini), StringComparison.Ordinal); // the template's text, not the package's (it has none)
    }

    // ── Version switch (#273: presets carry the version) ─────────────────────

    [Fact]
    public async Task SwitchVersion_ReinstallsAsTheOtherVersion_AndKeepsTheUsersIniValues()
    {
        var a = (await Import(WriteBuild(Path.Combine(_root, "sa"), os10: true, marker: "A"), "Build A")).Build!;
        var b = (await Import(WriteBuild(Path.Combine(_root, "sb"), os10: true, marker: "B", iniExtra: "[Menu]\nShortcutKey=0x2D\n"), "Build B")).Build!;
        var (game, store) = Game("switch");
        var managed = Managed(store, out var optiscaler);

        Assert.True((await managed.InstallOptiScalerAsync(game, a.Variant)).Success);
        var ini = Path.Combine(game.BinaryDirectory!, "OptiScaler.ini");
        OptiScalerIniEditor.SetValuesInFile(ini, [("Plugins", "LoadReshade", "true"), ("Old", "OnlyInA", "1")]);

        var switched = await managed.SwitchOptiScalerVersionAsync(game, b.Variant);

        Assert.True(switched.Success, switched.Message);
        Assert.StartsWith(LinuxManagedComponentService.SwitchedPrefix + "Custom: Build A → Custom: Build B.", switched.Message, StringComparison.Ordinal);
        Assert.EndsWith(LinuxManagedComponentService.SwitchKeptSuffix + "1", switched.Message, StringComparison.Ordinal);
        Assert.Equal(b.Variant, managed.GetInstalledOptiScalerVariant(game));
        Assert.Contains("B", File.ReadAllText(Path.Combine(game.BinaryDirectory!, "marker.dll")), StringComparison.Ordinal);
        Assert.Equal("true", OptiScalerIniEditor.GetValueInFile(ini, "Plugins", "LoadReshade")); // the user's value
        Assert.Equal("0x2D", OptiScalerIniEditor.GetValueInFile(ini, "Menu", "ShortcutKey")); // the new build's own key
        Assert.Null(OptiScalerIniEditor.GetValueInFile(ini, "Old", "OnlyInA")); // the old build's key is not carried
        Assert.Single(Directory.GetFiles(optiscaler.FreshIniBackupDirectory, "OptiScaler-*.ini", SearchOption.AllDirectories)); // a copy kept

        Assert.StartsWith(LinuxManagedComponentService.SwitchSameVersionPrefix, (await managed.SwitchOptiScalerVersionAsync(game, b.Variant)).Message, StringComparison.Ordinal);
        Assert.StartsWith(OptiScalerPackageProvider.UnknownVersionPrefix, (await managed.SwitchOptiScalerVersionAsync(game, OptiScalerVariant.Custom("gone"))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwitchVersion_WhenTheNewVersionCannotBeInstalled_PutsThePreviousOneBackWithTheSettings()
    {
        var a = (await Import(WriteBuild(Path.Combine(_root, "fa"), os10: true, marker: "A"), "Build A")).Build!;
        var bareDll = Path.Combine(_root, "fbare", "OptiScaler.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(bareDll)!);
        WritePe(bareDll, "optiscaler/OptiScaler bare");
        var bare = (await Import(bareDll, "Bare")).Build!; // no INI and no template service: its install is refused
        var (game, store) = Game("switch-fail");
        var managed = Managed(store, out _);
        Assert.True((await managed.InstallOptiScalerAsync(game, a.Variant)).Success);
        var ini = Path.Combine(game.BinaryDirectory!, "OptiScaler.ini");
        OptiScalerIniEditor.SetValuesInFile(ini, [("Plugins", "LoadReshade", "true")]);

        var switched = await managed.SwitchOptiScalerVersionAsync(game, bare.Variant);

        Assert.False(switched.Success);
        Assert.StartsWith(LinuxManagedComponentService.SwitchInstallFailedPrefix, switched.Message, StringComparison.Ordinal);
        Assert.EndsWith(LinuxManagedComponentService.SwitchRestoredNote, switched.Message, StringComparison.Ordinal);
        Assert.Equal(a.Variant, managed.GetInstalledOptiScalerVariant(game));
        Assert.Equal("true", OptiScalerIniEditor.GetValueInFile(ini, "Plugins", "LoadReshade"));
    }

    private LinuxManagedComponentService Managed(LinuxDeploymentRecordStore store, out OptiScalerDeploymentService optiscaler)
    {
        var provider = new OptiScalerPackageProvider(new HttpClient(new LinkHandler()), new ZipExtractor(), Path.Combine(_root, "cache"));
        optiscaler = new OptiScalerDeploymentService(new TransactionalFileDeploymentService(), store, new WineDllOverrideService(), provider)
        {
            FreshIniBackupDirectory = Path.Combine(_root, "ini-backups-" + Guid.NewGuid().ToString("N"))
        };
        return new LinuxManagedComponentService(
            records: store, reshade: null!, addonManager: null!, renodx: null!, renodxUpgrade: null!, optiscaler: optiscaler,
            relimiter: null!, displayCommander: null!, mfgAdaUnlock: null!, dlssg2030: null!, dlssEnablerStandalone: null!,
            rtx40Mfg: null!, dlss5Feeder: null!, dlss5Bridge: null!, d3dCompilerTools: null!, ultimateAsiLoader: null!,
            shortFuse: null!, dofFix: null!, reFramework: null!, lumaFramework: null!, lumaPolicy: null!,
            neuralVersionPins: new RHI.Platform.Linux.Paths.NeuralAddonVersionPreferenceStore(Path.Combine(_root, "pins-" + Guid.NewGuid().ToString("N") + ".json")));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Task<OptiScalerCustomImportResult> Import(string source, string name)
        => _store.ImportAsync(source, name, null, new HttpClient(new LinkHandler()), new ZipExtractor());

    private static string WriteBuild(string root, bool os10, bool forwarder = false, string marker = "v1", string iniExtra = "", string dllText = "")
    {
        Directory.CreateDirectory(root);
        WritePe(Path.Combine(root, "OptiScaler.dll"), "optiscaler/OptiScaler custom " + dllText);
        File.WriteAllText(Path.Combine(root, "OptiScaler.ini"), "[Plugins]\nLoadReshade=auto\n" + iniExtra);
        File.WriteAllText(Path.Combine(root, "marker.dll"), marker);
        if (os10)
        {
            Directory.CreateDirectory(Path.Combine(root, "OptiScaler"));
            File.WriteAllText(Path.Combine(root, "OptiScaler", "libxess.dll"), "MZ xess");
        }
        if (forwarder) WritePe(Path.Combine(root, "nvngx.dll_dlssnr.dll"), "forwarder");
        return root;
    }

    private static void WritePe(string path, string signature)
    {
        var bytes = new byte[96 * 1024];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        Encoding.ASCII.GetBytes(signature).CopyTo(bytes, 128);
        File.WriteAllBytes(path, bytes);
    }

    private byte[] ZipOf(string folder)
    {
        var zip = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        ZipFile.CreateFromDirectory(folder, zip);
        return File.ReadAllBytes(zip);
    }

    private (DetectedGame Game, LinuxDeploymentRecordStore Store) Game(string name)
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
            Name = "Game", InstallPath = Path.Combine(_root, name, "game"), BinaryDirectory = bin, ExecutablePath = exe, PrefixPath = prefix,
            Runner = "Proton", GraphicsApi = GraphicsApi.DirectX12, SteamAppId = 4242
        };
        return (game, new LinuxDeploymentRecordStore(Path.Combine(_root, name, "records.json")));
    }

    private static HttpResponseMessage Served(byte[] body, string type, string? fileName)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
        if (fileName is not null) response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = fileName };
        return response;
    }

    private sealed class LinkHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; init; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = Respond(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private sealed class ZipExtractor : IOptiScalerArchiveExtractor
    {
        public Task ExtractAllAsync(string archivePath, string outputDirectory, CancellationToken cancellationToken = default)
        {
            ZipFile.ExtractToDirectory(archivePath, outputDirectory);
            return Task.CompletedTask;
        }
    }
}
