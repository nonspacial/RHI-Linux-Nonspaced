using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

// #172: OptiScaler's DLL-name choice (the dropdown in its settings, like ReShade's #120). Automatic = dxgi.dll; a saved name
// is the one the install takes; a name another RHI component holds is not offered and never used; the installed name
// stays until removal.
public sealed partial class OptiScalerDeploymentServiceTests
{
    [Fact]
    public async Task ChosenDllName_IsTheNameTheInstallTakes_AndStaysWhileInstalled()
    {
        var (game, store) = CreateGame("choice-install", GraphicsApi.DirectX12);
        var (managed, names) = ManagedWithChoice(store, CreatePackage("choice-install-package"), "choice-install");
        Assert.Equal("dxgi.dll", managed.OptiScalerProxyNameFor(game)); // Automatic

        names.SetOptiScaler(game, "VERSION.dll");
        Assert.Equal("version.dll", managed.OptiScalerProxyNameFor(game));
        var installed = await managed.InstallOptiScalerAsync(game, OptiScalerVariant.Stable, nightlyBuild: null);

        Assert.True(installed.Success, installed.Message);
        Assert.True(BinarySignatureInspector.LooksLikeOptiScaler(Path.Combine(game.BinaryDirectory!, "version.dll")));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));
        Assert.Equal("version.dll", OptiScalerDeploymentService.GetProxyName(store.Find(game, ManagedComponentKind.OptiScaler)!));

        names.SetOptiScaler(game, "winmm.dll"); // a later choice does not move an installed OptiScaler
        Assert.Equal("version.dll", managed.OptiScalerProxyNameFor(game));
    }

    [Fact]
    public void NameHeldByAnotherComponent_IsNotOffered_AndASavedChoiceOfItFallsBackToDxgi()
    {
        var (game, store) = CreateGame("choice-held", GraphicsApi.DirectX12);
        SeedManagedReShade(game, store); // RHI's ReShade at dxgi.dll moves aside for OptiScaler: still offered
        var winmm = Path.Combine(game.BinaryDirectory!, "winmm.dll");
        File.WriteAllText(winmm, "MZ loader");
        store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.UltimateAsiLoader,
            BinaryDirectory = game.BinaryDirectory!, Version = "1", Files = [new LinuxDeployedFile { Path = winmm, Sha256 = Sha256(winmm) }]
        });
        var (managed, names) = ManagedWithChoice(store, CreatePackage("choice-held-package"), "choice-held");

        var offered = managed.OptiScalerProxyNamesFor(game);

        Assert.DoesNotContain("winmm.dll", offered);
        Assert.Contains("dxgi.dll", offered);
        Assert.Equal(OptiScalerDeploymentService.ProxyNames.Count - 1, offered.Count);
        names.SetOptiScaler(game, "winmm.dll");
        Assert.Equal("dxgi.dll", managed.OptiScalerProxyNameFor(game));
    }

    // Re-review nit 1: a binary folder spelled with a trailing separator still matches the folder of the holder's file.
    [Fact]
    public void HeldName_IsFound_WhenTheBinaryFolderEndsWithASeparator()
    {
        var (game, store) = CreateGame("choice-held-slash", GraphicsApi.DirectX12);
        SeedComponentFile(game, store, ManagedComponentKind.UltimateAsiLoader, "winmm.dll");
        game.BinaryDirectory = game.BinaryDirectory!.TrimEnd('/') + "/";
        var (managed, _) = ManagedWithChoice(store, CreatePackage("choice-held-slash-package"), "choice-held-slash");

        Assert.DoesNotContain("winmm.dll", managed.OptiScalerProxyNamesFor(game));
        Assert.Contains("dxgi.dll", managed.OptiScalerProxyNamesFor(game));
    }

    [Fact]
    public void DllNameStore_KeepsReShadesAndOptiScalersChoicesApart()
    {
        var (game, _) = CreateGame("choice-store", GraphicsApi.DirectX12);
        var names = new DllNamePreferenceStore(Path.Combine(_root, "choice-store", "dll-names.json"));

        names.SetReShade(game, "d3d11.dll");
        names.SetOptiScaler(game, "winhttp.dll");
        names.SetReShade(game, null);
        Assert.Equal("winhttp.dll", names.GetOptiScaler(game));
        names.SetReShade(game, "d3d11.dll");
        names.SetOptiScaler(game, null);
        Assert.Equal("d3d11.dll", names.GetReShade(game));
        Assert.Null(names.GetOptiScaler(game));
        Assert.Throws<ArgumentException>(() => names.SetOptiScaler(game, "opengl32.dll"));
        Assert.Throws<ArgumentException>(() => names.SetOptiScaler(game, "../dxgi.dll"));

        File.WriteAllText(Path.Combine(_root, "choice-store", "dll-names.json"),
            File.ReadAllText(Path.Combine(_root, "choice-store", "dll-names.json")).Replace("\"reshade\"", "\"optiscaler\": \"evil.dll\", \"reshade\"", StringComparison.Ordinal));
        Assert.Null(new DllNamePreferenceStore(Path.Combine(_root, "choice-store", "dll-names.json")).GetOptiScaler(game)); // hand-edited: automatic
    }

    // The dropdown (values, not wording: other test classes switch the UI language meanwhile).
    [Fact]
    public async Task Dropdown_DefaultsToAutomatic_HidesHeldNames_FollowsTheChoice_AndOffersRenamingWhileInstalled()
    {
        var (game, store) = CreateGame("choice-dropdown", GraphicsApi.DirectX12);
        var (managed, names) = ManagedWithChoice(store, CreatePackage("choice-dropdown-package"), "choice-dropdown");

        var fresh = RHI.Desktop.ViewModels.OptiScalerDllNamePresenter.For(managed, game, busy: false);
        Assert.Equal([null, .. OptiScalerDeploymentService.ProxyNames], fresh.Options.Select(o => o.Value));
        Assert.Null(fresh.Selected.Value);
        Assert.Contains("dxgi.dll", fresh.Selected.Display, StringComparison.Ordinal); // "Automatic (dxgi.dll)"
        Assert.True(fresh.CanChoose);
        Assert.False(RHI.Desktop.ViewModels.OptiScalerDllNamePresenter.For(managed, game, busy: true).CanChoose);

        var winmm = Path.Combine(game.BinaryDirectory!, "winmm.dll");
        File.WriteAllText(winmm, "MZ loader");
        store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.UltimateAsiLoader,
            BinaryDirectory = game.BinaryDirectory!, Version = "1", Files = [new LinuxDeployedFile { Path = winmm, Sha256 = Sha256(winmm) }]
        });
        names.SetOptiScaler(game, "d3d12.dll");
        var chosen = RHI.Desktop.ViewModels.OptiScalerDllNamePresenter.For(managed, game, busy: false);
        Assert.DoesNotContain(chosen.Options, o => o.Value == "winmm.dll");
        Assert.Equal("d3d12.dll", chosen.Selected.Value);
        Assert.Contains("winmm.dll", chosen.Hint, StringComparison.Ordinal);
        Assert.Contains("d3d12.dll", chosen.Hint, StringComparison.Ordinal);

        Assert.True((await managed.InstallOptiScalerAsync(game, OptiScalerVariant.Stable, nightlyBuild: null)).Success);
        // #196: installed, the dropdown keeps its name selected and offers the free names to rename it to in place.
        var installed = RHI.Desktop.ViewModels.OptiScalerDllNamePresenter.For(managed, game, busy: false);
        Assert.Equal("d3d12.dll", installed.Selected.Value);
        Assert.Equal([null, "dxgi.dll", "d3d11.dll", "d3d12.dll", "dbghelp.dll", "version.dll", "wininet.dll", "winhttp.dll"], installed.Options.Select(o => o.Value));
        Assert.True(installed.CanChoose);
        Assert.False(RHI.Desktop.ViewModels.OptiScalerDllNamePresenter.For(managed, game, busy: true).CanChoose);
        Assert.Contains("d3d12.dll", installed.Hint, StringComparison.Ordinal);
        Assert.Contains("rename it in place", installed.Hint, StringComparison.Ordinal);
    }

    // Review minor 3: a saved name another RHI component took since is skipped, and the install result says so plainly.
    [Fact]
    public async Task SavedNameHeldByAnotherComponent_InstallSaysItWasSkipped_AndNamesTheHolder()
    {
        var (game, store) = CreateGame("choice-skipped", GraphicsApi.DirectX12);
        SeedComponentFile(game, store, ManagedComponentKind.UltimateAsiLoader, "winmm.dll");
        var (managed, names) = ManagedWithChoice(store, CreatePackage("choice-skipped-package"), "choice-skipped");
        names.SetOptiScaler(game, "winmm.dll");
        Assert.Equal("winmm.dll (Ultimate ASI Loader)", managed.OptiScalerSkippedChoice(game));

        var installed = await managed.InstallOptiScalerAsync(game, OptiScalerVariant.Stable, nightlyBuild: null);

        Assert.True(installed.Success, installed.Message);
        Assert.EndsWith(LinuxManagedComponentService.OptiScalerSavedNameSkippedPrefix + "winmm.dll (Ultimate ASI Loader)", installed.Message, StringComparison.Ordinal);
        Assert.Equal("dxgi.dll", OptiScalerDeploymentService.GetProxyName(store.Find(game, ManagedComponentKind.OptiScaler)!));
        Assert.Null(managed.OptiScalerSkippedChoice(game)); // installed: the name is the record's now
    }

    [Fact]
    public async Task SavedNameFree_InstallSaysNothingAboutSkipping()
    {
        var (game, store) = CreateGame("choice-free", GraphicsApi.DirectX12);
        var (managed, names) = ManagedWithChoice(store, CreatePackage("choice-free-package"), "choice-free");
        names.SetOptiScaler(game, "winmm.dll");
        Assert.Null(managed.OptiScalerSkippedChoice(game));

        var installed = await managed.InstallOptiScalerAsync(game, OptiScalerVariant.Stable, nightlyBuild: null);

        Assert.True(installed.Success, installed.Message);
        Assert.DoesNotContain(LinuxManagedComponentService.OptiScalerSavedNameSkippedPrefix.Trim(), installed.Message, StringComparison.Ordinal);
    }

    // Review minors 3 (nit) and 8a: the component row's generic install takes the chosen name; with dxgi.dll itself held by
    // another component, "Automatic" stands for the next free name, the dropdown says so and the install takes it.
    [Fact]
    public async Task GenericInstall_TakesTheChosenName()
    {
        var (game, store) = CreateGame("choice-generic", GraphicsApi.DirectX12);
        var (managed, names) = ManagedWithChoice(store, CreatePackage("choice-generic-package"), "choice-generic");
        names.SetOptiScaler(game, "version.dll");

        var installed = await managed.InstallAsync(game, ManagedComponentKind.OptiScaler, []);

        Assert.True(installed.Success, installed.Message);
        Assert.True(BinarySignatureInspector.LooksLikeOptiScaler(Path.Combine(game.BinaryDirectory!, "version.dll")));
        Assert.False(File.Exists(Path.Combine(game.BinaryDirectory!, "dxgi.dll")));
        Assert.Equal("version.dll", OptiScalerDeploymentService.GetProxyName(store.Find(game, ManagedComponentKind.OptiScaler)!));
    }

    [Fact]
    public async Task DxgiHeldByAnotherComponent_AutomaticIsTheNextFreeName_InTheDropdownAndTheInstall()
    {
        var (game, store) = CreateGame("choice-dxgi-held", GraphicsApi.DirectX12);
        var holder = SeedComponentFile(game, store, ManagedComponentKind.UltimateAsiLoader, "dxgi.dll");
        var (managed, names) = ManagedWithChoice(store, CreatePackage("choice-dxgi-held-package"), "choice-dxgi-held");

        Assert.DoesNotContain("dxgi.dll", managed.OptiScalerProxyNamesFor(game));
        Assert.Equal("winmm.dll", managed.OptiScalerAutomaticProxyName(game));
        Assert.Equal("winmm.dll", managed.OptiScalerProxyNameFor(game));
        var dropdown = RHI.Desktop.ViewModels.OptiScalerDllNamePresenter.For(managed, game, busy: false);
        Assert.Null(dropdown.Options[0].Value);
        Assert.Contains("winmm.dll", dropdown.Options[0].Display, StringComparison.Ordinal); // "Automatic (winmm.dll)"
        Assert.DoesNotContain("dxgi.dll", dropdown.Options[0].Display, StringComparison.Ordinal);
        Assert.DoesNotContain(dropdown.Options, o => o.Value == "dxgi.dll");

        names.SetOptiScaler(game, "dxgi.dll"); // saved before the other component took it: skipped, and said
        var installed = await managed.InstallAsync(game, ManagedComponentKind.OptiScaler, []);

        Assert.True(installed.Success, installed.Message);
        Assert.EndsWith(LinuxManagedComponentService.OptiScalerSavedNameSkippedPrefix + "dxgi.dll (Ultimate ASI Loader)", installed.Message, StringComparison.Ordinal);
        Assert.True(BinarySignatureInspector.LooksLikeOptiScaler(Path.Combine(game.BinaryDirectory!, "winmm.dll")));
        Assert.Equal("MZ " + Path.GetFileName(holder), File.ReadAllText(holder));
        Assert.Equal("winmm.dll", OptiScalerDeploymentService.GetProxyName(store.Find(game, ManagedComponentKind.OptiScaler)!));
    }

    // Integration note (w4/encore adds its own choices): saving one choice copies every other field as it is, whatever
    // fields the type has, and the entry goes only when every choice is automatic.
    [Fact]
    public void DllNameStore_SavingOneChoice_KeepsEveryOtherField()
    {
        var (game, _) = CreateGame("choice-every-field", GraphicsApi.DirectX12);
        var path = Path.Combine(_root, "choice-every-field", "dll-names.json");
        var fields = typeof(DllNameChoices).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.CanWrite && p.PropertyType == typeof(string))
            .Select(p => p.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute), false)
                .Cast<System.Text.Json.Serialization.JsonPropertyNameAttribute>().Single().Name)
            .ToArray();
        Assert.Contains("reshade", fields);
        Assert.Contains("optiscaler", fields);
        var key = RHI.Platform.Linux.Settings.GameRecordIdentity.Key(game);

        foreach (var (field, save) in new (string, Action<DllNamePreferenceStore>)[]
                 {
                     ("reshade", store => store.SetReShade(game, "d3d11.dll")),
                     ("optiscaler", store => store.SetOptiScaler(game, "winhttp.dll"))
                 })
        {
            var seeded = fields.ToDictionary(f => f, f => "seed-" + f + ".dll");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, string>> { [key] = seeded }));

            save(new DllNamePreferenceStore(path));

            var saved = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(path))![key];
            foreach (var other in fields.Where(f => f != field))
                Assert.Equal("seed-" + other + ".dll", saved[other]);
            Assert.NotEqual("seed-" + field + ".dll", saved[field]);
        }

        File.WriteAllText(path, "{}");
        var names = new DllNamePreferenceStore(path);
        names.SetOptiScaler(game, "winhttp.dll");
        names.SetOptiScaler(game, null);
        Assert.DoesNotContain(key, File.ReadAllText(path), StringComparison.Ordinal); // all automatic: no entry
    }

    private static string SeedComponentFile(DetectedGame game, LinuxDeploymentRecordStore store, ManagedComponentKind kind, string name)
    {
        var path = Path.Combine(game.BinaryDirectory!, name);
        File.WriteAllText(path, "MZ " + name);
        store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = kind,
            BinaryDirectory = game.BinaryDirectory!, Version = "1", Files = [new LinuxDeployedFile { Path = path, Sha256 = Sha256(path) }]
        });
        return path;
    }

    internal static (LinuxManagedComponentService Managed, DllNamePreferenceStore Names) ManagedWithChoice(
        LinuxDeploymentRecordStore store, OptiScalerPackage package, string root)
        => ManagedWithChoice(store, new FakePackageProvider(package), Path.Combine(Path.GetTempPath(), "rhi-optiscaler-choice-" + root + "-" + Guid.NewGuid().ToString("N")));

    private static (LinuxManagedComponentService Managed, DllNamePreferenceStore Names) ManagedWithChoice(
        LinuxDeploymentRecordStore store, IOptiScalerPackageProvider provider, string scratch)
    {
        var names = new DllNamePreferenceStore(Path.Combine(scratch, "dll-names.json"));
        var http = new HttpClient(new NexusApiClientTests.FakeNexus(_ => throw new HttpRequestException("offline test")));
        var reshade = new ReShadeDeploymentService(http, new TransactionalFileDeploymentService(), store, new WineDllOverrideService(), null!,
            Path.Combine(scratch, "reshade-cache"),
            new ReShadeShaderPackService(http, Path.Combine(scratch, "shader-packs"), customRoot: Path.Combine(scratch, "custom-shaders")),
            dllNames: names);
        var managed = new LinuxManagedComponentService(store, reshade: reshade, addonManager: null!, renodx: null!, renodxUpgrade: null!,
            optiscaler: NewService(store, provider), relimiter: null!, displayCommander: null!, mfgAdaUnlock: null!, dlssg2030: null!,
            dlssEnablerStandalone: null!, rtx40Mfg: null!, dlss5Feeder: null!, dlss5Bridge: null!, d3dCompilerTools: null!,
            ultimateAsiLoader: null!, shortFuse: null!, dofFix: null!, reFramework: null!, lumaFramework: null!, lumaPolicy: null!,
            updateLog: new ComponentUpdateLogStore(Path.Combine(scratch, "update-log.json")));
        return (managed, names);
    }
}
