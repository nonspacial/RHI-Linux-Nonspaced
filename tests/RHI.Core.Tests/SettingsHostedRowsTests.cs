using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Detection.Xbox;

namespace RHI.Core.Tests;

/// <summary>
/// Rows other lanes put into Settings, hosted by vmsplit's <see cref="SettingsViewModel"/> at integration: scan's
/// "PartingX / Xbox folders" list (#90) and parity's Accounts &amp; updates presenters (#100, #163, #99).
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class SettingsHostedRowsTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-settings-rows-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task XboxFolders_IsHosted_SavesThroughTheStore_AndRescansTheDetectorsThatReadIt()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var folders = Assert.IsType<XboxFoldersPresenter>(fx.Vm.Settings.XboxFolders);
        var partingX = Directory.CreateDirectory(Path.Combine(_root, "partingx-state")).FullName;

        Assert.Null(folders.Add(partingX));

        Assert.Equal([partingX], folders.Folders);
        Assert.Equal([partingX], new XboxFolderStore(Path.Combine(fx.ConfigDir, "xbox-folders.json")).Folders());
        Assert.Equal(["Wine", XboxGameDetector.SourceName], SettingsViewModel.XboxFolderDetectors.Order(StringComparer.Ordinal));
        Assert.Null(folders.Remove(partingX));
        Assert.Empty(new XboxFolderStore(Path.Combine(fx.ConfigDir, "xbox-folders.json")).Folders());
    }

    [Fact]
    public async Task Parity_IsTheShellsOneObject()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        Assert.Null(fx.Vm.Settings.Parity);
        using var http = new HttpClient(new Offline());
        var wine = new RHI.Platform.Linux.Deployment.WineDllOverrideService();
        var services = RHI.Desktop.AppComposition.Create(http,
            new RHI.Platform.Linux.Deployment.LinuxDeploymentRecordStore(Path.Combine(_root, "records.json")), wine,
            RHI.Platform.Linux.Deployment.LinuxDeploymentRecovery.CreateJournal(wine, Path.Combine(_root, "journal")));

        fx.Vm.Parity = services.Parity;

        Assert.Same(services.Parity, fx.Vm.Settings.Parity);
        using var vm = services.CreateViewModel(); // the app's own graph hosts the Xbox folder list too
        await vm.Initialization.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.NotNull(vm.Settings.XboxFolders);
    }

    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("offline test");
    }
}
