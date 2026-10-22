using System.Security.Cryptography;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Hdr;
using static RHI.Core.Tests.NrGpuTestKit;

namespace RHI.Core.Tests;

/// <summary>
/// #234 (owner pick: both entry points open one read-only page): what the page finds (nr-gpu's card policy and its
/// installed-version helper, never a second copy), when its two entry points show, the page's rows in EN and PT-BR, that
/// both entry points open the same page, and that opening it writes nothing anywhere (game folder, prefix, RHI's data).
/// The card comes from the fixture's seam (never the real /proc).
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class NeuralRenderingCheckViewModelTests : IDisposable
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-nr-check-vm-" + Guid.NewGuid().ToString("N"));

    public NeuralRenderingCheckViewModelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        UiLocalizationService.Apply("en-US");
        try { Directory.Delete(_root, true); } catch { }
    }

    public static TheoryData<string> Languages => new() { "en-US", "pt-BR" };

    private sealed record Bl3(DetectedGame Game, string Folder, string Bin, string Prefix, string Nr);

    /// <summary>Borderlands 3's shape: the NR DLL beside the executable (optional), a prefix of its own.</summary>
    private Bl3 Borderlands(string? nrVersion = For50, string name = "Borderlands 3", string runner = "Wine")
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var bin = Directory.CreateDirectory(Path.Combine(folder, "OakGame", "Binaries", "Win64")).FullName;
        var prefix = Directory.CreateDirectory(Path.Combine(_root, name + "-prefix", "drive_c")).FullName;
        var exe = Path.Combine(bin, "Borderlands3.exe");
        File.WriteAllBytes(exe, CacheForeignFileRejectionTests.Pe(0x8664, 4096, 7));
        var nr = Path.Combine(bin, "nvngx_dlssnr.dll");
        if (nrVersion is not null) File.WriteAllBytes(nr, Dll(nrVersion));
        var game = new DetectedGame
        {
            Name = name, Source = "Manual", Runner = runner, InstallPath = folder, ExecutablePath = exe, BinaryDirectory = bin,
            PrefixPath = Path.GetDirectoryName(prefix)
        };
        return new(game, folder, bin, Path.GetDirectoryName(prefix)!, nr);
    }

    /// <summary>Starts the shell over the games with the fake card, selects <paramref name="select"/> and waits for its analysis.</summary>
    private static async Task<ViewModelFixture> StartAsync(IReadOnlyList<DetectedGame> games, NvidiaGpuFacts gpu, string language = "en-US",
        string? select = null, Action<ViewModelFixture>? seed = null)
    {
        UiLocalizationService.Apply(language);
        var fx = await ViewModelFixture.StartAsync(games, uiLanguage: language, httpResponse: NeuralRenderingGpuViewModelTests.Answer,
            hdrProfiles: Hdr(gpu), nvidiaGpu: () => gpu);
        UiLocalizationService.Apply(language);
        seed?.Invoke(fx);
        await SelectAsync(fx, select ?? games[0].Name);
        return fx;
    }

    private static async Task SelectAsync(ViewModelFixture fx, string name)
    {
        var analysed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(AnalysisOutcome outcome)
        {
            if (outcome.Game.Name == name) analysed.TrySetResult();
        }
        fx.Vm.Session.Completed += OnCompleted;
        try
        {
            var game = fx.Vm.Games.Single(g => g.Game.Name == name).Game;
            fx.Vm.Session.Invalidate(game);
            if (ReferenceEquals(fx.Vm.SelectedGame, game)) fx.Vm.SelectedGame = null;
            fx.Vm.SelectedGame = game;
            await analysed.Task.WaitAsync(HangGuard);
        }
        finally { fx.Vm.Session.Completed -= OnCompleted; }
        await fx.WhenSelectionProbeIdleAsync(HangGuard);
    }

    private static DetectedGame Listed(ViewModelFixture fx, string name) => fx.Vm.Games.Single(row => row.Name == name).Game;

    private static Task<NrCheckDiagnosis> DiagnoseAsync(ViewModelFixture fx, DetectedGame game)
    {
        var listed = Listed(fx, game.Name);
        fx.Vm.Session.TryGetCached(listed, out var analysis);
        return fx.Managed.DiagnoseNeuralRenderingAsync(listed, analysis?.Facts.HasNativeDlssForNeuralRendering, analysis?.Dlss.DlssnrPaths);
    }

    // ---- the NR DLL line: nr-gpu's policy and helper ------------------------------------------------------------------

    [Fact]
    public async Task Bl3Shape_Rtx4090WithThe50xxOnlyDll_IsNotForThisCard_AndNamesTheDllMadeForIt()
    {
        var bl3 = Borderlands(For50);
        using var fx = await StartAsync([bl3.Game], Facts(RtxGeneration.Rtx40),
            seed: fx => NrGpuGames.SeedOwner(fx.Records, Listed(fx, bl3.Game.Name), ManagedComponentKind.Dlss5Feeder, bl3.Nr, version: "0.13.1-beta.1"));

        var dll = (await DiagnoseAsync(fx, bl3.Game)).Dll;

        Assert.Equal(NrDllFit.NotForThisCard, dll.Fit);
        Assert.Equal(For50, dll.Version);
        Assert.Equal(InstalledNrVersionSource.PeVersion, dll.VersionSource); // a record from before #230: no version metadata
        Assert.Equal(ManagedComponentKind.Dlss5Feeder, dll.Owner);
        Assert.Equal("DLSS5 Feeder", dll.OwnerName);
        Assert.Equal(ForAll, dll.LatestForCard);
        Assert.Equal(bl3.Nr, dll.Path);
        Assert.False(dll.FaultsOnThisDriver);
        // The same answer Update All's NR check reads (one helper).
        var record = fx.Records.Find(Listed(fx, bl3.Game.Name), ManagedComponentKind.Dlss5Feeder)!;
        Assert.Equal(fx.Managed.InstalledNeuralRenderingVersion(Listed(fx, bl3.Game.Name), record).Version, dll.Version);
    }

    [Fact]
    public async Task TheRightDll_ForTheCard_Fits()
    {
        var bl3 = Borderlands(ForAll);
        using var fx = await StartAsync([bl3.Game], Facts(RtxGeneration.Rtx40),
            seed: fx => NrGpuGames.SeedOwner(fx.Records, Listed(fx, bl3.Game.Name), ManagedComponentKind.ShortFuse, bl3.Nr,
                new Dictionary<string, string> { [NeuralRenderingDllSwap.MetaDllVersion] = ForAll }));

        var dll = (await DiagnoseAsync(fx, bl3.Game)).Dll;

        Assert.Equal(NrDllFit.Fits, dll.Fit);
        Assert.Equal(InstalledNrVersionSource.Metadata, dll.VersionSource);
        Assert.Equal("ShortFuse", dll.OwnerName);
    }

    [Fact]
    public async Task AnUnknownCard_CannotTellTheFit_AndNamesNoDllForIt()
    {
        var bl3 = Borderlands(For50);
        var unknown = new NvidiaGpuFacts(true, false, "NVIDIA TITAN X (Pascal)") { DriverVersion = "615.78.08" };
        using var fx = await StartAsync([bl3.Game], unknown,
            seed: fx => NrGpuGames.SeedOwner(fx.Records, Listed(fx, bl3.Game.Name), ManagedComponentKind.Dlss5Feeder, bl3.Nr));

        var diagnosis = (await DiagnoseAsync(fx, bl3.Game));

        Assert.Equal(NrDllFit.CardUnknown, diagnosis.Dll.Fit);
        Assert.Null(diagnosis.Dll.LatestForCard);
        Assert.Equal(RtxGeneration.Unknown, diagnosis.Gpu.Generation);
        Assert.Equal("NVIDIA TITAN X (Pascal)", diagnosis.Gpu.Model);
    }

    [Fact]
    public async Task TheGamesOwnDll_WithoutAnRhiOwner_IsJudgedByTheSameHelper()
    {
        var bl3 = Borderlands(ForAll);
        using var fx = await StartAsync([bl3.Game], Facts(RtxGeneration.Rtx30));

        var dll = (await DiagnoseAsync(fx, bl3.Game)).Dll;

        Assert.Null(dll.Owner);
        Assert.Null(dll.OwnerName);
        Assert.Equal(ForAll, dll.Version);
        Assert.Equal(InstalledNrVersionSource.PeVersion, dll.VersionSource);
        Assert.Equal(NrDllFit.Fits, dll.Fit);
        Assert.True(fx.Managed.NeuralRenderingCheckApplies(Listed(fx, bl3.Game.Name), nrDllFound: true));
    }

    [Fact]
    public async Task ADllRhiCannotName_IsVersionUnknown()
    {
        var bl3 = Borderlands(nrVersion: null);
        File.WriteAllBytes(bl3.Nr, Dll("310.9.99"));
        using var fx = await StartAsync([bl3.Game], Facts(RtxGeneration.Rtx40));

        var dll = (await DiagnoseAsync(fx, bl3.Game)).Dll;

        Assert.Equal(NrDllFit.VersionUnknown, dll.Fit);
        Assert.Null(dll.Version);
        Assert.Equal(bl3.Nr, dll.Path);
    }

    [Fact]
    public async Task A3108_0DllOnAnR615Driver_IsFlagged()
    {
        var bl3 = Borderlands(Old50);
        using var fx = await StartAsync([bl3.Game], Facts(RtxGeneration.Rtx50, "615.78.08"),
            seed: fx => NrGpuGames.SeedOwner(fx.Records, Listed(fx, bl3.Game.Name), ManagedComponentKind.Dlss5Feeder, bl3.Nr));

        var dll = (await DiagnoseAsync(fx, bl3.Game)).Dll;

        Assert.Equal(NrDllFit.Fits, dll.Fit);
        Assert.True(dll.FaultsOnThisDriver);
    }

    [Fact]
    public async Task AHandPickedVersion_IsShown()
    {
        var bl3 = Borderlands(For50);
        using var fx = await StartAsync([bl3.Game], Facts(RtxGeneration.Rtx40), seed: fx =>
        {
            var listed = Listed(fx, bl3.Game.Name);
            NrGpuGames.SeedOwner(fx.Records, listed, ManagedComponentKind.Dlss5Feeder, bl3.Nr,
                new Dictionary<string, string> { [NeuralRenderingDllSwap.MetaDllVersion] = For50 });
            new RHI.Platform.Linux.Paths.NeuralAddonVersionPreferenceStore(Path.Combine(fx.ConfigDir, "prefs", "neural.json")).SetNeuralRenderingDllVersion(listed, For50);
        });

        Assert.Equal(For50, (await DiagnoseAsync(fx, bl3.Game)).Dll.PickedVersion);
    }

    // ---- where the page shows (hide rule) ---------------------------------------------------------------------------------

    [Fact]
    public async Task NoNvidiaCard_HidesBothEntryPoints_EvenWithAnNrDll()
    {
        var bl3 = Borderlands(For50);
        using var fx = await StartAsync([bl3.Game], NvidiaGpuFacts.None,
            seed: fx => NrGpuGames.SeedOwner(fx.Records, Listed(fx, bl3.Game.Name), ManagedComponentKind.Dlss5Feeder, bl3.Nr));

        Assert.False(fx.Vm.NrCheck.IsAvailable);
        Assert.DoesNotContain(fx.Vm.CardMenuFor(fx.Vm.Games.Single()), item => item.Id == "nr-check");
    }

    [Fact]
    public async Task NoNrDllAndNoNrMethod_HidesBothEntryPoints()
    {
        var bl3 = Borderlands(nrVersion: null);
        using var fx = await StartAsync([bl3.Game], Facts(RtxGeneration.Rtx40));

        Assert.False(fx.Vm.NrCheck.IsAvailable);
        Assert.DoesNotContain(fx.Vm.CardMenuFor(fx.Vm.Games.Single()), item => item.Id == "nr-check");
    }

    [Fact]
    public async Task ANativeLinuxGame_HidesBothEntryPoints()
    {
        var bl3 = Borderlands(For50, runner: "Native Linux");
        using var fx = await StartAsync([bl3.Game], Facts(RtxGeneration.Rtx40));

        Assert.False(fx.Vm.NrCheck.IsAvailable);
        Assert.False(fx.Managed.NeuralRenderingCheckApplies(Listed(fx, bl3.Game.Name), nrDllFound: true));
        Assert.DoesNotContain(fx.Vm.CardMenuFor(fx.Vm.Games.Single()), item => item.Id == "nr-check");
    }

    [Fact]
    public async Task AnNrMethodWithoutTheDll_ShowsBothEntryPoints()
    {
        var bl3 = Borderlands(nrVersion: null);
        using var fx = await StartAsync([bl3.Game], Facts(RtxGeneration.Rtx40), seed: fx =>
            fx.Records.Upsert(new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(Listed(fx, bl3.Game.Name)), GameName = bl3.Game.Name,
                Component = ManagedComponentKind.Dlss5Dx11Bridge, BinaryDirectory = bl3.Bin, Version = "1.0"
            }));

        Assert.True(fx.Vm.NrCheck.IsAvailable);
        Assert.Equal("nr-check", fx.Vm.CardMenuFor(fx.Vm.Games.Single()).Last().Id);
        Assert.Equal(["DLSS5 DX11 Bridge"], (await DiagnoseAsync(fx, bl3.Game)).Method.InUse);
    }

    // #234 follow-up F4 (probe P5): the NR DLL sits in a sub-folder only the analysis finds; the card menu gives the
    // panel's answer, also once the game's analysis has left the 8-entry cache.
    [Fact]
    public async Task NrDllInASubfolder_BothEntryPointsAgree_AfterTheAnalysisLeavesTheCache()
    {
        var bl3 = Borderlands(nrVersion: null);
        var sub = Directory.CreateDirectory(Path.Combine(bl3.Bin, "Plugins", "DLSS")).FullName;
        File.WriteAllBytes(Path.Combine(sub, "nvngx_dlssnr.dll"), Dll(ForAll));
        using var fx = await StartAsync([bl3.Game], Facts(RtxGeneration.Rtx40));
        var listed = Listed(fx, bl3.Game.Name);

        var panel = fx.Vm.NrCheck.IsAvailable;
        var menuCached = fx.Vm.CardMenuFor(fx.Vm.Games.Single()).Any(item => item.Id == "nr-check");
        fx.Vm.Session.Invalidate(listed);
        var menuUncached = fx.Vm.CardMenuFor(fx.Vm.Games.Single()).Any(item => item.Id == "nr-check");

        Assert.True(panel);
        Assert.True(menuCached);
        Assert.True(menuUncached);
        Assert.False(fx.Vm.Session.TryGetCached(listed, out _));
    }

    [Fact]
    public async Task TheButton_FollowsTheSelection()
    {
        var bl3 = Borderlands(For50);
        var other = Borderlands(nrVersion: null, name: "Tunic");
        using var fx = await StartAsync([bl3.Game, other.Game], Facts(RtxGeneration.Rtx40), select: bl3.Game.Name);
        Assert.True(fx.Vm.NrCheck.IsAvailable);

        await SelectAsync(fx, other.Game.Name);
        Assert.False(fx.Vm.NrCheck.IsAvailable);

        await SelectAsync(fx, bl3.Game.Name);
        Assert.True(fx.Vm.NrCheck.IsAvailable);
    }

    // ---- the page: rows, both entry points, nothing written ----------------------------------------------------------------

    /// <summary>The prototype's data on the BL3 shape: RTX 4090, the 50xx-only DLL under the Feeder, an EAC folder, two log errors.</summary>
    private async Task<(ViewModelFixture Fx, Bl3 Game)> PrototypeShapeAsync(string language)
    {
        var bl3 = Borderlands(For50);
        Directory.CreateDirectory(Path.Combine(bl3.Folder, "EasyAntiCheat"));
        File.WriteAllText(Path.Combine(bl3.Bin, "dlss5-feed.log"), string.Concat(Enumerable.Repeat("frame ok\n", 87)) + "NGX CreateFeature(18): 0xBAD00001 FeatureNotSupported\n");
        File.WriteAllText(Path.Combine(bl3.Bin, "ReShade.log"), string.Concat(Enumerable.Repeat("loading\n", 213)) + "DLSS5_Feed.fx(88,5): error E5017: cannot map expression\n");
        var fx = await StartAsync([bl3.Game], Facts(RtxGeneration.Rtx40), language,
            seed: fx => NrGpuGames.SeedOwner(fx.Records, Listed(fx, bl3.Game.Name), ManagedComponentKind.Dlss5Feeder, bl3.Nr, version: "0.13.1-beta.1"));
        return (fx, bl3);
    }

    private static async Task<NeuralRenderingCheckDialogViewModel> OpenThroughPanelAsync(ViewModelFixture fx)
    {
        var shown = new TaskCompletionSource<NeuralRenderingCheckDialogViewModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        fx.Vm.NrCheck.ShowDialogAsync = model => { shown.TrySetResult(model); return Task.CompletedTask; };
        Assert.True(fx.Vm.NrCheck.IsAvailable);
        await fx.Vm.NrCheck.OpenCommand.ExecuteAsync(null).WaitAsync(HangGuard);
        return await shown.Task.WaitAsync(HangGuard);
    }

    private static async Task<NeuralRenderingCheckDialogViewModel> OpenThroughMenuAsync(ViewModelFixture fx)
    {
        var shown = new TaskCompletionSource<NeuralRenderingCheckDialogViewModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        fx.Vm.NrCheck.ShowDialogAsync = model => { shown.TrySetResult(model); return Task.CompletedTask; };
        var items = fx.Vm.CardMenuFor(fx.Vm.Games.Single());
        var item = items.Last(); // the owner's pick: the menu's last entry, after Copy game report
        Assert.Equal("nr-check", item.Id);
        Assert.Equal("game-report", items[^2].Id);
        Assert.Equal(UiLocalizationService.Text("Nrc.MenuItem"), item.Label);
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)item.Command).ExecuteAsync(null).WaitAsync(HangGuard);
        return await shown.Task.WaitAsync(HangGuard);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task ThePage_ShowsTheSectionsInOrder_WithWarningsAndFixHints(string language)
    {
        var (fx, bl3) = await PrototypeShapeAsync(language);
        using var _ = fx;

        var page = await OpenThroughPanelAsync(fx);

        var pt = language == "pt-BR";
        Assert.Equal(pt ? "Verificar Renderização Neural" : "Check Neural Rendering", page.WindowTitle);
        Assert.Equal(pt ? "O que o RHI encontrou em Borderlands 3. Esta página só lê arquivos." : "What RHI found for Borderlands 3. This page only reads files.", page.Intro);
        Assert.Equal(["gpu", "dll", "method", "anti-cheat", "logs"], page.Sections.Select(s => s.Id));
        Assert.Equal(pt ? ["Placa de vídeo", "DLL do Neural Rendering", "Método recomendado", "Pastas de anti-cheat", "Erros nos logs"]
                        : ["Graphics card", "Neural Rendering DLL", "Recommended method", "Anti-cheat folders", "Log findings"],
            page.Sections.Select(s => s.Label));

        var gpu = page.Sections[0].Lines;
        Assert.Equal("NVIDIA GeForce RTX 4090", gpu[0].Text);
        Assert.Equal(pt ? "Geração: RTX 40" : "Generation: RTX 40", gpu[1].Text);
        Assert.Contains("615.78.08", gpu[0].Tip); // provenance only in the tooltip

        var dll = page.Sections[1].Lines;
        Assert.Equal(pt ? "310.8.3 (50xx): não é para esta placa" : "310.8.3 (50xx): not for this card", dll[0].Text);
        Assert.True(dll[0].IsWarning);
        Assert.Contains(bl3.Nr, dll[0].Tip);
        Assert.Equal(pt ? "Colocada por DLSS5 Feeder" : "Put in by DLSS5 Feeder", dll[1].Text);
        Assert.Contains(dll, line => line.Text == (pt ? "Feita para esta placa: 310.8.2 (20/30/40/50)" : "Made for this card: 310.8.2 (20/30/40/50)"));
        Assert.Contains(dll, line => line.Text == (pt
            ? "Como resolver: escolha 310.8.2 (20/30/40/50) na lista NR DLL em Versões NR, na seção Perfil do ReShade; a troca substitui só essa DLL."
            : "Fix: choose 310.8.2 (20/30/40/50) in the NR DLL list under NR versions in the ReShade profile section; it swaps only that DLL."));

        var method = page.Sections[2].Lines;
        Assert.Equal("DLSS5 Feeder", method[0].Text); // no native DLSS in the fixture game: the Feeder is recommended
        Assert.Equal(pt ? "Em uso neste jogo: DLSS5 Feeder" : "In use in this game: DLSS5 Feeder", method[1].Text);

        var antiCheat = page.Sections[3].Lines;
        Assert.Equal(pt ? "Encontrado: EasyAntiCheat" : "Found: EasyAntiCheat", antiCheat[0].Text);
        Assert.Equal(pt ? "Só informação: o RHI não altera nada neles." : "Information only: RHI changes nothing about them.", antiCheat[1].Text);
        Assert.Contains(Path.Combine(bl3.Folder, "EasyAntiCheat"), antiCheat[0].Tip);

        var logs = page.Sections[4].Lines;
        Assert.Equal(pt ? "2 erros conhecidos" : "2 known errors", logs[0].Text);
        Assert.Equal(pt ? "ReShade.log, linha 214: o compilador de shaders não conseguiu compilar os shaders"
                        : "ReShade.log, line 214: the shader compiler could not build the shaders", logs[1].Text);
        Assert.True(logs[1].IsWarning);
        Assert.Contains("error E5017", logs[1].Tip);
        // A Wine game with its own prefix: the D3DCompiler 47 line shows Winetricks (not Protontricks) — the hint names that one.
        Assert.True(fx.Vm.ReShadeProfile.CanInstallD3DCompilerWithWinetricks);
        Assert.False(fx.Vm.ReShadeProfile.CanInstallD3DCompilerWithProtontricks);
        Assert.Equal(pt ? "Como resolver: na linha D3DCompiler 47 da seção Perfil do ReShade, use o botão do Winetricks. Procurar só escolhe a DLL da Microsoft para a próxima vez que o RHI configurar o ReShade ou o DLSS5 Feeder."
                        : "Fix: on the D3DCompiler 47 line of the ReShade profile section, use the Winetricks button. Browse only chooses Microsoft's DLL for the next time RHI sets up ReShade or the DLSS5 Feeder.", logs[2].Text);
        Assert.Equal(pt ? "dlss5-feed.log, linha 88: recurso não suportado (0xBAD00001)" : "dlss5-feed.log, line 88: feature not supported (0xBAD00001)", logs[3].Text);
        Assert.StartsWith(pt ? "Como resolver: escolha 310.8.2" : "Fix: choose 310.8.2", logs[4].Text);
        Assert.Equal(pt ? "Somente leitura: o RHI não alterou nenhum arquivo." : "Read only: RHI changed no files.", page.Footer);

        // Read only: nothing on the page offers or words an install.
        var texts = page.Sections.SelectMany(s => s.Lines.Select(l => l.Text).Append(s.Label)).Append(page.Intro).Append(page.Footer).Append(page.WindowTitle);
        Assert.DoesNotContain(texts, text => text.Contains("install", StringComparison.OrdinalIgnoreCase) || text.Contains("instal", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task ACleanGame_SaysSo(string language)
    {
        var bl3 = Borderlands(ForAll);
        using var fx = await StartAsync([bl3.Game], Facts(RtxGeneration.Rtx40), language);

        var page = await OpenThroughMenuAsync(fx);

        var pt = language == "pt-BR";
        Assert.Equal(pt ? "310.8.2 (20/30/40/50): feita para esta placa" : "310.8.2 (20/30/40/50): made for this card", page.Sections[1].Lines[0].Text);
        Assert.False(page.Sections[1].Lines[0].IsWarning);
        Assert.Equal(pt ? "Não foi colocada pelo RHI (é do jogo ou foi adicionada à mão)" : "Not put in by RHI (the game's own, or added by hand)", page.Sections[1].Lines[1].Text);
        Assert.Equal(pt ? "Nenhum configurado pelo RHI neste jogo" : "None set up by RHI in this game", page.Sections[2].Lines[1].Text);
        Assert.Equal(pt ? "Nenhuma encontrada" : "None found", page.Sections[3].Lines[0].Text);
        Assert.Equal(pt ? "Nenhum log encontrado" : "No logs found", Assert.Single(page.Sections[4].Lines).Text);
        Assert.DoesNotContain(page.Sections.SelectMany(s => s.Lines), line => line.IsWarning);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task AnUnreadableOrSpecialLog_IsALine_NeverAnError(string language)
    {
        var bl3 = Borderlands(ForAll);
        Directory.CreateDirectory(Path.Combine(bl3.Bin, "OptiScaler.log"));
        File.WriteAllText(Path.Combine(bl3.Bin, "ReShade.log"), "all fine\n");
        using var fx = await StartAsync([bl3.Game], Facts(RtxGeneration.Rtx40), language);

        var page = await OpenThroughPanelAsync(fx);

        var logs = page.Sections[4].Lines;
        Assert.Equal(language == "pt-BR" ? "Nenhum erro conhecido" : "No known errors", logs[0].Text);
        Assert.Equal(language == "pt-BR" ? "OptiScaler.log não é um arquivo comum, então não foi lido" : "OptiScaler.log is not a regular file, so it was not read", logs[1].Text);
        Assert.True(logs[1].IsWarning);
    }

    [Fact]
    public async Task BothEntryPoints_OpenTheSamePage()
    {
        var (fx, bl3) = await PrototypeShapeAsync("en-US");
        using var _ = fx;

        var fromPanel = await OpenThroughPanelAsync(fx);
        var fromMenu = await OpenThroughMenuAsync(fx);

        Assert.Same(Listed(fx, bl3.Game.Name), fromPanel.Game);
        Assert.Same(fromPanel.Game, fromMenu.Game);
        Assert.Equal(fromPanel.Sections.Select(s => (s.Id, s.Label, string.Join("|", s.Lines.Select(l => l.Text)))),
            fromMenu.Sections.Select(s => (s.Id, s.Label, string.Join("|", s.Lines.Select(l => l.Text)))));
    }

    [Fact]
    public async Task ASecondPressWhileThePageOpens_OpensNoSecondPage()
    {
        var (fx, _) = await PrototypeShapeAsync("en-US");
        using var fixture = fx;
        var shown = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fx.Vm.NrCheck.ShowDialogAsync = async model => { shown++; first.TrySetResult(); await release.Task; };

        var opening = fx.Vm.NrCheck.OpenCommand.ExecuteAsync(null);
        await first.Task.WaitAsync(HangGuard);
        var menu = fx.Vm.CardMenuFor(fx.Vm.Games.Single()).Last();
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)menu.Command).ExecuteAsync(null).WaitAsync(HangGuard);
        release.TrySetResult();
        await opening.WaitAsync(HangGuard);

        Assert.Equal(1, shown);
    }

    [Fact]
    public async Task OpeningThePage_WritesNothing_GameFolderPrefixOrRhiData()
    {
        var (fx, bl3) = await PrototypeShapeAsync("en-US");
        using var _ = fx;
        File.WriteAllText(Path.Combine(bl3.Prefix, "user.reg"), "WINE REGISTRY Version 2\n");
        var watched = new[] { bl3.Folder, bl3.Prefix, fx.Root };
        var before = Snapshot(watched);

        await OpenThroughPanelAsync(fx);
        await OpenThroughMenuAsync(fx);
        await DiagnoseAsync(fx, bl3.Game);

        Assert.Equal(before, Snapshot(watched));
    }

    /// <summary>Every entry under the folders: path, kind, size, modification time and SHA-256 (files), byte for byte.</summary>
    internal static IReadOnlyList<string> Snapshot(IEnumerable<string> roots)
    {
        var entries = new List<string>();
        foreach (var root in roots)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = 0 })
                         .Order(StringComparer.Ordinal))
            {
                var info = new FileInfo(path);
                if (info.LinkTarget is { } link) entries.Add($"L {path} -> {link}");
                else if (Directory.Exists(path)) entries.Add($"D {path} {Directory.GetLastWriteTimeUtc(path):O}");
                else entries.Add($"F {path} {info.Length} {info.LastWriteTimeUtc:O} {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}");
            }
        }
        return entries;
    }
}

/// <summary>
/// #234: every fix hint the page shows names a control that exists in the main window (the D3DCompiler 47 line's helpers,
/// the NR DLL list, the DLSS section's Neural rendering line with Apply, a component row's action, the NR Cost Scaler row)
/// or none (a plain step) — the window's layout is read for each control's bindings and handlers, for every owner and every
/// state of the controls. Whether the named control is enabled for a game is pinned per state against the live controls in
/// <see cref="NeuralRenderingCheckFollowUpTests"/>.
/// </summary>
public sealed class FixHintControlTests
{
    private static readonly string MainWindow = File.ReadAllText(Path.Combine(LocalizationKeyUsageTests.RepoRoot(), "src", "RHI.Desktop", "Views", "MainWindow.axaml"));

    private static NrCheckDiagnosis Diagnosis(ManagedComponentKind? owner, NrDllFit fit, NrCheckControls controls, params ManagedComponentKind[] inUse)
    {
        var dll = new NrCheckDll(fit == NrDllFit.NoDll ? null : "/g/nvngx_dlssnr.dll", owner, owner?.ToString(), fit is NrDllFit.ChangedOutsideRhi or NrDllFit.NoDll ? null : "310.8.3 (50xx)",
            InstalledNrVersionSource.Metadata, fit, "310.8.2 (20/30/40/50)", null, false);
        var kinds = owner is { } o ? inUse.Prepend(o).Distinct().ToArray() : inUse;
        return new NrCheckDiagnosis(new NrCheckGpu(true, "NVIDIA GeForce RTX 4090", RtxGeneration.Rtx40, "615.78.08"), dll,
            new NrCheckMethod(null, null, kinds.Select(k => k.ToString()).ToArray(), kinds), [], [], controls);
    }

    public static TheoryData<NrFixHint, ManagedComponentKind?, NrDllFit> Cases()
    {
        var data = new TheoryData<NrFixHint, ManagedComponentKind?, NrDllFit>();
        foreach (var hint in Enum.GetValues<NrFixHint>())
            foreach (var owner in new ManagedComponentKind?[] { null, ManagedComponentKind.ShortFuse, ManagedComponentKind.Dlss5Feeder, ManagedComponentKind.OptiScaler, ManagedComponentKind.RtxEncore })
                foreach (var fit in new[] { NrDllFit.NotForThisCard, NrDllFit.ChangedOutsideRhi, NrDllFit.NoDll })
                    data.Add(hint, owner, fit);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryFixHint_NamesAControlThatExists_OrNone(NrFixHint hint, ManagedComponentKind? owner, NrDllFit fit)
    {
        ManagedComponentKind[] every = [ManagedComponentKind.ShortFuse, ManagedComponentKind.Dlss5Feeder, ManagedComponentKind.Dlss5Dx11Bridge,
            ManagedComponentKind.OptiScaler, ManagedComponentKind.RtxEncore];
        foreach (var line in new[] { false, true })
            foreach (var list in new[] { false, true })
                foreach (var held in new[] { false, true })
                    foreach (var tricks in new[] { false, true })
                        foreach (var rows in new ManagedComponentKind[]?[] { null, [], every })
                    {
                        ManagedComponentKind[] heldKinds = held && owner is { } o ? [o] : [];
                        var diagnosis = Diagnosis(owner, fit, new NrCheckControls(line, list, heldKinds, tricks, !tricks, rows), ManagedComponentKind.Dlss5Dx11Bridge);

                        var control = NeuralRenderingCheckDialogViewModel.FixControl(hint, diagnosis);

                        Assert.True(NeuralRenderingCheckDialogViewModel.FixControls.TryGetValue(control, out var markers), $"{hint}/{owner}/{fit}: {control} is no known control");
                        foreach (var marker in markers!) Assert.Contains(marker, MainWindow);
                        Assert.False(string.IsNullOrWhiteSpace(NeuralRenderingCheckDialogViewModel.FixText(hint, diagnosis)));
                    }
    }

    [Fact]
    public void EachHint_FollowsWhatItsControlDoesForTheGame()
    {
        var swaps = new NrCheckControls(DlssNrLineSwaps: true, NrDllListSwaps: true, HeldByScaler: [], Protontricks: false, Winetricks: false,
            RemovableRows: [ManagedComponentKind.Dlss5Feeder, ManagedComponentKind.OptiScaler]);
        var feeder = Diagnosis(ManagedComponentKind.Dlss5Feeder, NrDllFit.NotForThisCard, swaps);
        var own = Diagnosis(null, NrDllFit.NotForThisCard, swaps);

        Assert.Equal("d3dcompiler", NeuralRenderingCheckDialogViewModel.FixControl(NrFixHint.NativeD3DCompiler, feeder));
        Assert.Equal("nr-dll-list", NeuralRenderingCheckDialogViewModel.FixControl(NrFixHint.NrDllForCard, feeder));
        Assert.Equal("nr-dll-list+component-remove", NeuralRenderingCheckDialogViewModel.FixControl(NrFixHint.NrDllForCard,
            Diagnosis(ManagedComponentKind.Dlss5Feeder, NrDllFit.NotForThisCard, swaps with { NrDllListSwaps = false })));
        Assert.Equal("nr-cost-scaler-remove", NeuralRenderingCheckDialogViewModel.FixControl(NrFixHint.NrDllForCard,
            Diagnosis(ManagedComponentKind.Dlss5Feeder, NrDllFit.NotForThisCard, swaps with { HeldByScaler = [ManagedComponentKind.Dlss5Feeder] })));
        Assert.Equal("none", NeuralRenderingCheckDialogViewModel.FixControl(NrFixHint.NrDllForCard, Diagnosis(ManagedComponentKind.Dlss5Feeder, NrDllFit.ChangedOutsideRhi, swaps)));
        Assert.Equal("dlss-nr-line", NeuralRenderingCheckDialogViewModel.FixControl(NrFixHint.NrDllForCard, own));
        Assert.Equal("none", NeuralRenderingCheckDialogViewModel.FixControl(NrFixHint.NrDllForCard, Diagnosis(null, NrDllFit.NotForThisCard, swaps with { DlssNrLineSwaps = false })));
        Assert.Equal("component-remove", NeuralRenderingCheckDialogViewModel.FixControl(NrFixHint.RemoveNrMethod, feeder));
        Assert.Equal("nr-cost-scaler-remove", NeuralRenderingCheckDialogViewModel.FixControl(NrFixHint.RemoveNrMethod,
            Diagnosis(ManagedComponentKind.Dlss5Feeder, NrDllFit.NotForThisCard, swaps with { HeldByScaler = [ManagedComponentKind.Dlss5Feeder] })));
        Assert.Equal("none", NeuralRenderingCheckDialogViewModel.FixControl(NrFixHint.RemoveNrMethod, own));
        // A row without an enabled Remove is never named as the thing to press; rows not known yet (no analysis): ask for one.
        var rtx = Diagnosis(ManagedComponentKind.RtxEncore, NrDllFit.NotForThisCard, swaps);
        Assert.Equal("none", NeuralRenderingCheckDialogViewModel.FixControl(NrFixHint.NrDllForCard, rtx));
        Assert.Equal(UiLocalizationService.Format("Nrc.FixNoRemove", ComponentProbe.RtxEncoreDisplayName), NeuralRenderingCheckDialogViewModel.FixText(NrFixHint.NrDllForCard, rtx));
        Assert.Equal("component-remove", NeuralRenderingCheckDialogViewModel.FixControl(NrFixHint.NrDllForCard, rtx with { Controls = swaps with { RemovableRows = [ManagedComponentKind.RtxEncore] } }));
        foreach (var unknown in new[] { Diagnosis(ManagedComponentKind.OptiScaler, NrDllFit.NotForThisCard, swaps with { RemovableRows = null }), own with { Controls = swaps with { RemovableRows = null } } })
        {
            Assert.Equal("none", NeuralRenderingCheckDialogViewModel.FixControl(NrFixHint.NrDllForCard, unknown));
            Assert.Equal(UiLocalizationService.Text("Nrc.FixSelectFirst"), NeuralRenderingCheckDialogViewModel.FixText(NrFixHint.NrDllForCard, unknown));
        }
        // The NR DLL list and the NR Cost Scaler row are known from RHI's records alone: named without an analysis too.
        Assert.Equal("nr-dll-list", NeuralRenderingCheckDialogViewModel.FixControl(NrFixHint.NrDllForCard, feeder with { Controls = swaps with { RemovableRows = null } }));
    }
}
