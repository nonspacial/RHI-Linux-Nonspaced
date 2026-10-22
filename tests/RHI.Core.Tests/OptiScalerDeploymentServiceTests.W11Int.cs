using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;
using static RHI.Core.Tests.NrGpuTestKit;

namespace RHI.Core.Tests;

/// <summary>
/// Wave 11 integration, where OptiScaler's update transaction (Update All and the in-place Nightly switch) meets the
/// other lanes: a game held after a failed undo gets the hold's restart text, a stalled build download reads as a stall
/// and changes nothing, and the Nightly switch never touches a Neural Rendering DLL (a Nightly owns none).
/// </summary>
public sealed partial class OptiScalerDeploymentServiceTests
{
    // Integration item 1 (the toolkit rule since w10 D15): a game held because the undo of its own earlier OptiScaler
    // change could not finish (RHI's half-undone dxgi.dll sits in a game folder that turned read-only) gets the restart
    // text from Update All and from the Nightly switch — not "modified outside RHI", which took RHI's own half-undone file
    // for the user's change. Nothing is downloaded and nothing changes.
    [PermissionDenialTheory]
    [InlineData("en-US", false)]
    [InlineData("pt-BR", false)]
    [InlineData("en-US", true)]
    [InlineData("pt-BR", true)]
    public async Task W11Int_OnAGameHeldByItsOwnOptiScalerUndo_UpdateAndTheNightlySwitch_GetTheRestartText(string language, bool viaSwitch)
    {
        var f = NewSwitch($"w11int-held-{language}-{viaSwitch}", journaled: true);
        await InstallThenChooseAsync(f, viaSwitch ? Installed : null, viaSwitch ? Chosen : null);
        var bin = f.Game.BinaryDirectory!;
        var dxgi = Path.Combine(bin, "dxgi.dll");
        var record = f.Store.Find(f.Game, ManagedComponentKind.OptiScaler)!;
        var halfUndone = File.ReadAllBytes(dxgi);
        halfUndone[^1] ^= 0x5A;

        var (opened, refusal) = await DeploymentScope.BeginAsync(f.Journal!.Journal, "earlier OptiScaler update", f.Game,
            ManagedComponentKind.OptiScaler, CancellationToken.None);
        Assert.Null(refusal);
        await using (var scope = opened!)
        {
            await scope.PlaceBytesAsync(dxgi, halfUndone);
            f.Store.SaveFaultForTesting = () =>
            {
                PermissionDenial.MakeReadOnly(bin);
                return new IOException("Simulated write failure: records.json");
            };
            try
            {
                Assert.NotNull(Record.Exception(() => f.Store.Upsert(scope.Stamp(f.Store.Find(f.Game, ManagedComponentKind.OptiScaler)!))));
            }
            finally
            {
                f.Store.SaveFaultForTesting = null;
            }
            Assert.False((await scope.RollbackAsync()).Success); // held: RHI's dxgi.dll cannot be put back
        }
        try
        {
            // What the "modified outside RHI" check would see.
            Assert.False(FileHash.HexEquals(FileHash.Sha256Hex(dxgi), ManagedFileSwap.FindFile(record, "dxgi.dll")!.Sha256));
            var recordJson = JsonSerializer.Serialize(f.Store.Find(f.Game, ManagedComponentKind.OptiScaler));

            var result = viaSwitch
                ? await f.Service.SwitchNightlyBuildAsync(f.Game, Chosen, f.Choices)
                : await f.Service.UpdateAsync(f.Game);

            Assert.False(result.Success);
            Assert.Equal((viaSwitch ? OptiScalerDeploymentService.NightlySwitchFailedPrefix : "") + DeploymentJournal.EarlierUndoFailedMessage,
                result.Message);
            var line = UiLocalizationService.PresentFor(language, result.Message);
            Assert.Contains(language == "pt-BR" ? "Reinicie o RHI para concluir" : "Restart RHI to finish it", line, StringComparison.Ordinal);
            Assert.DoesNotContain("modified outside RHI", line, StringComparison.Ordinal);
            Assert.DoesNotContain(UiLocalizationService.UnlocalizedFallbackFor(language), line, StringComparison.Ordinal);
            if (language == "pt-BR")
            {
                Assert.DoesNotContain("was not switched", line, StringComparison.Ordinal);
                Assert.DoesNotContain("could not finish undoing", line, StringComparison.Ordinal);
            }
            Assert.Empty(f.Provider.Calls);
            Assert.Equal(halfUndone, File.ReadAllBytes(dxgi));
            Assert.Equal(recordJson, JsonSerializer.Serialize(f.Store.Find(f.Game, ManagedComponentKind.OptiScaler)));
            if (viaSwitch) Assert.Equal(Installed, Choice(f)); // the choice is back on the installed build
        }
        finally
        {
            PermissionDenial.MakeWritable(bin);
        }
    }

    // Integration item 4 (download-idle × nightly-switch): the chosen build's download stops sending data. The switch
    // gives up at the idle limit with the stall line (never a Cancel), and the files, the record and the saved choice
    // stay on the installed build. The real package provider and TrustedDownload, as in the app.
    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    public async Task W11Int_NightlySwitch_WhoseBuildDownloadStalls_SaysSo_AndLeavesFilesRecordAndChoice(string language)
    {
        TrustedDownload.IdleLimitForTesting = TimeSpan.FromMilliseconds(300);
        var name = "w11int-nsw-stall-" + language;
        var (game, store) = CreateGame(name, GraphicsApi.DirectX12);
        var choices = new NeuralAddonVersionPreferenceStore(Path.Combine(_root, name, "cfg", "pins.json"));
        var stalledAsset = $"https://github.com/rhi-tests/nightly-{Chosen}.7z";
        var http = new StalledHttp.Handler(request => request.RequestUri!.AbsoluteUri == stalledAsset ? StalledHttp.Stalled() : null);
        var inner = new NightlyHttp();
        using var client = new HttpClient(new RouteFirst(http, inner));
        var provider = new OptiScalerPackageProvider(client, new NightlyExtractor(), Path.Combine(_root, name, "cache"));
        var service = new OptiScalerDeploymentService(new TransactionalFileDeploymentService(), store, new WineDllOverrideService(), provider);
        choices.Set(game, ManagedComponentKind.OptiScaler, Installed);
        var install = await service.InstallAsync(game, OptiScalerVariant.Nightly, Installed);
        Assert.True(install.Success, install.Message);
        choices.Set(game, ManagedComponentKind.OptiScaler, Chosen);
        var watched = new[] { game.InstallPath, game.PrefixPath! };
        var before = CancelAtWritePhase.Snapshot(watched);
        var recordJson = JsonSerializer.Serialize(store.Find(game, ManagedComponentKind.OptiScaler));

        var result = await service.SwitchNightlyBuildAsync(game, Chosen, choices).WaitAsync(StalledHttp.HangGuard);

        Assert.False(result.Success);
        Assert.StartsWith(OptiScalerDeploymentService.NightlySwitchFailedPrefix + OptiScalerDeploymentService.NightlyBuildUnavailablePrefix
            + DownloadStalledException.MessagePrefix, result.Message, StringComparison.Ordinal);
        Assert.Contains(stalledAsset, http.Requests);
        CancelAtWritePhase.AssertUnchanged(before, watched);
        Assert.Equal(recordJson, JsonSerializer.Serialize(store.Find(game, ManagedComponentKind.OptiScaler)));
        Assert.Equal(Installed, choices.Get(game, ManagedComponentKind.OptiScaler));
        var line = UiLocalizationService.PresentFor(language, result.Message);
        Assert.DoesNotContain(UiLocalizationService.UnlocalizedFallbackFor(language), line, StringComparison.Ordinal);
        Assert.DoesNotContain(language == "pt-BR" ? "cancelad" : "cancel", line, StringComparison.OrdinalIgnoreCase);
        if (language == "pt-BR")
        {
            Assert.DoesNotContain("The download stopped", line, StringComparison.Ordinal);
            Assert.DoesNotContain("could not be downloaded", line, StringComparison.Ordinal);
        }
    }

    // Integration item 4 (nr-gpu × nightly-switch): an installed Nightly owns no Neural Rendering DLL, so the switch and
    // Update All never stage one and never touch one another component owns in the same folder — even with the DLSS
    // service wired and an RTX 40 card whose "Latest" NR DLL differs from the one on disk.
    [Fact]
    public async Task W11Int_NightlySwitch_OwnsNoNeuralRenderingDll_AndLeavesTheOneAnotherComponentOwns()
    {
        var name = "w11int-nsw-nr";
        var (game, store) = CreateGame(name, GraphicsApi.DirectX12);
        var latest = CreatePackage(name + "-latest") with { Version = "20261013" };
        var provider = new NightlyProvider(latest, CreatePackage(name + "-pinned"));
        var server = new Server();
        using var http = new HttpClient(server);
        var dlss = NrGpuTestKit.Dlss(http, Path.Combine(_root, name, "cache-dlss"), RtxGeneration.Rtx40);
        var service = new OptiScalerDeploymentService(new TransactionalFileDeploymentService(), store, new WineDllOverrideService(), provider, dlss: dlss);
        var choices = new NeuralAddonVersionPreferenceStore(Path.Combine(_root, name, "cfg", "pins.json"));
        choices.Set(game, ManagedComponentKind.OptiScaler, Installed);
        Assert.True((await service.InstallAsync(game, OptiScalerVariant.Nightly, Installed)).Success);
        choices.Set(game, ManagedComponentKind.OptiScaler, Chosen);
        // Another component's NR DLL beside it (made for RTX 50, so a "Latest" move would replace it on this card).
        var nr = Path.Combine(game.BinaryDirectory!, "nvngx_dlssnr.dll");
        File.WriteAllBytes(nr, Dll(For50));
        Assert.False(NeuralRenderingRuntimeOwnership.OptiScalerVariantOwnsRuntime(OptiScalerVariant.Nightly));
        Assert.False(NeuralRenderingRuntimeOwnership.ListsRuntime(store.Find(game, ManagedComponentKind.OptiScaler)!));
        server.Requests.Clear();

        var switched = await service.SwitchNightlyBuildAsync(game, Chosen, choices);
        var record = store.Find(game, ManagedComponentKind.OptiScaler)!;
        var check = await service.CheckUpdateAsync(game);
        var update = await service.UpdateAsync(game);

        Assert.True(switched.Success, switched.Message);
        Assert.Equal(Chosen, OptiScalerDeploymentService.GetPinnedNightlyBuild(record));
        Assert.False(NeuralRenderingRuntimeOwnership.ListsRuntime(record));
        Assert.Null(ManagedFileSwap.FindFile(record, "nvngx_dlssnr.dll"));
        Assert.False(record.Metadata.ContainsKey(OptiScalerDeploymentService.DlssNrRuntimeVersionMetadataKey));
        Assert.False(check.UpdateAvailable, check.Message);
        Assert.StartsWith(OptiScalerDeploymentService.NightlyPinnedNotUpdatedPrefix, update.Message, StringComparison.Ordinal);
        Assert.Equal(Dll(For50), File.ReadAllBytes(nr));
        Assert.DoesNotContain(server.Requests, url => LiveVersions.Any(v => url == UrlOf(v)));
    }

    // Answers from the first handler, or from the second when the first has no answer (offline).
    private sealed class RouteFirst(StalledHttp.Handler first, HttpMessageHandler second) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _second = new(second);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                return await new HttpMessageInvoker(first, disposeHandler: false).SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex) when (ex.Message == "offline test")
            {
                return await _second.SendAsync(request, cancellationToken);
            }
        }
    }
}
