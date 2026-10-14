using System.Net;
using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #120: ReShade under a per-game proxy DLL name (upstream's DLL-name dropdown). The name is chosen before install
/// (user, else the RHI manifest's, else the graphics API's), recorded with the payload, hash-checked on update and
/// removal, given its own Wine override, and found by every consumer that used to look for fixed names.
/// </summary>
public sealed class DllNameTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-dll-names-" + Guid.NewGuid().ToString("N"));

    public DllNameTests() => Directory.CreateDirectory(_root);

    // ── ReShade install / update / removal ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Install_UnderTheChosenName_UsesItRecordsItAndGivesItTheOverride()
    {
        var fx = Fixture("chosen");
        fx.Names.SetReShade(fx.Game, "winmm.dll");

        var result = await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = false });

        Assert.True(result.Success, result.Message);
        var winmm = Path.Combine(fx.Bin, "winmm.dll");
        Assert.True(BinarySignatureInspector.LooksLikeReShade(winmm));
        Assert.False(File.Exists(Path.Combine(fx.Bin, "dxgi.dll")));
        var record = fx.Records.Find(fx.Game, ManagedComponentKind.ReShade)!;
        Assert.Equal(winmm, ProxyPayloadLocator.ReShadePayload(record)!.Path);
        Assert.Equal(FileHash.Sha256Hex(winmm), record.Files[0].Sha256, ignoreCase: true);
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(fx.Registry(), "winmm", "Game.exe"));
        Assert.Null(WineDllOverrideService.TryReadOverride(fx.Registry(), "dxgi", "Game.exe"));
        Assert.Contains("applied as winmm.dll", result.Message);
    }

    [Fact]
    public async Task Update_KeepsTheRecordedName_EvenAfterTheChoiceChanged_AndRemovalTakesItBack()
    {
        var fx = Fixture("update");
        fx.Names.SetReShade(fx.Game, "winmm.dll");
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = false })).Success);
        var before = LinuxDeploymentRecord.PayloadFingerprint(fx.Records.Find(fx.Game, ManagedComponentKind.ReShade)!);

        fx.Names.SetReShade(fx.Game, "version.dll");
        var update = await fx.Service.UpdateAsync(fx.Game);

        Assert.True(update.Success, update.Message);
        Assert.True(File.Exists(Path.Combine(fx.Bin, "winmm.dll")));
        Assert.False(File.Exists(Path.Combine(fx.Bin, "version.dll")));
        // Same version and bytes under the same name: Update All does not count it as updated (#113).
        Assert.Equal(before, LinuxDeploymentRecord.PayloadFingerprint(fx.Records.Find(fx.Game, ManagedComponentKind.ReShade)!));

        var remove = await fx.Service.RemoveAsync(fx.Game);
        Assert.True(remove.Success, remove.Message);
        Assert.False(File.Exists(Path.Combine(fx.Bin, "winmm.dll")));
        Assert.Null(WineDllOverrideService.TryReadOverride(fx.Registry(), "winmm", "Game.exe"));
        Assert.Null(fx.Records.Find(fx.Game, ManagedComponentKind.ReShade));
    }

    [Fact]
    public async Task ChangedOutsideRhi_UnderTheChosenName_UpdateAndRemovalAreRefused()
    {
        var fx = Fixture("hash");
        fx.Names.SetReShade(fx.Game, "dinput8.dll");
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = false })).Success);
        var payload = Path.Combine(fx.Bin, "dinput8.dll");
        File.AppendAllText(payload, "user change");
        var changed = File.ReadAllBytes(payload);

        var update = await fx.Service.UpdateAsync(fx.Game);
        var remove = await fx.Service.RemoveAsync(fx.Game);

        Assert.False(update.Success);
        Assert.Contains("modified outside RHI", update.Message);
        Assert.False(remove.Success);
        Assert.Contains("Protected modified file", remove.Message);
        Assert.Equal(changed, File.ReadAllBytes(payload));
    }

    [Fact]
    public async Task Install_UnderTheChosenName_NeverOverwritesAForeignFile()
    {
        var fx = Fixture("foreign");
        fx.Names.SetReShade(fx.Game, "version.dll");
        var foreign = Path.Combine(fx.Bin, "version.dll");
        File.WriteAllBytes(foreign, [0x4D, 0x5A, 1, 2, 3]);

        var result = await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = false });

        Assert.False(result.Success);
        Assert.Contains("Foreign DLL protection: 'version.dll'", result.Message);
        Assert.Equal([0x4D, 0x5A, 1, 2, 3], File.ReadAllBytes(foreign));
        Assert.Null(fx.Records.Find(fx.Game, ManagedComponentKind.ReShade));
    }

    [Fact]
    public async Task ReShade64AsTheChosenName_GetsNoWineOverride()
    {
        var fx = Fixture("own-name");
        fx.Names.SetReShade(fx.Game, "ReShade64.dll");

        var result = await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = false });

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(Path.Combine(fx.Bin, "ReShade64.dll")));
        Assert.Null(fx.Records.Find(fx.Game, ManagedComponentKind.ReShade)!.WineOverride);
        Assert.DoesNotContain("DllOverrides", fx.Registry());
    }

    [Fact]
    public async Task ANameForTheOtherBitness_IsRefusedBeforeAnyChange()
    {
        var fx = Fixture("bitness", machine: 0x014c);
        fx.Names.SetReShade(fx.Game, "ReShade64.dll");

        var result = await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = false });

        Assert.False(result.Success);
        Assert.Equal(ReShadeDeploymentService.NameNotForBitnessPrefix + "ReShade64.dll", result.Message);
        Assert.Empty(fx.Handler.Requests);
        Assert.Empty(Directory.EnumerateFiles(fx.Bin, "*.dll"));
    }

    // ── which name: user, manifest (as upstream), graphics API ─────────────────────────────────────────────────

    [Fact]
    public async Task ManifestName_IsUsedLikeUpstream_AndTheUserChoiceWins()
    {
        var fx = Fixture("manifest", api: GraphicsApi.DirectX11);
        fx.Game.ReShadeDllNameOverride = "d3d11.dll";
        Assert.Equal(new ProxyNameResolution("d3d11.dll", ProxyNameSource.Manifest), fx.Service.ResolveProxyName(fx.Game));

        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = false })).Success);
        Assert.True(File.Exists(Path.Combine(fx.Bin, "d3d11.dll")));
        Assert.False(File.Exists(Path.Combine(fx.Bin, "dxgi.dll")));

        fx.Names.SetReShade(fx.Game, "winmm.dll");
        Assert.Equal(new ProxyNameResolution("winmm.dll", ProxyNameSource.User), fx.Service.ResolveProxyName(fx.Game));
    }

    // Upstream's manifest names dxgi.dll for DirectX 9 titles that run through dgVoodoo2; a DirectX 9 game does not load
    // dxgi.dll, and RHI moves ReShade there only when that translation is installed.
    [Fact]
    public void ManifestName_ThatTheGamesApiWouldNotLoad_IsIgnoredAndSaysSo()
    {
        var fx = Fixture("manifest-dx9", api: GraphicsApi.DirectX9);
        fx.Game.ReShadeDllNameOverride = "dxgi.dll";

        Assert.Equal(new ProxyNameResolution("d3d9.dll", ProxyNameSource.GraphicsApi, "dxgi.dll"), fx.Service.ResolveProxyName(fx.Game));
    }

    [Theory]
    [InlineData("../../evil.dll")]
    [InlineData("/etc/passwd")]
    [InlineData("kernel32.dll")]
    public void OnlyOfferedNames_AreStoredOrUsed(string name)
    {
        var fx = Fixture("names");
        Assert.Throws<ArgumentException>(() => fx.Names.SetReShade(fx.Game, name));

        // A hand-edited file or a manifest value outside the list reads as automatic.
        File.WriteAllText(fx.NamesPath, $$"""{ "{{GameRecordIdentity.Key(fx.Game)}}": { "reshade": "{{name}}" } }""");
        Assert.Null(fx.Names.GetReShade(fx.Game));
        fx.Game.ReShadeDllNameOverride = name;
        Assert.Equal("dxgi.dll", fx.Service.ResolveProxyName(fx.Game)!.Name);
    }

    // ── one ReShade per game (w3 review major 2) ────────────────────────────────────────────────────────────────

    // The user's own ReShade as dxgi.dll and a choice of dinput8.dll: a second ReShade would load beside it.
    [Fact]
    public async Task Install_UnderAChosenName_WithTheUsersReShadeAsDxgi_IsRefusedBeforeAnyChange()
    {
        var fx = Fixture("second-user");
        var dxgi = Path.Combine(fx.Bin, "dxgi.dll");
        WriteSigned(dxgi, "ReShade crosire reshade.me");
        var before = File.ReadAllBytes(dxgi);
        fx.Names.SetReShade(fx.Game, "dinput8.dll");
        var registry = fx.Registry();

        var result = await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = false });

        Assert.False(result.Success);
        Assert.Equal(ReShadeDeploymentService.OtherReShadePresentPrefix + dxgi, result.Message);
        Assert.False(File.Exists(Path.Combine(fx.Bin, "dinput8.dll")));
        Assert.Equal(before, File.ReadAllBytes(dxgi));
        Assert.Null(fx.Records.Find(fx.Game, ManagedComponentKind.ReShade));
        Assert.Equal(registry, fx.Registry());
        Assert.Empty(fx.Handler.Requests);
    }

    // The manifest names version.dll for the game and the user did nothing: same refusal (no user action at all).
    [Fact]
    public async Task Install_UnderTheManifestName_WithTheUsersReShadeAsDxgi_IsRefused()
    {
        var fx = Fixture("second-manifest", api: GraphicsApi.DirectX11);
        fx.Game.ReShadeDllNameOverride = "version.dll";
        var dxgi = Path.Combine(fx.Bin, "DXGI.dll");
        WriteSigned(dxgi, "ReShade crosire reshade.me");

        var result = await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = false });

        Assert.False(result.Success);
        Assert.Equal(ReShadeDeploymentService.OtherReShadePresentPrefix + dxgi, result.Message);
        Assert.False(File.Exists(Path.Combine(fx.Bin, "version.dll")));
        Assert.Null(fx.Records.Find(fx.Game, ManagedComponentKind.ReShade));
    }

    // The other way round: the user's ReShade under a name only the dropdown knows (dinput8.dll), and an Automatic
    // install that would put RHI's as dxgi.dll.
    [Fact]
    public async Task Install_Automatic_WithTheUsersReShadeAsDinput8_IsRefused()
    {
        var fx = Fixture("second-dinput8");
        var dinput8 = Path.Combine(fx.Bin, "dinput8.dll");
        WriteSigned(dinput8, "ReShade crosire reshade.me");

        var result = await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = false });

        Assert.False(result.Success);
        Assert.Equal(ReShadeDeploymentService.OtherReShadePresentPrefix + dinput8, result.Message);
        Assert.False(File.Exists(Path.Combine(fx.Bin, "dxgi.dll")));
    }

    // Luma and ShortFuse need an RHI-owned ReShade and install it automatically: with the user's ReShade under another
    // name than the chosen one, nothing is installed (their dependency guard sees it under any offered name).
    [Theory]
    [InlineData(ManagedComponentKind.LumaFramework)]
    [InlineData(ManagedComponentKind.ShortFuse)]
    public async Task AutomaticReShade_ForLumaOrShortFuse_WithTheUsersReShadeAsDxgi_IsRefused(ManagedComponentKind target)
    {
        var fx = Fixture("second-" + target);
        var dxgi = Path.Combine(fx.Bin, "dxgi.dll");
        WriteSigned(dxgi, "ReShade crosire reshade.me");
        fx.Names.SetReShade(fx.Game, "dinput8.dll");
        var managed = Managed(fx);
        var targetRan = false;

        var result = await managed.InstallWithManagedDependenciesAsync(fx.Game, target, [], () =>
        {
            targetRan = true;
            return Task.FromResult(ComponentOperationResult.Ok("installed"));
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(targetRan);
        Assert.Contains("Um ReShade externo foi detectado", result.Message);
        Assert.False(File.Exists(Path.Combine(fx.Bin, "dinput8.dll")));
        Assert.Null(fx.Records.Find(fx.Game, ManagedComponentKind.ReShade));
    }

    // RHI's own ReShade under another name is reused: a profile Apply after the choice changed keeps the recorded name
    // and installs nothing beside it.
    [Fact]
    public async Task Apply_AfterTheChoiceChanged_ReusesRhisReShadeUnderItsRecordedName()
    {
        var fx = Fixture("reuse");
        fx.Names.SetReShade(fx.Game, "winmm.dll");
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = false })).Success);
        fx.Names.SetReShade(fx.Game, "version.dll");

        var apply = await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = false });

        Assert.True(apply.Success, apply.Message);
        Assert.True(File.Exists(Path.Combine(fx.Bin, "winmm.dll")));
        Assert.False(File.Exists(Path.Combine(fx.Bin, "version.dll")));
    }

    // OptiScaler under another name is not ReShade (the scan's rule): the install goes ahead.
    [Fact]
    public async Task Install_WithOptiScalerUnderAnotherName_IsNotTakenForASecondReShade()
    {
        var fx = Fixture("not-second");
        WriteSigned(Path.Combine(fx.Bin, "winmm.dll"), "OptiScaler LoadReshade ReShade64.dll");

        var result = await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = false });

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(Path.Combine(fx.Bin, "dxgi.dll")));
    }

    // ── consumers that used to look for fixed names ────────────────────────────────────────────────────────────

    [Fact]
    public void ComponentScanAndAddonPrerequisite_FindReShadeUnderAnyOfferedName_ButNotOptiScaler()
    {
        var fx = Fixture("probe");
        var winmm = Path.Combine(fx.Bin, "winmm.dll");
        WriteSigned(winmm, "ReShade crosire reshade.me");

        Assert.True(GitHubReShadeAddonDeploymentService.HasReShade(fx.Bin));
        Assert.Equal(ComponentAvailability.Installed, ReShadeRow(fx).Availability);

        // OptiScaler under a new name mentions ReShade (LoadReshade) but is not ReShade.
        WriteSigned(winmm, "OptiScaler LoadReshade ReShade64.dll");
        Assert.False(GitHubReShadeAddonDeploymentService.HasReShade(fx.Bin));
        Assert.NotEqual(ComponentAvailability.Installed, ReShadeRow(fx).Availability);
    }

    [Fact]
    public async Task RenoDx_InstallsOverReShadeUnderAChosenName()
    {
        var fx = Fixture("renodx");
        fx.Names.SetReShade(fx.Game, "version.dll");
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = false })).Success);
        var http = new HttpClient(new WikiHandler());
        var renodx = new RenoDxDeploymentService(http, fx.Records, new RenoDxCatalogService(http), Path.Combine(_root, "renodx-cache"));

        var result = await renodx.InstallAsync(fx.Game, [fx.Game.ExecutablePath!]);

        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public async Task Dx9Translation_WithReShadeUnderAChosenName_IsRefusedAndChangesNothing()
    {
        var fx = Fixture("dx9", api: GraphicsApi.DirectX9);
        fx.Names.SetReShade(fx.Game, "dinput8.dll");
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = false })).Success);
        var before = File.ReadAllBytes(Path.Combine(fx.Bin, "dinput8.dll"));
        var registry = fx.Registry();

        var coordinator = new Dx9TranslationReShadeCoordinator(fx.Records, new WineDllOverrideService());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.MigrateAsync(fx.Game));

        Assert.Equal(Dx9TranslationReShadeCoordinator.NeedsReShadeAsD3d9Prefix + "dinput8.dll", error.Message);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(fx.Bin, "dinput8.dll")));
        Assert.False(File.Exists(Path.Combine(fx.Bin, "dxgi.dll")));
        Assert.Equal(registry, fx.Registry());
    }

    // w3 review minor 5: a DirectX 9 game where the user chose dxgi.dll (upstream's manifest name for its dgVoodoo2
    // titles): the refusal says what to do, in plain words, in both languages; nothing changes.
    [Fact]
    public async Task Dx9Translation_WithReShadeChosenAsDxgi_SaysPlainlyWhatToDo()
    {
        var fx = Fixture("dx9-dxgi", api: GraphicsApi.DirectX9);
        fx.Names.SetReShade(fx.Game, "dxgi.dll");
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = false })).Success);
        var before = File.ReadAllBytes(Path.Combine(fx.Bin, "dxgi.dll"));
        var registry = fx.Registry();

        var coordinator = new Dx9TranslationReShadeCoordinator(fx.Records, new WineDllOverrideService());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.MigrateAsync(fx.Game));

        Assert.Equal(Dx9TranslationReShadeCoordinator.NeedsReShadeAsD3d9Prefix + "dxgi.dll", error.Message);
        Assert.Contains("set its DLL name to Automatic", error.Message);
        var luma = "Luma Framework installation rolled back: " + error.Message;
        var portuguese = RHI.Desktop.Localization.UiLocalizationService.PresentFor("pt-BR", luma);
        Assert.Contains("defina o nome da DLL como Automático", portuguese);
        Assert.DoesNotContain(RHI.Desktop.Localization.UiLocalizationService.EnglishMarkersIn(portuguese), m => !m.Equals("no", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(fx.Bin, "dxgi.dll")));
        Assert.False(File.Exists(Path.Combine(fx.Bin, "d3d9.dll")));
        Assert.Equal(registry, fx.Registry());
    }

    [Fact]
    public async Task MovedGame_RepairCarriesTheChoiceAndTheRecord_AndThePayloadIsStillFound()
    {
        var fx = Fixture("moved");
        fx.Game.Source = "Manual";
        fx.Names.SetReShade(fx.Game, "winmm.dll");
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = false })).Success);

        // The user moves the game folder.
        var oldRoot = fx.Game.InstallPath;
        var newRoot = Path.Combine(_root, "moved-library", "Game");
        Directory.CreateDirectory(Path.GetDirectoryName(newRoot)!);
        Directory.Move(oldRoot, newRoot);
        var moved = new DetectedGame
        {
            Name = fx.Game.Name, Source = "Manual", InstallPath = newRoot, BinaryDirectory = Path.Combine(newRoot, "bin"),
            ExecutablePath = Path.Combine(newRoot, "bin", "Game.exe"), PrefixPath = fx.Game.PrefixPath, Runner = "Proton",
            GraphicsApi = GraphicsApi.DirectX12
        };
        Assert.Null(fx.Names.GetReShade(moved));
        var relocation = new DeploymentRelocation(fx.Records);
        // The repair is offered for the record under the old folder.
        Assert.True(relocation.NeedsRepair(moved, [fx.Names]));
        Assert.Contains(relocation.Preview(moved), r => r.Record.Component == ManagedComponentKind.ReShade);

        var result = relocation.Repair(moved, [fx.Names], out _);

        Assert.True(result.Success, result.Message);
        Assert.Equal("winmm.dll", fx.Names.GetReShade(moved));
        var payload = ProxyPayloadLocator.ReShadePayload(fx.Records.Find(moved, ManagedComponentKind.ReShade));
        Assert.Equal(Path.Combine(moved.BinaryDirectory!, "winmm.dll"), payload!.Path);
        Assert.Equal([], fx.Names.MovedPaths(moved));
    }

    // PatchTargetGuard: a store-keyed game keeps its key when moved; its ReShade record under the chosen name still
    // points at the old folder, so every change is refused until the repair re-points it.
    [Fact]
    public async Task MovedStoreKeyedGame_IsGuardedUntilRepaired()
    {
        var fx = Fixture("guarded");
        fx.Game.SteamAppId = 4711;
        fx.Names.SetReShade(fx.Game, "winmm.dll");
        Assert.True((await fx.Service.InstallAsync(fx.Game, new ReShadeInstallOptions { ManageIni = false })).Success);
        var newRoot = Path.Combine(_root, "guarded-library", "Game");
        Directory.CreateDirectory(Path.GetDirectoryName(newRoot)!);
        Directory.Move(fx.Game.InstallPath, newRoot);
        var moved = new DetectedGame
        {
            Name = fx.Game.Name, Source = "Steam", SteamAppId = 4711, InstallPath = newRoot, BinaryDirectory = Path.Combine(newRoot, "bin"),
            ExecutablePath = Path.Combine(newRoot, "bin", "Game.exe"), PrefixPath = fx.Game.PrefixPath, Runner = "Proton", GraphicsApi = GraphicsApi.DirectX12
        };
        var relocation = new DeploymentRelocation(fx.Records);

        Assert.StartsWith(DeploymentRelocation.GuardPrefix, relocation.Guard(moved)!.Message);
        Assert.True(relocation.Repair(moved, [fx.Names], out _).Success);
        Assert.Null(relocation.Guard(moved));
        Assert.Equal(Path.Combine(moved.BinaryDirectory!, "winmm.dll"), ProxyPayloadLocator.ReShadePayload(fx.Records.Find(moved, ManagedComponentKind.ReShade))!.Path);
        Assert.Equal("winmm.dll", fx.Names.GetReShade(moved));
    }

    // ── fixture ────────────────────────────────────────────────────────────────────────────────────────────────

    private ManagedComponent ReShadeRow(GameFixture fx)
        => ComponentProbe.Probe(fx.Game, Directory.EnumerateFiles(fx.Bin).ToList()).Single(c => c.Kind == ManagedComponentKind.ReShade);

    private static LinuxManagedComponentService Managed(GameFixture fx)
        => new(fx.Records, reshade: fx.Service, addonManager: null!, renodx: null!, renodxUpgrade: null!,
            optiscaler: null!, relimiter: null!, displayCommander: null!, mfgAdaUnlock: null!, dlssg2030: null!,
            dlssEnablerStandalone: null!, rtx40Mfg: null!, dlss5Feeder: null!, dlss5Bridge: null!, d3dCompilerTools: null!,
            ultimateAsiLoader: null!, shortFuse: null!, dofFix: null!, reFramework: null!, lumaFramework: null!, lumaPolicy: null!);

    private static void WriteSigned(string path, string signature)
    {
        var bytes = new byte[8192];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        Encoding.ASCII.GetBytes(signature).CopyTo(bytes, 512);
        File.WriteAllBytes(path, bytes);
    }

    private sealed record GameFixture(DetectedGame Game, string Bin, string Prefix, LinuxDeploymentRecordStore Records, DllNamePreferenceStore Names,
        string NamesPath, ReShadeDeploymentService Service, SetupHandler Handler)
    {
        public string Registry() => File.ReadAllText(Path.Combine(Prefix, "user.reg"));
    }

    private GameFixture Fixture(string name, GraphicsApi api = GraphicsApi.DirectX12, ushort machine = 0x8664)
    {
        var root = Path.Combine(_root, name, "Game");
        var bin = Path.Combine(root, "bin");
        var prefix = Path.Combine(_root, name, "pfx");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(prefix);
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, machine);
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\Other] 1\n\"x\"=\"y\"\n");
        var game = new DetectedGame
        {
            Name = "Game " + name, InstallPath = root, BinaryDirectory = bin, ExecutablePath = exe, PrefixPath = prefix,
            Runner = "Proton", GraphicsApi = api, Source = "Test"
        };
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, name, "records.json"));
        var namesPath = Path.Combine(_root, name, "dll-names.json");
        var names = new DllNamePreferenceStore(namesPath);
        var handler = new SetupHandler();
        var http = new HttpClient(handler);
        var cache = Path.Combine(_root, name, "cache");
        var service = new ReShadeDeploymentService(http, new TransactionalFileDeploymentService(), records, new WineDllOverrideService(),
            new StandInExtractor(), cache, new ReShadeShaderPackService(http, Path.Combine(cache, "shader-packs"), customRoot: Path.Combine(cache, "custom-shaders")),
            dllNames: names);
        return new GameFixture(game, bin, prefix, records, names, namesPath, service, handler);
    }

    internal sealed class SetupHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            lock (Requests) Requests.Add(uri.AbsoluteUri);
            if (uri.Host == "reshade.me" && uri.AbsolutePath == "/")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("<a href=\"/downloads/ReShade_Setup_6.8.0_Addon.exe\">Download</a>") });
            var setup = new byte[600_000];
            setup[0] = (byte)'M';
            setup[1] = (byte)'Z';
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(setup) });
        }
    }

    private sealed class StandInExtractor : IReShadeArchiveExtractor
    {
        public Task ExtractDllsAsync(string setupExe, string outputDirectory, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(outputDirectory);
            Write(Path.Combine(outputDirectory, "ReShade64.dll"), 0x8664);
            Write(Path.Combine(outputDirectory, "ReShade32.dll"), 0x014c);
            return Task.CompletedTask;
        }

        private static void Write(string path, ushort machine)
        {
            PeArchitectureDetectorTests.WritePe(path, machine, 1_100_000);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            stream.Position = 512;
            stream.Write(Encoding.ASCII.GetBytes("ReShade crosire reshade.me"));
        }
    }

    // The RenoDX wiki with one generic-free entry for the test game, and an add-on download.
    private sealed class WikiHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsoluteUri == RenoDxCatalogService.WikiUrl)
            {
                const string html = "<table><tr><th>Name</th><th>Links</th><th>Status</th></tr>" +
                                    "<tr><td>Game renodx</td><td><a href=\"https://clshortfuse.github.io/renodx/renodx-game.addon64\">snapshot</a></td><td>✅</td></tr></table>";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) });
            }
            var bytes = new byte[120 * 1024];
            bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
            Encoding.ASCII.GetBytes("RenoDX").CopyTo(bytes, 256);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
