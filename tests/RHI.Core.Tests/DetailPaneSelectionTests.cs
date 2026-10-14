using RHI.Core.Models;
using RHI.Desktop.Localization;

namespace RHI.Core.Tests;

/// <summary>
/// The detail pane on a game switch (Refs #152): it is asked to scroll back to the top only when a different game is
/// selected, and with HDR Off the launch-profile preview and its Copy button are hidden.
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class DetailPaneSelectionTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rhi-detail-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public async Task SelectingAnotherGame_ScrollsTheDetailPaneToTheTop_ButARefreshOfTheSameGameDoesNot()
    {
        using var fx = await ViewModelFixture.StartAsync([Game("Alpha Quest"), Game("Beta Quest")]);
        await SelectAsync(fx, "Alpha Quest");
        var switches = 0;
        fx.Vm.SelectedGameSwitched += () => switches++;

        await SelectAsync(fx, "Beta Quest");
        Assert.Equal(1, switches);

        // A refresh clears the selection and puts the same game back: the reader keeps their place.
        await fx.Vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("Beta Quest", fx.Vm.SelectedGame?.Name);
        Assert.Equal(1, switches);

        await SelectAsync(fx, "Alpha Quest");
        Assert.Equal(2, switches);
    }

    [Fact]
    public async Task HdrOff_HidesTheLaunchProfilePreviewAndCopy_AnHdrModeShowsThem()
    {
        using var fx = await ViewModelFixture.StartAsync([Game("Alpha Quest")]);
        await SelectAsync(fx, "Alpha Quest");
        var raised = new List<string?>();
        fx.Vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.Equal(UiLocalizationService.Text("Ui.HdrOff"), fx.Vm.SelectedHdrMode);
        Assert.False(fx.Vm.ShowHdrLaunchProfile);

        fx.Vm.SelectedHdrMode = UiLocalizationService.Text("Ui.HdrNativeWayland");
        Assert.True(fx.Vm.ShowHdrLaunchProfile);
        Assert.Contains(nameof(fx.Vm.ShowHdrLaunchProfile), raised);

        raised.Clear();
        fx.Vm.SelectedHdrMode = UiLocalizationService.Text("Ui.HdrOff");
        Assert.False(fx.Vm.ShowHdrLaunchProfile);
        Assert.Contains(nameof(fx.Vm.ShowHdrLaunchProfile), raised);
    }

    private static async Task SelectAsync(ViewModelFixture fx, string name)
    {
        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Name == name).Game;
        for (var i = 0; i < 200 && (fx.Vm.IsSelectionProbeBusy || !fx.Vm.StatusText.StartsWith("Ready", StringComparison.Ordinal)); i++)
            await Task.Delay(25);
    }

    private DetectedGame Game(string name)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var exe = Path.Combine(dir, name.Replace(" ", "") + ".exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        return new DetectedGame
        {
            Name = name, Source = "Wine", InstallPath = dir, ExecutablePath = exe, BinaryDirectory = dir, LaunchExecutablePath = exe,
            Runner = "Wine", LaunchCommand = "wine Game.exe"
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
