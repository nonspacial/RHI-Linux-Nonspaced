using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>#159: the RE Framework ⚙ presenter through the real view model (busy gate, status line, PT-BR).</summary>
[Collection(UiLanguageCollection.Name)]
public sealed class ReFrameworkStoragePresenterTests
{
    private static DetectedGame Game(string root) => new()
    {
        Name = "Resident Example", Source = "Wine", InstallPath = root, BinaryDirectory = root,
        ExecutablePath = Path.Combine(root, "re.exe"), EngineHint = "RE Engine", LaunchCommand = "wine re.exe"
    };

    private static async Task SelectAsync(ViewModelFixture fx)
    {
        fx.Vm.SelectedGame = fx.Vm.Games.Single().Game;
        for (var i = 0; i < 400 && fx.Vm.IsSelectionProbeBusy; i++) await Task.Delay(25);
        Assert.False(fx.Vm.IsSelectionProbeBusy);
    }

    [Fact]
    public async Task Delete_RemovesTheFolder_AndTheStatusLineSaysWhere()
    {
        var root = Directory.CreateTempSubdirectory("rhi-ref-storage-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "re.exe"), "MZ");
            var storage = Path.Combine(root, "_storage_");
            Directory.CreateDirectory(storage);
            File.WriteAllText(Path.Combine(storage, "cache.bin"), "x");
            using var fx = await ViewModelFixture.StartAsync([Game(root)]);
            await SelectAsync(fx);
            var presenter = fx.Vm.ReFrameworkStorage;
            Assert.True(presenter.CanDelete);
            Assert.Equal(storage, presenter.FolderPath);
            Assert.Equal(UiLocalizationService.Text("Journal.RefStorageFound"), presenter.StatusLine);

            var result = await presenter.DeleteAsync();

            Assert.True(result.Success, result.Message);
            Assert.False(Directory.Exists(storage));
            Assert.Contains(REFrameworkDeploymentService.StorageDeletedPrefix + storage, fx.Vm.StatusText, StringComparison.Ordinal);
            Assert.False(presenter.CanDelete);
            Assert.Equal(UiLocalizationService.Text("Journal.RefStorageNotFound"), presenter.StatusLine);
            Assert.False(fx.Vm.IsBusy);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Delete_WhileAnotherOperationRuns_IsRefusedAndTouchesNothing()
    {
        var root = Directory.CreateTempSubdirectory("rhi-ref-storage-busy-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "re.exe"), "MZ");
            var storage = Path.Combine(root, "_storage_");
            Directory.CreateDirectory(storage);
            using var fx = await ViewModelFixture.StartAsync([Game(root)]);
            await SelectAsync(fx);
            using var held = fx.Vm.Operations.TryBegin("test");
            Assert.NotNull(held);

            var result = await fx.Vm.ReFrameworkStorage.DeleteAsync();

            Assert.False(result.Success);
            Assert.True(Directory.Exists(storage));
            Assert.Contains(UiLocalizationService.Text("Vm.AnotherRHIOperationAlreadyRunning"), fx.Vm.StatusText, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    // Review w3-journal #5: an unreadable sub-folder comes back as a result line (shown in the dialog and the status
    // bar), never as an exception the dialog's handler only logs.
    [Fact]
    public async Task Delete_AnUnreadableSubFolder_ComesBackAsAStatusLine()
    {
        var root = Directory.CreateTempSubdirectory("rhi-ref-storage-locked-").FullName;
        var locked = Path.Combine(root, "_storage_", "locked");
        try
        {
            File.WriteAllText(Path.Combine(root, "re.exe"), "MZ");
            Directory.CreateDirectory(locked);
            File.SetUnixFileMode(locked, UnixFileMode.None);
            using var fx = await ViewModelFixture.StartAsync([Game(root)]);
            await SelectAsync(fx);

            var result = await fx.Vm.ReFrameworkStorage.DeleteAsync();

            Assert.False(result.Success);
            Assert.StartsWith(REFrameworkDeploymentService.StorageUnreadablePrefix, result.Message, StringComparison.Ordinal);
            Assert.Contains(REFrameworkDeploymentService.StorageUnreadablePrefix, fx.Vm.StatusText, StringComparison.Ordinal);
            Assert.True(Directory.Exists(locked));
            Assert.False(fx.Vm.IsBusy);
        }
        finally
        {
            if (Directory.Exists(locked)) File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task InPortuguese_TheResultLineIsPortuguese()
    {
        var root = Directory.CreateTempSubdirectory("rhi-ref-storage-pt-").FullName;
        UiLocalizationService.Apply("pt-BR");
        try
        {
            File.WriteAllText(Path.Combine(root, "re.exe"), "MZ");
            using var fx = await ViewModelFixture.StartAsync([Game(root)], "pt-BR");
            await SelectAsync(fx);

            var result = await fx.Vm.ReFrameworkStorage.DeleteAsync();

            Assert.False(result.Success);
            Assert.Contains("Nenhuma pasta _storage_ foi encontrada na pasta do jogo: ", fx.Vm.StatusText, StringComparison.Ordinal);
        }
        finally
        {
            UiLocalizationService.Apply("en-US");
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData(ComponentAvailability.Installed, true)]
    [InlineData(ComponentAvailability.UpdateAvailable, true)]
    [InlineData(ComponentAvailability.Available, false)]
    [InlineData(ComponentAvailability.Unsupported, false)]
    public void TheCog_ShowsOnlyWhileReFrameworkIsInstalled(ComponentAvailability availability, bool shown)
    {
        Assert.Equal(shown, ReFrameworkStoragePresenter.HasSettings(new ManagedComponent { Kind = ManagedComponentKind.REFramework, DisplayName = "RE Framework", Availability = availability }));
        Assert.False(ReFrameworkStoragePresenter.HasSettings(new ManagedComponent { Kind = ManagedComponentKind.ReShade, DisplayName = "ReShade", Availability = ComponentAvailability.Installed }));
    }
}
