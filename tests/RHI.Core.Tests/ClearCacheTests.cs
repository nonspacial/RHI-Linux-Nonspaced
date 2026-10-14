namespace RHI.Core.Tests;

/// <summary>
/// Settings → Clear cache (Refs #84, UI-17): the cache is deleted off the calling (UI) thread while the operation holds
/// the busy gate, and it is refused, deleting nothing, while another operation runs.
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class ClearCacheTests
{
    [Fact]
    public async Task ClearCache_RunsOffTheCallingThread_HoldingTheBusyGate()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var cache = fx.Settings.CacheRoot;
        for (var i = 0; i < 3000; i++)
        {
            var dir = Directory.CreateDirectory(Path.Combine(cache, "packages", $"p{i % 30}")).FullName;
            File.WriteAllText(Path.Combine(dir, $"f{i}.bin"), "x");
        }

        var clearing = fx.Vm.Settings.PurgeLinuxCacheAsync();

        // The deletion runs on the thread pool: the caller gets an unfinished task back and the gate is held meanwhile.
        Assert.False(clearing.IsCompleted);
        Assert.True(fx.Vm.IsBusy);
        Assert.Equal("clear-cache", fx.Vm.Operations.CurrentKind);
        Assert.False(fx.Vm.Operations.CanCancel);

        var message = await clearing;
        Assert.Equal(RHI.Desktop.Localization.UiLocalizationService.Format("Vm.FileSRemovedFromCache", 3000), message);
        Assert.False(fx.Vm.IsBusy);
        Assert.False(Directory.Exists(cache));
    }

    [Fact]
    public async Task ClearCache_WhileAnotherOperationRuns_IsRefused_AndDeletesNothing()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var file = Path.Combine(Directory.CreateDirectory(fx.Settings.CacheRoot).FullName, "package.zip");
        File.WriteAllText(file, "x");
        using var held = fx.Vm.Operations.TryBegin("test");

        var message = await fx.Vm.Settings.PurgeLinuxCacheAsync();

        Assert.Equal(RHI.Desktop.Localization.UiLocalizationService.Text("Vm.AnotherRHIOperationAlreadyRunning"), message);
        Assert.True(File.Exists(file));
    }
}
