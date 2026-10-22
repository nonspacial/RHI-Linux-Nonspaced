using System.Collections.Concurrent;
using System.Net;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Hdr;
using static RHI.Core.Tests.NrGpuTestKit;

namespace RHI.Core.Tests;

/// <summary>
/// #231 (owner decision 2026-10-22: warn and confirm, never refuse). Linux threshold 615.71.09 = the first Linux R615 driver,
/// the Linux equivalent of F5's Windows 616.56 (reviews/nvidia-driver-numbering.md). Second warning: a 310.8.0.x NR DLL
/// on an R615+ driver. The driver comes from fake /proc trees and the fixture's NVIDIA seam, never the real one.
/// </summary>
public sealed class OptiScalerF5DriverPolicyTests
{
    [Theory]
    [InlineData("615.71.09", false)] // the first Linux R615 driver
    [InlineData("615.78.08", false)] // the owner's
    [InlineData("620.10.01", false)]
    [InlineData("615.71.08", true)]
    [InlineData("610.57.04", true)]  // R610: its NGX core has no DLSSNR
    [InlineData("550.54.14", true)]
    public void Threshold_IsTheLinuxR615Driver_ComparedNumberByNumber(string driver, bool warns)
    {
        var advice = OptiScalerF5DriverPolicy.Advise(driver, null);

        Assert.Equal(warns, advice.DriverBelowMinimum);
        Assert.Equal(warns, advice.AnyWarning);
        Assert.False(advice.DriverUnknown);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a version")]
    public void NoDriver_OrUnreadable_Warns(string? driver)
    {
        var advice = OptiScalerF5DriverPolicy.Advise(driver, null);

        Assert.True(advice is { DriverUnknown: true, DriverBelowMinimum: false, AnyWarning: true });
    }

    [Fact]
    public void TheWindowsNumber_IsNamedButNeverTheLinuxGate()
    {
        Assert.Equal(("615.71.09", "616.56"), (OptiScalerF5DriverPolicy.MinimumLinuxDriver, OptiScalerF5DriverPolicy.WindowsMinimum));
        Assert.True(OptiScalerF5DriverPolicy.MeetsMinimum("615.78.08")); // below 616.56 numerically, still R615
    }

    [Theory]
    [InlineData("310,8,0,0", "615.78.08", true, false)]
    [InlineData("310.8.0 (50xx)", "615.71.09", true, false)]
    [InlineData("310,8,2,0", "615.78.08", false, false)]
    [InlineData("310,8,3,0", "615.78.08", false, false)]
    [InlineData("310,8,0,0", "610.57.04", false, true)] // only the driver warning below R615
    [InlineData("310,8,0,0", null, false, true)]          // no driver: only the driver warning
    public void NrDll310_8_0_OnAnR615Driver_IsTheSecondWarning(string nr, string? driver, bool nrFaults, bool driverWarns)
    {
        var advice = OptiScalerF5DriverPolicy.Advise(driver, nr);

        Assert.Equal((nrFaults, driverWarns), (advice.NrDllFaults, advice.DriverWarning));
    }

    [Fact]
    public void TheProbesDriverVersion_FeedsThePolicy_OnTheOwnersOpenModuleLine()
    {
        var root = Path.Combine(Path.GetTempPath(), "rhi-f5-proc-" + Guid.NewGuid().ToString("N"));
        try
        {
            var facts = NvidiaGpuProbe.Read(ProcTree(root, OpenModuleVersion, "NVIDIA GeForce RTX 4090"), Path.Combine(root, "sys"));

            Assert.False(OptiScalerF5DriverPolicy.Advise(facts.DriverVersion, null).AnyWarning);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}

/// <summary>#231 view-model rows (EN + PT-BR): the F5 confirm on install, the Update All note, the 310.8.0 row note.</summary>
[Collection(UiLanguageCollection.Name)]
public sealed class F5DriverConfirmViewModelTests : IDisposable
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-f5-vm-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<string> _requests = new();

    public F5DriverConfirmViewModelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        UiLocalizationService.Apply("en-US");
        try { Directory.Delete(_root, true); } catch { }
    }

    public static TheoryData<string> Languages => new() { "en-US", "pt-BR" };

    private HttpResponseMessage? Answer(HttpRequestMessage request)
    {
        _requests.Enqueue(request.RequestUri!.AbsoluteUri);
        return NeuralRenderingGpuViewModelTests.Answer(request);
    }

    private DetectedGame Game(string name)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var prefix = Directory.CreateDirectory(Path.Combine(_root, name + "-pfx")).FullName;
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n");
        var exe = Path.Combine(dir, name.Replace(" ", "") + ".exe");
        File.WriteAllBytes(exe, CacheForeignFileRejectionTests.Pe(0x8664, 4096, 7));
        return new DetectedGame
        {
            Name = name, Source = "Manual", Runner = "Manual Windows", InstallPath = dir, ExecutablePath = exe, BinaryDirectory = dir,
            PrefixPath = prefix, GraphicsApi = GraphicsApi.DirectX12
        };
    }

    private async Task<ViewModelFixture> StartF5Async(DetectedGame game, string? driver, string language = "en-US")
    {
        UiLocalizationService.Apply(language);
        var facts = Facts(RtxGeneration.Rtx40, driver);
        var fx = await ViewModelFixture.StartAsync([game], uiLanguage: language, httpResponse: Answer, hdrProfiles: Hdr(facts), nvidiaGpu: () => facts);
        await NeuralRenderingGpuViewModelTests.SelectAsync(fx, game.Name);
        Assert.Contains(fx.Vm.Session.Analysis!.Components, c => c.Kind == ManagedComponentKind.OptiScaler && c.ActionAllowed);
        fx.Vm.Runtime.SelectedOptiScalerVariant = fx.Vm.Runtime.OptiScalerVariantOptions.For(OptiScalerVariant.F5);
        UiLocalizationService.Apply(language);
        return fx;
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task BelowTheLinuxThreshold_AsksFirst_NamingBothNumbers_AndDeclinedChangesNothing(string language)
    {
        var game = Game("F5 Quest");
        using var fx = await StartF5Async(game, "610.57.04", language);
        (string Title, string Text, string Accept)? asked = null;
        fx.Vm.ConfirmAsync = (title, text, accept) => { asked = (title, text, accept); return Task.FromResult(false); };
        _requests.Clear();

        await fx.Vm.ManageComponentCommand.ExecuteAsync(ManagedComponentKind.OptiScaler).WaitAsync(HangGuard);

        Assert.NotNull(asked);
        Assert.Equal(language == "pt-BR"
                ? "O F5 DLSSNR precisa do driver NVIDIA 615.71.09 ou mais novo no Linux (616.56 no Windows). Este computador tem o 610.57.04.\n\nInstalar mesmo assim?"
                : "F5 DLSSNR needs NVIDIA driver 615.71.09 or newer on Linux (616.56 on Windows). This computer has 610.57.04.\n\nInstall anyway?",
            asked.Value.Text);
        Assert.Equal(language == "pt-BR" ? "Instalar mesmo assim" : "Install anyway", asked.Value.Accept);
        Assert.Equal(language == "pt-BR" ? "Instalação de OptiScaler F5 DLSSNR cancelada." : "OptiScaler F5 DLSSNR installation cancelled.", fx.Vm.StatusText);
        Assert.DoesNotContain(_requests, url => url.Contains("janblade", StringComparison.Ordinal)); // nothing was fetched or installed
        Assert.Null(fx.Records.Find(game, ManagedComponentKind.OptiScaler));
        Assert.Equal([game.ExecutablePath!], Directory.GetFiles(game.InstallPath));
    }

    [Fact]
    public async Task NoNvidiaDriver_AsksToo_AndAcceptedGoesOnToTheInstall()
    {
        UiLocalizationService.Apply("en-US");
        var game = Game("Unloaded Quest");
        using var fx = await StartF5Async(game, driver: null);
        var asked = 0;
        string? text = null;
        fx.Vm.ConfirmAsync = (_, body, _) => { asked++; text = body; return Task.FromResult(true); };
        _requests.Clear();

        await fx.Vm.ManageComponentCommand.ExecuteAsync(ManagedComponentKind.OptiScaler).WaitAsync(HangGuard);

        Assert.Equal(1, asked);
        Assert.StartsWith("F5 DLSSNR needs NVIDIA driver 615.71.09 or newer on Linux (616.56 on Windows). RHI found no NVIDIA driver loaded", text, StringComparison.Ordinal);
        // Accepted: the install ran (offline here, so its package fetch is what failed — never a refusal by the check).
        Assert.Contains(_requests, url => url == OptiScalerPackageProvider.F5ReleasesApi);
        Assert.NotEqual("OptiScaler F5 DLSSNR installation cancelled.", fx.Vm.StatusText);
    }

    [Theory]
    [InlineData("615.71.09")]
    [InlineData("615.78.08")]
    public async Task AtOrAboveTheThreshold_NoDialog(string driver)
    {
        UiLocalizationService.Apply("en-US");
        var game = Game("Fine Quest " + driver);
        using var fx = await StartF5Async(game, driver);
        var asked = 0;
        fx.Vm.ConfirmAsync = (_, _, _) => { asked++; return Task.FromResult(false); };
        _requests.Clear();

        await fx.Vm.ManageComponentCommand.ExecuteAsync(ManagedComponentKind.OptiScaler).WaitAsync(HangGuard);

        Assert.Equal(0, asked);
        Assert.Contains(_requests, url => url == OptiScalerPackageProvider.F5ReleasesApi);
    }

    [Fact]
    public async Task OtherChannels_NeverAsk()
    {
        UiLocalizationService.Apply("en-US");
        var game = Game("Stable Quest");
        using var fx = await StartF5Async(game, "610.57.04");
        fx.Vm.Runtime.SelectedOptiScalerVariant = fx.Vm.Runtime.OptiScalerVariantOptions.For(OptiScalerVariant.DlssNr);
        var asked = 0;
        fx.Vm.ConfirmAsync = (_, _, _) => { asked++; return Task.FromResult(false); };

        await fx.Vm.ManageComponentCommand.ExecuteAsync(ManagedComponentKind.OptiScaler).WaitAsync(HangGuard);

        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task A310_8_0NrDllOnAnR615Driver_AsksWithTheSecondWarning_Only()
    {
        UiLocalizationService.Apply("en-US");
        var game = Game("Old Model Quest");
        // An RTX 50 whose manifest's newest NR entry is the 310.8.0 build: F5 would stage it on a 615.78.08 driver.
        HttpResponseMessage? OldOnly(HttpRequestMessage request) => request.RequestUri!.AbsoluteUri == DlssManagerService.ManifestUrl
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ManifestJson([Old50])) }
            : Answer(request);
        var facts = Facts(RtxGeneration.Rtx50, "615.78.08");
        using var fx = await ViewModelFixture.StartAsync([game], httpResponse: OldOnly, hdrProfiles: Hdr(facts), nvidiaGpu: () => facts);
        await NeuralRenderingGpuViewModelTests.SelectAsync(fx, game.Name);
        fx.Vm.Runtime.SelectedOptiScalerVariant = fx.Vm.Runtime.OptiScalerVariantOptions.For(OptiScalerVariant.F5);
        string? text = null;
        fx.Vm.ConfirmAsync = (_, body, _) => { text = body; return Task.FromResult(false); };

        await fx.Vm.ManageComponentCommand.ExecuteAsync(ManagedComponentKind.OptiScaler).WaitAsync(HangGuard);

        Assert.Equal("The Neural Rendering DLL F5 will load is 310.8.0 (50xx), which crashes on NVIDIA drivers 615 and newer; 310.8.2 or newer is needed.\n\nInstall anyway?", text);
        Assert.Null(fx.Records.Find(game, ManagedComponentKind.OptiScaler));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task UpdateAll_NeverAsks_AndItsLineCarriesTheDriverNote(string language)
    {
        var game = Game("Updated F5 Quest");
        UiLocalizationService.Apply(language);
        var facts = Facts(RtxGeneration.Rtx40, "610.57.04");
        using var fx = await ViewModelFixture.StartAsync([game], uiLanguage: language, httpResponse: Answer, hdrProfiles: Hdr(facts), nvidiaGpu: () => facts);
        var listed = fx.Vm.Games.Single(row => row.Name == game.Name).Game;
        var record = new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(listed), GameName = listed.Name, Component = ManagedComponentKind.OptiScaler,
            BinaryDirectory = listed.BinaryDirectory!, Version = "0.1.30", SourceUrl = "https://github.com/rhi-tests/f5.zip"
        };
        record.Metadata[OptiScalerDeploymentService.VariantMetadataKey] = "F5";
        fx.Records.Upsert(record);
        Assert.True(fx.Managed.HasOptiScalerF5(listed));
        fx.Vm.Updates.SetLibraryHasManagedTargets(true);
        var asked = 0;
        fx.Vm.ConfirmAsync = (_, _, _) => { asked++; return Task.FromResult(false); };
        UiLocalizationService.Apply(language);

        await fx.Vm.Updates.UpdateAllCommand.ExecuteAsync(null).WaitAsync(HangGuard);

        Assert.Equal(0, asked);
        Assert.Contains(language == "pt-BR"
                ? "OptiScaler F5 DLSSNR em Updated F5 Quest: o driver NVIDIA 610.57.04 é mais antigo que o 615.71.09 que o F5 precisa no Linux (616.56 no Windows)."
                : "OptiScaler F5 DLSSNR in Updated F5 Quest: NVIDIA driver 610.57.04 is older than the 615.71.09 F5 needs on Linux (616.56 on Windows).",
            fx.Vm.StatusText, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task A310_8_0NrDllInstalledOnAnR615Driver_ItsRowSaysSo(string language)
    {
        var game = Game("Faulting Quest");
        UiLocalizationService.Apply(language);
        var facts = Facts(RtxGeneration.Rtx50, "615.78.08");
        using var fx = await ViewModelFixture.StartAsync([game], uiLanguage: language, httpResponse: Answer, hdrProfiles: Hdr(facts), nvidiaGpu: () => facts);
        var nr = Path.Combine(game.BinaryDirectory!, "nvngx_dlssnr.dll");
        File.WriteAllBytes(nr, Dll(Old50));
        NrGpuGames.SeedOwner(fx.Records, game, ManagedComponentKind.ShortFuse, nr, new Dictionary<string, string> { [NeuralRenderingDllSwap.MetaDllVersion] = Old50 });
        var component = new ManagedComponent { Kind = ManagedComponentKind.ShortFuse, DisplayName = "ShortFuse", Availability = ComponentAvailability.Installed };
        UiLocalizationService.Apply(language);

        fx.Managed.Decorate(game, [component]);
        var row = new ComponentRowViewModel(component, new CommunityToolkit.Mvvm.Input.RelayCommand(() => { }));

        Assert.EndsWith(language == "pt-BR"
                ? "Esta DLL do Neural Rendering trava nos drivers NVIDIA 615 e mais novos; é preciso a 310.8.2 ou mais nova. Instalada: " + Old50
                : "This Neural Rendering DLL crashes on NVIDIA drivers 615 and newer; 310.8.2 or newer is needed. Installed: " + Old50,
            row.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A310_8_2NrDll_OrADriverBeforeR615_HasNoCrashNote()
    {
        UiLocalizationService.Apply("en-US");
        foreach (var (version, driver) in new[] { (ForAll, "615.78.08"), (Old50, "610.57.04") })
        {
            var game = Game("Calm Quest " + driver);
            var facts = Facts(RtxGeneration.Rtx50, driver);
            using var fx = await ViewModelFixture.StartAsync([game], httpResponse: Answer, hdrProfiles: Hdr(facts), nvidiaGpu: () => facts);
            var nr = Path.Combine(game.BinaryDirectory!, "nvngx_dlssnr.dll");
            File.WriteAllBytes(nr, Dll(version));
            NrGpuGames.SeedOwner(fx.Records, game, ManagedComponentKind.Dlss5Feeder, nr, new Dictionary<string, string> { [NeuralRenderingDllSwap.MetaDllVersion] = version });
            var component = new ManagedComponent { Kind = ManagedComponentKind.Dlss5Feeder, DisplayName = "DLSS5 Feeder", Availability = ComponentAvailability.Installed };

            fx.Managed.Decorate(game, [component]);

            Assert.DoesNotContain(OptiScalerF5DriverPolicy.FaultingNrDllNotePrefix, component.Note ?? "", StringComparison.Ordinal);
        }
    }
}
