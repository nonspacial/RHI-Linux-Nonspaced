using System.Net;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Hdr;
using static RHI.Core.Tests.NrGpuTestKit;

namespace RHI.Core.Tests;

/// <summary>
/// #229 view-model rows: every NR list the shell shows follows the card (the NR DLL dropdown, the DLSS panel's NR swap
/// list, Settings → DLSS defaults, the DLSS batch); an unknown card lists everything and the dropdown's tooltip says
/// why; a hand-picked pin stays selected; Quick Apply's NR step for a DLL made for another card is skipped with a line.
/// EN + PT-BR. The GPU comes from the fixture's seam (never the real /proc).
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class NeuralRenderingGpuViewModelTests : IDisposable
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-nr-gpu-vm-" + Guid.NewGuid().ToString("N"));

    public NeuralRenderingGpuViewModelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        UiLocalizationService.Apply("en-US");
        try { Directory.Delete(_root, true); } catch { }
    }

    public static TheoryData<string> Languages => new() { "en-US", "pt-BR" };

    /// <summary>The offline fixture answering the live DLSS manifest and its NR zips.</summary>
    internal static HttpResponseMessage? Answer(HttpRequestMessage request)
    {
        var url = request.RequestUri!.AbsoluteUri;
        if (url == DlssManagerService.ManifestUrl) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ManifestJson()) };
        if (url == SrUrl) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip("nvngx_dlss.dll", SrDll)) };
        var version = LiveVersions.FirstOrDefault(v => UrlOf(v) == url);
        return version is null ? null : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip("nvngx_dlssnr.dll", Dll(version))) };
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task Rtx4090_EveryNrList_HidesThe50xxBuilds(string language)
    {
        UiLocalizationService.Apply(language);
        using var fx = await ViewModelFixture.StartAsync([], uiLanguage: language, httpResponse: Answer,
            hdrProfiles: Hdr(Facts(RtxGeneration.Rtx40)), nvidiaGpu: () => Facts(RtxGeneration.Rtx40));

        Assert.Equal([UiLocalizationService.Text("Ui.OptionLatest"), ForAll], fx.Vm.Extras.NeuralRenderingDllVersionOptions);
        Assert.Null(fx.Vm.Extras.NeuralRenderingDllVersionsTip);
        Assert.Equal([ForAll], fx.Vm.Dlss.DlssNrVersions);
        Assert.Equal(ForAll, fx.Vm.Dlss.SelectedDlssNrVersion);
        var settings = new SettingsDialogViewModel(fx.Vm, _ => null, _ => Task.FromResult<string?>(null));
        Assert.Equal([OptionLists.DoNotChangeDisplay(), ForAll], settings.DefaultDlssnr.Items);
        var batch = DlssBatchDialogViewModel.For(fx.Vm.Dlss, fx.Vm.Operations, []);
        Assert.Equal([OptionLists.DoNotChangeDisplay(), ForAll], batch.NeuralRendering.Items);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task UnknownCard_ListsEverything_AndTheDropdownSaysWhy(string language)
    {
        UiLocalizationService.Apply(language);
        using var fx = await ViewModelFixture.StartAsync([], uiLanguage: language, httpResponse: Answer,
            hdrProfiles: Hdr(NvidiaGpuFacts.None), nvidiaGpu: () => NvidiaGpuFacts.None);

        Assert.Equal([UiLocalizationService.Text("Ui.OptionLatest"), For50, ForAll, Old50], fx.Vm.Extras.NeuralRenderingDllVersionOptions);
        Assert.Equal([For50, ForAll, Old50], fx.Vm.Dlss.DlssNrVersions);
        Assert.Equal(language == "pt-BR"
                ? "O RHI não conseguiu identificar a sua placa de vídeo, então todas as versões estão listadas."
                : "RHI could not tell your graphics card, so every version is listed.",
            fx.Vm.Extras.NeuralRenderingDllVersionsTip);
    }

    [Fact]
    public async Task Rtx5080_ListsItsOwnBuildsFirst()
    {
        UiLocalizationService.Apply("en-US");
        using var fx = await ViewModelFixture.StartAsync([], httpResponse: Answer, hdrProfiles: Hdr(Facts(RtxGeneration.Rtx50)), nvidiaGpu: () => Facts(RtxGeneration.Rtx50));

        Assert.Equal([For50, ForAll, Old50], fx.Vm.Dlss.DlssNrVersions);
        Assert.Equal(For50, fx.Vm.Dlss.SelectedDlssNrVersion);
    }

    [Fact]
    public async Task AHandPickedPinForAnotherCard_StaysSelectedAndShown()
    {
        UiLocalizationService.Apply("en-US");
        var game = Game("Pinned Quest", "nvngx_dlssnr.dll");
        using var fx = await ViewModelFixture.StartAsync([game], httpResponse: Answer, hdrProfiles: Hdr(Facts(RtxGeneration.Rtx40)), nvidiaGpu: () => Facts(RtxGeneration.Rtx40));
        fx.Vm.SelectedGame = null; // the pin is read when the game is selected
        fx.Managed.SetNeuralRenderingDllVersionPin(game, For50);

        await SelectAsync(fx, game.Name);

        Assert.Equal(For50, fx.Vm.Extras.SelectedNeuralRenderingDllVersion);
        Assert.Contains(For50, fx.Vm.Extras.NeuralRenderingDllVersionOptions);
        Assert.Equal(For50, fx.Managed.GetNeuralRenderingDllVersionPin(game));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task QuickApply_ASavedNrDefaultForAnotherCard_IsSkippedWithALine_AndChangesNoFile(string language)
    {
        var game = Game("Default Quest", "nvngx_dlssnr.dll");
        var nr = Path.Combine(game.InstallPath, "nvngx_dlssnr.dll");
        File.WriteAllBytes(nr, Dll(Old50));
        UiLocalizationService.Apply(language);
        using var fx = await ViewModelFixture.StartAsync([game], uiLanguage: language, httpResponse: Answer,
            hdrProfiles: Hdr(Facts(RtxGeneration.Rtx40)), nvidiaGpu: () => Facts(RtxGeneration.Rtx40));
        await SelectAsync(fx, game.Name);
        var settings = fx.Vm.Settings.GetLinuxSettingsSnapshot().Settings;
        settings.DefaultDlssnrVersion = For50;
        fx.Vm.Settings.SaveLinuxSettings(settings);
        UiLocalizationService.Apply(language);
        fx.Vm.Dlss.NotifyDlssDefaults();

        await fx.Vm.Dlss.QuickApplyDlssDefaultsCommand.ExecuteAsync(null).WaitAsync(HangGuard);

        Assert.Contains(language == "pt-BR"
                ? $"O Neural Rendering {For50} não foi aplicado em Default Quest: essa DLL é feita para outra geração de placa de vídeo."
                : $"Neural Rendering {For50} was not applied to Default Quest: that DLL is made for another graphics card generation.",
            fx.Vm.StatusText, StringComparison.Ordinal);
        Assert.Equal(Dll(Old50), File.ReadAllBytes(nr));
        Assert.False(File.Exists(nr + ".original"));
    }

    private DetectedGame Game(string name, params string[] files)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var exe = Path.Combine(dir, name.Replace(" ", "") + ".exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        foreach (var file in files) File.WriteAllBytes(Path.Combine(dir, file), Dll(Old50));
        return new DetectedGame
        {
            Name = name, Source = "Manual", InstallPath = dir, ExecutablePath = exe, BinaryDirectory = dir, LaunchExecutablePath = exe,
            Runner = "Proton", CompatibilityTool = "GE-Proton10-25", CompatibilityLayerType = "proton", LaunchCommand = "umu-run Quest.exe"
        };
    }

    internal static async Task SelectAsync(ViewModelFixture fx, string name)
    {
        var analysed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(AnalysisOutcome outcome)
        {
            if (outcome.Game.Name == name) analysed.TrySetResult();
        }
        fx.Vm.Session.Completed += OnCompleted;
        try
        {
            fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Game.Name == name).Game;
            if (!(fx.Vm.Session.Game?.Name == name && fx.Vm.Session.Analysis is not null))
                await analysed.Task.WaitAsync(HangGuard);
        }
        finally { fx.Vm.Session.Completed -= OnCompleted; }
        await fx.WhenSelectionProbeIdleAsync(HangGuard);
    }
}
