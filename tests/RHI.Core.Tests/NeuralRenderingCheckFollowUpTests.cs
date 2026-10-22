using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Hdr;
using static RHI.Core.Tests.NrGpuTestKit;

namespace RHI.Core.Tests;

/// <summary>
/// #234 follow-up (independent review of w11/nr-check, F1–F7; probes P1/P2/P3/P5/P7 adopted): the page gives Update All's
/// answer for an owner's DLL changed outside RHI (one rule), and every fix hint names only a control that is enabled and
/// does that for THIS game's state — checked here against the live controls of the selected game (the DLSS / Streamline
/// Neural rendering line, the NR DLL list, the component rows, the NR Cost Scaler row, the D3DCompiler 47 buttons) — or
/// gives a plain step naming no control.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class NeuralRenderingCheckFollowUpTests : IDisposable
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-nrc-followup-" + Guid.NewGuid().ToString("N"));

    public NeuralRenderingCheckFollowUpTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        UiLocalizationService.Apply("en-US");
        try { Directory.Delete(_root, true); } catch { }
    }

    public static TheoryData<string> Languages => new() { "en-US", "pt-BR" };

    private sealed record Game(DetectedGame Detected, string Bin, string Nr);

    private Game MakeGame(string name, string? nrVersion, bool prefix = true, int? steamAppId = null)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var bin = Directory.CreateDirectory(Path.Combine(folder, "Bin")).FullName;
        string? prefixPath = null;
        if (prefix)
        {
            prefixPath = Directory.CreateDirectory(Path.Combine(_root, name + "-prefix")).FullName;
            Directory.CreateDirectory(Path.Combine(prefixPath, "drive_c"));
        }
        var exe = Path.Combine(bin, "Game.exe");
        File.WriteAllBytes(exe, CacheForeignFileRejectionTests.Pe(0x8664, 4096, 7));
        var nr = Path.Combine(bin, "nvngx_dlssnr.dll");
        if (nrVersion is not null) File.WriteAllBytes(nr, Dll(nrVersion));
        return new(new DetectedGame
        {
            Name = name, Source = steamAppId is null ? "Manual" : "Steam", Runner = "Wine", InstallPath = folder, ExecutablePath = exe,
            BinaryDirectory = bin, PrefixPath = prefixPath, SteamAppId = steamAppId
        }, bin, nr);
    }

    private static DetectedGame Listed(ViewModelFixture fx) => fx.Vm.Games.Single().Game;

    private static async Task<ViewModelFixture> StartAsync(Game game, string language = "en-US", Action<ViewModelFixture>? seed = null,
        RtxGeneration card = RtxGeneration.Rtx40)
    {
        UiLocalizationService.Apply(language);
        var gpu = Facts(card);
        var fx = await ViewModelFixture.StartAsync([game.Detected], uiLanguage: language, httpResponse: NeuralRenderingGpuViewModelTests.Answer,
            hdrProfiles: Hdr(gpu), nvidiaGpu: () => gpu);
        UiLocalizationService.Apply(language);
        seed?.Invoke(fx);
        var listed = Listed(fx);
        var analysed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(AnalysisOutcome o) { if (o.Game.Name == game.Detected.Name) analysed.TrySetResult(); }
        fx.Vm.Session.Completed += OnCompleted;
        try
        {
            fx.Vm.Session.Invalidate(listed);
            fx.Vm.SelectedGame = null;
            fx.Vm.SelectedGame = listed;
            await analysed.Task.WaitAsync(HangGuard);
        }
        finally { fx.Vm.Session.Completed -= OnCompleted; }
        await fx.WhenSelectionProbeIdleAsync(HangGuard);
        return fx;
    }

    private static async Task<NeuralRenderingCheckDialogViewModel> OpenAsync(ViewModelFixture fx)
    {
        var shown = new TaskCompletionSource<NeuralRenderingCheckDialogViewModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        fx.Vm.NrCheck.ShowDialogAsync = m => { shown.TrySetResult(m); return Task.CompletedTask; };
        Assert.True(fx.Vm.NrCheck.IsAvailable);
        await fx.Vm.NrCheck.OpenCommand.ExecuteAsync(null).WaitAsync(HangGuard);
        return await shown.Task.WaitAsync(HangGuard);
    }

    private static void Record(ViewModelFixture fx, ManagedComponentKind kind, string bin, IEnumerable<string> files,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var game = Listed(fx);
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = kind, BinaryDirectory = bin, Version = "1.0",
            Files = files.Select(path => new LinuxDeployedFile { Path = path, Sha256 = File.Exists(path) ? RHI.Core.Deployment.FileHash.Sha256Hex(path) : "00" }).ToList(),
            Metadata = new(metadata ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)
        });
    }

    private static string Payload(Game game, string name, string marker = "MZ addon")
    {
        var path = Path.Combine(game.Bin, name);
        File.WriteAllText(path, marker);
        return path;
    }

    private static void FeederLog(Game game, string line)
        => File.WriteAllText(Path.Combine(game.Bin, "dlss5-feed.log"), "start\n" + line + "\n");

    private static string Text(string key) => UiLocalizationService.Text(key);

    // Every way a hint can name a control, checked against what that control does for the selected game right now.
    private static readonly string[] ControlWords = ["Apply", "Aplicar", "NR DLL", "on its row", "na linha dele", "Protontricks", "Winetricks", "Browse", "Procurar", "NR Cost Scaler"];

    /// <summary>
    /// The control <paramref name="hint"/> names for this game is enabled and does that action in the game's state, read
    /// from the live controls (never from the page's own facts). <paramref name="row"/>: the Managed components row a
    /// "remove it" hint must name, as the list shows it.
    /// </summary>
    private static void AssertHintIsUsable(ViewModelFixture fx, NeuralRenderingCheckDialogViewModel page, NrFixHint hint, ManagedComponentKind? row = null)
    {
        var control = NeuralRenderingCheckDialogViewModel.FixControl(hint, page.Diagnosis);
        var text = NeuralRenderingCheckDialogViewModel.FixText(hint, page.Diagnosis);
        var game = fx.Vm.SelectedGame!;
        Assert.Same(page.Game, game);
        void RowRemovable(ManagedComponentKind kind)
        {
            var rows = fx.Vm.Components.Where(r => r.Kind == kind).ToList();
            var shown = Assert.Single(rows);
            Assert.True(shown.HasAction && shown.IsRemoveAction && shown.IsActionEnabled && shown.Action == Text("Ui.ActionRemove"),
                $"{kind}'s row offers no enabled Remove ({shown.Action}, enabled={shown.IsActionEnabled})");
            Assert.Contains(shown.Name, text); // follow-up F7: the row as the list shows it
        }
        switch (control)
        {
            case "dlss-nr-line":
                Assert.True(fx.Vm.Dlss.CanSwapDlssNr, "the DLSS / Streamline Neural rendering line is disabled for this game");
                break;
            case "nr-dll-list":
                Assert.True(fx.Vm.Extras.CanConfigureNeuralRenderingDllVersion);
                Assert.True(fx.Vm.Dlss.OrchestratedDlssStackManaged, "the NR DLL list only saves the choice here (no in-place swap)");
                break;
            case "nr-dll-list+component-remove":
                Assert.True(fx.Vm.Extras.CanConfigureNeuralRenderingDllVersion);
                Assert.False(fx.Vm.Dlss.OrchestratedDlssStackManaged);
                Assert.Equal(NeuralPinChange.ReinstallNeeded, fx.Managed.PlanNeuralPinChange(game, ManagedComponentKind.Dlss5Feeder));
                RowRemovable(ManagedComponentKind.Dlss5Feeder);
                break;
            case "component-remove":
                RowRemovable(row ?? throw new Xunit.Sdk.XunitException("a remove hint without the row the test expects"));
                break;
            case "nr-cost-scaler-remove":
                var scaler = new NrCostScalerPresenter(fx.Managed).StateFor(fx.Vm);
                Assert.True(scaler is { Visible: true, Installed: true, CanAct: true }, $"the NR Cost Scaler row has no enabled Remove ({scaler})");
                Assert.Contains("NR Cost Scaler", text);
                break;
            case "d3dcompiler":
                Assert.Equal(fx.Vm.ReShadeProfile.CanInstallD3DCompilerWithProtontricks, text.Contains("Protontricks", StringComparison.Ordinal));
                Assert.Equal(fx.Vm.ReShadeProfile.CanInstallD3DCompilerWithWinetricks, text.Contains("Winetricks", StringComparison.Ordinal));
                break;
            case NeuralRenderingCheckDialogViewModel.NoControl:
                Assert.DoesNotContain(ControlWords, word => text.Contains(word, StringComparison.Ordinal));
                break;
            default:
                throw new Xunit.Sdk.XunitException($"unknown control {control}");
        }
        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    // ---- F1: the page gives Update All's answer for an owner's DLL changed outside RHI ----------------------------------

    // P1: the owner's NR DLL was replaced by hand (the record keeps 310.8.3 (50xx)'s hash and metadata).
    [Theory]
    [MemberData(nameof(Languages))]
    public async Task F1_OwnerDllChangedOutsideRhi_PageAgreesWithUpdateAll(string language)
    {
        var g = MakeGame("P1 Game", For50);
        FeederLog(g, "NGX CreateFeature(18): 0xBAD00001 FeatureNotSupported");
        using var fx = await StartAsync(g, language, seed: fx =>
        {
            NrGpuGames.SeedOwner(fx.Records, Listed(fx), ManagedComponentKind.Dlss5Feeder, g.Nr,
                new Dictionary<string, string> { [NeuralRenderingDllSwap.MetaDllVersion] = For50 });
            File.WriteAllBytes(g.Nr, Dll(ForAll)); // replaced by hand
        });

        var page = await OpenAsync(fx);
        var check = await fx.Managed.CheckManagedUpdateAsync(Listed(fx), ManagedComponentKind.Dlss5Feeder, []);

        Assert.True(check.CouldNotCheck);
        Assert.Contains(LinuxManagedComponentService.NrChangedOutsideMessage, check.Message);
        Assert.Equal(NrDllFit.ChangedOutsideRhi, page.Diagnosis.Dll.Fit);
        Assert.Null(page.Diagnosis.Dll.Version); // never the record's version for another file
        var pt = language == "pt-BR";
        var dll = page.Sections[1].Lines;
        Assert.Equal(pt ? "Alterada ou removida fora do RHI, então o RHI não conseguiu verificá-la" : "Changed or removed outside RHI, so RHI could not check it", dll[0].Text);
        Assert.True(dll[0].IsWarning);
        Assert.Equal(pt ? "Colocada por DLSS5 Feeder e depois alterada fora do RHI" : "Put in by DLSS5 Feeder, then changed outside RHI", dll[1].Text);
        Assert.Contains(pt ? "Atualizar tudo o deixa como está" : "Update All leaves it as it is", dll[0].Tip);
        Assert.DoesNotContain(dll, line => line.Text.StartsWith(Text("Nrc.Fix").Split('{')[0], StringComparison.Ordinal)); // no swap hint
        // The log's "DLL made for this card" hint names no control: RHI leaves the file as it is.
        Assert.Equal(NeuralRenderingCheckDialogViewModel.NoControl, NeuralRenderingCheckDialogViewModel.FixControl(NrFixHint.NrDllForCard, page.Diagnosis));
        Assert.Contains(page.Sections[4].Lines, line => line.Text == UiLocalizationService.Format("Nrc.Fix", Text("Nrc.FixChangedOutside")));
        AssertHintIsUsable(fx, page, NrFixHint.NrDllForCard);
    }

    [Fact]
    public async Task F1_OwnerDllRemovedOutsideRhi_IsChangedOutside_Too()
    {
        var g = MakeGame("Removed Game", For50);
        using var fx = await StartAsync(g, seed: fx =>
        {
            NrGpuGames.SeedOwner(fx.Records, Listed(fx), ManagedComponentKind.ShortFuse, g.Nr,
                new Dictionary<string, string> { [NeuralRenderingDllSwap.MetaDllVersion] = For50 });
            File.Delete(g.Nr);
        });

        var page = await OpenAsync(fx);

        Assert.Equal(NrDllFit.ChangedOutsideRhi, page.Diagnosis.Dll.Fit);
        Assert.Null(page.Diagnosis.Dll.Version);
    }

    [Fact]
    public async Task F1_TheRecordedFileUnchanged_KeepsTheRecordsVersionAndItsSwapHint()
    {
        var g = MakeGame("Same Game", For50);
        using var fx = await StartAsync(g, seed: fx =>
            NrGpuGames.SeedOwner(fx.Records, Listed(fx), ManagedComponentKind.Dlss5Feeder, g.Nr,
                new Dictionary<string, string> { [NeuralRenderingDllSwap.MetaDllVersion] = For50 }));

        var page = await OpenAsync(fx);

        Assert.Equal(NrDllFit.NotForThisCard, page.Diagnosis.Dll.Fit);
        Assert.Equal(For50, page.Diagnosis.Dll.Version);
        Assert.Equal("nr-dll-list", NeuralRenderingCheckDialogViewModel.FixControl(NrFixHint.NrDllForCard, page.Diagnosis));
        AssertHintIsUsable(fx, page, NrFixHint.NrDllForCard);
    }

    // Behind the NR Cost Scaler the owner's content is nvngx_dlssnr_real.dll (Update All's helper reads it there): the proxy
    // under the owner's name is not "changed outside RHI"; and while the scaler wraps the DLL, the owner's list and row are
    // held, so the hint names the scaler's row first.
    [Fact]
    public async Task F1_F2_BehindTheNrCostScaler_TheRealDllIsChecked_AndTheScalerRowComesFirst()
    {
        var g = MakeGame("Scaled Game", For50);
        using var fx = await StartAsync(g, seed: fx =>
        {
            NrGpuGames.SeedOwner(fx.Records, Listed(fx), ManagedComponentKind.Dlss5Feeder, g.Nr,
                new Dictionary<string, string> { [NeuralRenderingDllSwap.MetaDllVersion] = For50 });
            File.Move(g.Nr, Path.Combine(g.Bin, NrCostScalerService.RealDllName));
            File.WriteAllBytes(g.Nr, CacheForeignFileRejectionTests.Pe(0x8664, 8192, 9)); // the scaler's proxy
            Record(fx, ManagedComponentKind.NrCostScaler, g.Bin, [g.Nr], new Dictionary<string, string> { ["NrCostScaler.Owner"] = "DLSS5 Feeder" });
        });

        var page = await OpenAsync(fx);

        Assert.Equal(NrDllFit.NotForThisCard, page.Diagnosis.Dll.Fit);
        Assert.Equal("nr-cost-scaler-remove", NeuralRenderingCheckDialogViewModel.FixControl(NrFixHint.NrDllForCard, page.Diagnosis));
        AssertHintIsUsable(fx, page, NrFixHint.NrDllForCard);
        Assert.NotNull(NrCostScalerService.OwnerChangeRefusal(fx.Records, Listed(fx), ManagedComponentKind.Dlss5Feeder)); // the list would be refused
    }

    // ---- F2/F3: each hint names a control that works for THIS game, or a plain step -------------------------------------

    // P2: DX11 Bridge only, no NR DLL, 0xBAD00001 in the Feeder's log: the DLSS line is disabled (no NR DLL to swap).
    [Fact]
    public async Task F2_BridgeOnly_NoNrDll_NamesNoDisabledControl()
    {
        var g = MakeGame("P2 Game", nrVersion: null);
        FeederLog(g, "NGX CreateFeature(18): 0xBAD00001 FeatureNotSupported");
        using var fx = await StartAsync(g, seed: fx => Record(fx, ManagedComponentKind.Dlss5Dx11Bridge, g.Bin, []));

        var page = await OpenAsync(fx);

        Assert.False(fx.Vm.Dlss.CanSwapDlssNr);
        Assert.Equal(NeuralRenderingCheckDialogViewModel.NoControl, NeuralRenderingCheckDialogViewModel.FixControl(NrFixHint.NrDllForCard, page.Diagnosis));
        Assert.Contains(page.Sections[4].Lines, line => line.Text == UiLocalizationService.Format("Nrc.Fix", Text("Nrc.FixNoDll")));
        AssertHintIsUsable(fx, page, NrFixHint.NrDllForCard);
    }

    // P3: an older add-on-only DLSS5 Feeder whose record lists no NR DLL; the DLL is the game's own (F3: not "put in by").
    [Fact]
    public async Task F2_F3_LegacyFeeder_TheGamesOwnDll_NamesTheDlssLine_AndNoOwner()
    {
        var g = MakeGame("P3 Game", For50);
        using var fx = await StartAsync(g, seed: fx => Record(fx, ManagedComponentKind.Dlss5Feeder, g.Bin, [Path.Combine(g.Bin, "dlss5_feed.addon64")]));

        var page = await OpenAsync(fx);

        Assert.Null(page.Diagnosis.Dll.Owner);
        Assert.Null(page.Diagnosis.Dll.OwnerName);
        Assert.Equal(Text("Nrc.DllGameOwn"), page.Sections[1].Lines[1].Text);
        Assert.Equal("dlss-nr-line", NeuralRenderingCheckDialogViewModel.FixControl(NrFixHint.NrDllForCard, page.Diagnosis));
        AssertHintIsUsable(fx, page, NrFixHint.NrDllForCard);
    }

    // An older add-on-only Feeder that does list the NR DLL: the list only saves the choice, the Feeder takes it when added again.
    [Fact]
    public async Task F2_LegacyFeederOwningTheDll_NamesTheListThenItsRow()
    {
        var g = MakeGame("Legacy Owner", For50);
        using var fx = await StartAsync(g, seed: fx => Record(fx, ManagedComponentKind.Dlss5Feeder, g.Bin, [g.Nr, Payload(g, "dlss5-feed.addon64")]));

        var page = await OpenAsync(fx);

        Assert.Equal(ManagedComponentKind.Dlss5Feeder, page.Diagnosis.Dll.Owner);
        Assert.Equal("nr-dll-list+component-remove", NeuralRenderingCheckDialogViewModel.FixControl(NrFixHint.NrDllForCard, page.Diagnosis));
        AssertHintIsUsable(fx, page, NrFixHint.NrDllForCard);
    }

    // A DLSS5 Feeder Full stack whose record does not list the game's own NR DLL: the DLSS section is locked, the list swaps
    // the Feeder's DLL (none): no control can swap this file.
    [Fact]
    public async Task F2_FeederFullLocksTheDlssSection_TheGamesOwnDll_IsAPlainStep()
    {
        var g = MakeGame("Locked Game", For50);
        using var fx = await StartAsync(g, seed: fx => Record(fx, ManagedComponentKind.Dlss5Feeder, g.Bin, [Path.Combine(g.Bin, "dlss5_feed.addon64")],
            new Dictionary<string, string> { ["Feeder.FullStack"] = "1" }));

        var page = await OpenAsync(fx);

        Assert.Null(page.Diagnosis.Dll.Owner);
        Assert.False(fx.Vm.Dlss.CanSwapDlssNr);
        Assert.Equal(NeuralRenderingCheckDialogViewModel.NoControl, NeuralRenderingCheckDialogViewModel.FixControl(NrFixHint.NrDllForCard, page.Diagnosis));
        Assert.Equal(Text("Nrc.FixReplaceYourself"), NeuralRenderingCheckDialogViewModel.FixText(NrFixHint.NrDllForCard, page.Diagnosis));
        AssertHintIsUsable(fx, page, NrFixHint.NrDllForCard);
    }

    // OptiScaler DLSS NR owns the DLL: its row (shown as "OptiScaler", F7) — for the DLL hint and for a crash.
    [Theory]
    [MemberData(nameof(Languages))]
    public async Task F2_F7_OptiScalerOwner_NamesItsRowAsShown(string language)
    {
        var g = MakeGame("Opti Game", For50);
        FeederLog(g, "Unhandled exception 0xC0000005 at 0x00007FF6");
        using var fx = await StartAsync(g, language, seed: fx => Record(fx, ManagedComponentKind.OptiScaler, g.Bin,
            [g.Nr, Payload(g, "OptiScaler.dll", "MZ optiscaler/OptiScaler official package")],
            new Dictionary<string, string> { ["OptiScaler.Variant"] = nameof(OptiScalerVariant.DlssNr) }));

        var page = await OpenAsync(fx);

        Assert.Equal(ManagedComponentKind.OptiScaler, page.Diagnosis.Dll.Owner);
        Assert.Equal("component-remove", NeuralRenderingCheckDialogViewModel.FixControl(NrFixHint.NrDllForCard, page.Diagnosis));
        AssertHintIsUsable(fx, page, NrFixHint.NrDllForCard, ManagedComponentKind.OptiScaler);
        Assert.Equal("component-remove", NeuralRenderingCheckDialogViewModel.FixControl(NrFixHint.RemoveNrMethod, page.Diagnosis));
        AssertHintIsUsable(fx, page, NrFixHint.RemoveNrMethod, ManagedComponentKind.OptiScaler);
        var crash = NeuralRenderingCheckDialogViewModel.FixText(NrFixHint.RemoveNrMethod, page.Diagnosis);
        Assert.Equal(UiLocalizationService.Format("Nrc.FixRemove", "OptiScaler"), crash);
        Assert.DoesNotContain(page.Diagnosis.Dll.OwnerName!, crash); // "OptiScaler DLSS NR" is not a row
    }

    // The OptiScaler record is there but its files are gone: its row offers Install, not Remove — the hint presses nothing.
    [Fact]
    public async Task F2_AnOwnerRowWithoutRemove_IsNeverNamedAsTheThingToPress()
    {
        var g = MakeGame("Opti Gone", For50);
        using var fx = await StartAsync(g, seed: fx => Record(fx, ManagedComponentKind.OptiScaler, g.Bin, [g.Nr, Path.Combine(g.Bin, "OptiScaler.dll")],
            new Dictionary<string, string> { ["OptiScaler.Variant"] = nameof(OptiScalerVariant.DlssNr) }));

        var page = await OpenAsync(fx);

        Assert.False(fx.Vm.Components.Single(r => r.Kind == ManagedComponentKind.OptiScaler).IsRemoveAction);
        Assert.Equal(NeuralRenderingCheckDialogViewModel.NoControl, NeuralRenderingCheckDialogViewModel.FixControl(NrFixHint.NrDllForCard, page.Diagnosis));
        Assert.Equal(UiLocalizationService.Format("Nrc.FixNoRemove", "OptiScaler"), NeuralRenderingCheckDialogViewModel.FixText(NrFixHint.NrDllForCard, page.Diagnosis));
        AssertHintIsUsable(fx, page, NrFixHint.NrDllForCard);
    }

    // An older Feeder owning the DLL whose add-on file is gone: its row offers Install (enabled), never Remove — not named.
    [Fact]
    public async Task F2_AnOwnerRowOfferingInstall_IsNeverNamedAsTheThingToPress()
    {
        var g = MakeGame("Feeder Gone", For50);
        using var fx = await StartAsync(g, seed: fx => Record(fx, ManagedComponentKind.Dlss5Feeder, g.Bin, [g.Nr, Path.Combine(g.Bin, "dlss5-feed.addon64")]));

        var page = await OpenAsync(fx);

        var row = fx.Vm.Components.Single(r => r.Kind == ManagedComponentKind.Dlss5Feeder);
        Assert.True(row.IsInstallAction && row.IsActionEnabled);
        Assert.Equal(NeuralRenderingCheckDialogViewModel.NoControl, NeuralRenderingCheckDialogViewModel.FixControl(NrFixHint.NrDllForCard, page.Diagnosis));
        Assert.Equal(UiLocalizationService.Format("Nrc.FixNoRemove", "DLSS5 Feeder"), NeuralRenderingCheckDialogViewModel.FixText(NrFixHint.NrDllForCard, page.Diagnosis));
    }

    // The row rule the page reads: an enabled Remove only (an Install, or a locked Remove, is not one).
    [Theory]
    [InlineData(ComponentAvailability.Installed, true, true)]
    [InlineData(ComponentAvailability.UpdateAvailable, true, true)]
    [InlineData(ComponentAvailability.Installed, false, false)]
    [InlineData(ComponentAvailability.Available, true, false)]
    [InlineData(ComponentAvailability.Available, false, false)]
    public void F2_ARowOffersRemove_OnlyWithAnEnabledRemove(ComponentAvailability availability, bool allowed, bool offers)
    {
        var row = new ComponentRowViewModel(new ManagedComponent
        {
            Kind = ManagedComponentKind.Dlss5Feeder, DisplayName = "DLSS5 Feeder", Availability = availability, ActionAllowed = allowed
        }, new CommunityToolkit.Mvvm.Input.RelayCommand(() => { }));

        Assert.Equal(offers, row.OffersRemove);
        Assert.Equal(offers, row.IsRemoveAction && row.IsActionEnabled); // what the row's button shows
    }

    // Opened for a game without an analysis (another game selected; the card menu selects its card first, so in the app
    // this is an analysis that failed or ran past the page's wait): no row or DLSS line is named.
    [Fact]
    public async Task F2_WithoutAnAnalysis_NamesNoRowOrDlssLine()
    {
        var g = MakeGame("Never Looked", For50);
        var other = MakeGame("Other Game", nrVersion: null);
        UiLocalizationService.Apply("en-US");
        var gpu = Facts(RtxGeneration.Rtx40);
        using var fx = await ViewModelFixture.StartAsync([other.Detected, g.Detected], uiLanguage: "en-US",
            httpResponse: NeuralRenderingGpuViewModelTests.Answer, hdrProfiles: Hdr(gpu), nvidiaGpu: () => gpu);
        var never = fx.Vm.Games.Single(r => r.Game.Name == g.Detected.Name);
        var analysed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(AnalysisOutcome o) { if (o.Game.Name == other.Detected.Name) analysed.TrySetResult(); }
        fx.Vm.Session.Completed += OnCompleted;
        fx.Vm.SelectedGame = fx.Vm.Games.Single(r => r.Game.Name == other.Detected.Name).Game;
        await analysed.Task.WaitAsync(HangGuard);
        fx.Vm.Session.Completed -= OnCompleted;
        await fx.WhenSelectionProbeIdleAsync(HangGuard);
        fx.Vm.Session.Invalidate(never.Game);
        Assert.False(fx.Vm.Session.TryGetCached(never.Game, out _));
        Assert.NotSame(never.Game, fx.Vm.Session.Game);
        var shown = new TaskCompletionSource<NeuralRenderingCheckDialogViewModel>(TaskCreationOptions.RunContinuationsAsynchronously);
        fx.Vm.NrCheck.ShowDialogAsync = m => { shown.TrySetResult(m); return Task.CompletedTask; };

        await fx.Vm.NrCheck.OpenForAsync(never.Game).WaitAsync(HangGuard);
        var page = await shown.Task.WaitAsync(HangGuard);

        Assert.Same(never.Game, page.Game);
        Assert.Null(page.Diagnosis.Controls.RemovableRows);
        Assert.Equal(NeuralRenderingCheckDialogViewModel.NoControl, NeuralRenderingCheckDialogViewModel.FixControl(NrFixHint.NrDllForCard, page.Diagnosis));
        Assert.Equal(Text("Nrc.FixSelectFirst"), NeuralRenderingCheckDialogViewModel.FixText(NrFixHint.NrDllForCard, page.Diagnosis));
    }

    // A crash with only the DX11 Bridge in use: its row; with no method RHI set up: a plain step.
    [Fact]
    public async Task F2_Crash_NamesTheMethodsRow_OrAPlainStepWithoutOne()
    {
        var bridge = MakeGame("Bridge Crash", nrVersion: null);
        FeederLog(bridge, "stopped: the D3D12 device was removed");
        using (var fx = await StartAsync(bridge, seed: fx => Record(fx, ManagedComponentKind.Dlss5Dx11Bridge, bridge.Bin, [Payload(bridge, "dlss5-bridge.addon64")])))
        {
            var page = await OpenAsync(fx);
            Assert.Equal("component-remove", NeuralRenderingCheckDialogViewModel.FixControl(NrFixHint.RemoveNrMethod, page.Diagnosis));
            AssertHintIsUsable(fx, page, NrFixHint.RemoveNrMethod, ManagedComponentKind.Dlss5Dx11Bridge);
        }

        var own = MakeGame("Own Crash", ForAll);
        FeederLog(own, "stopped: the D3D12 device was removed");
        using (var fx = await StartAsync(own))
        {
            var page = await OpenAsync(fx);
            Assert.Equal(NeuralRenderingCheckDialogViewModel.NoControl, NeuralRenderingCheckDialogViewModel.FixControl(NrFixHint.RemoveNrMethod, page.Diagnosis));
            Assert.Equal(Text("Nrc.FixNoMethod"), NeuralRenderingCheckDialogViewModel.FixText(NrFixHint.RemoveNrMethod, page.Diagnosis));
            AssertHintIsUsable(fx, page, NrFixHint.RemoveNrMethod);
        }
    }

    // F2/F5: the D3DCompiler 47 hint names only the buttons shown for this game, and says what Browse does.
    [Theory]
    [InlineData(true, null, "Nrc.FixD3DCompilerWinetricks")]
    [InlineData(true, 1234560, "Nrc.FixD3DCompilerProtontricks")]
    [InlineData(false, null, "Nrc.FixD3DCompilerBrowse")]
    public async Task F2_F5_TheShaderCompilerHint_NamesOnlyTheButtonsShown(bool prefix, int? steamAppId, string expected)
    {
        var g = MakeGame("Compiler Game " + expected.Split('.')[1], ForAll, prefix, steamAppId);
        File.WriteAllText(Path.Combine(g.Bin, "ReShade.log"), "DLSS5_Feed.fx(88,5): error E5017: cannot map expression\n");
        using var fx = await StartAsync(g);

        var page = await OpenAsync(fx);

        Assert.Equal(Text(expected), NeuralRenderingCheckDialogViewModel.FixText(NrFixHint.NativeD3DCompiler, page.Diagnosis));
        AssertHintIsUsable(fx, page, NrFixHint.NativeD3DCompiler);
        Assert.Contains(page.Sections[4].Lines, line => line.Text == UiLocalizationService.Format("Nrc.Fix", Text(expected)));
    }

    [Fact]
    public void F5_TheBrowseWording_SaysWhenRhiUsesTheDll()
    {
        foreach (var key in new[] { "Nrc.FixD3DCompilerBoth", "Nrc.FixD3DCompilerProtontricks", "Nrc.FixD3DCompilerWinetricks", "Nrc.FixD3DCompilerBrowse" })
        {
            UiLocalizationService.Apply("en-US");
            Assert.Contains("next time", Text(key));
            Assert.Contains("sets up ReShade or the DLSS5 Feeder", Text(key));
            Assert.DoesNotContain("or pick Microsoft's DLL with Browse", Text(key));
            UiLocalizationService.Apply("pt-BR");
            Assert.Contains("próxima vez", Text(key));
            Assert.Contains("configurar o ReShade ou o DLSS5 Feeder", Text(key));
        }
        UiLocalizationService.Apply("en-US");
    }
}
