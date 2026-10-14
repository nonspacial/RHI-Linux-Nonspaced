using System.Security.Cryptography;
using System.Text;
using RHI.Core.Models;
using RHI.Desktop.ViewModels;
using RHI.Desktop.Views;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// Merge review finding 1: the NR Cost Scaler row sits inside the Extras section since the #89 split, so its DataContext is
/// the Extras panel view model; it reaches the shell through <see cref="PanelViewModel.Shell"/>. Without that fallback the
/// row would never show there (the harness was the only check).
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class NrCostScalerRowTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-nr-row-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Row_UnderTheExtrasPanelsDataContext_ReadsTheShell_AndShowsForAGameWithAnOwner()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "Scaled")).FullName;
        var exe = Path.Combine(dir, "Scaled.exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        var game = new DetectedGame
        {
            Name = "Scaled", Source = "Wine", InstallPath = dir, ExecutablePath = exe, BinaryDirectory = dir, LaunchExecutablePath = exe,
            Runner = "Wine", LaunchCommand = "wine Scaled.exe"
        };
        using var fx = await ViewModelFixture.StartAsync([game]);
        using var http = new HttpClient(new OfflineHandler());
        var wine = new WineDllOverrideService();
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "app-records.json"));
        var services = RHI.Desktop.AppComposition.Create(http, records, wine, LinuxDeploymentRecovery.CreateJournal(wine, Path.Combine(_root, "journal")));
        fx.Vm.Parity = services.Parity;
        fx.Vm.SelectedGameRow = fx.Vm.Games.Single(row => row.Name == "Scaled");
        for (var i = 0; i < 200 && (fx.Vm.IsSelectionProbeBusy || fx.Vm.SelectedGame is null); i++) await Task.Delay(25);
        var selected = fx.Vm.SelectedGame!;
        var runtime = Encoding.UTF8.GetBytes("MZ owner runtime");
        var runtimePath = Path.Combine(dir, "nvngx_dlssnr.dll");
        File.WriteAllBytes(runtimePath, runtime);
        records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(selected), GameName = selected.Name, Component = ManagedComponentKind.ShortFuse,
            BinaryDirectory = dir, Version = "1.0",
            Files = [new LinuxDeployedFile { Path = runtimePath, Sha256 = Convert.ToHexString(SHA256.HashData(runtime)) }]
        });
        Assert.True(services.Parity!.NrCostScaler.StateFor(fx.Vm).Visible);

        var underExtras = new NrCostScalerRow { DataContext = fx.Vm.Extras };
        var underShell = new NrCostScalerRow { DataContext = fx.Vm };
        var underNothing = new NrCostScalerRow { DataContext = new object() };

        Assert.IsAssignableFrom<PanelViewModel>(fx.Vm.Extras);
        Assert.True(underExtras.IsVisible);
        Assert.True(underShell.IsVisible);
        Assert.False(underNothing.IsVisible);
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("offline test");
    }
}
