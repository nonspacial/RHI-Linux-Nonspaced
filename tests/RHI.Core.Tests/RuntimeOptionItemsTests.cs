using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// Unit 6 of the split (Refs #89; UI-12/20): the runtime dropdowns hold <c>{Value, Display}</c> items. A language change
/// relabels the same items in place — the selection keeps its value and item, nothing is written back to the stores,
/// and the label follows the language — instead of clearing and refilling string lists (whose cleared ComboBox wrote
/// null back and needed a guard in every handler).
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class RuntimeOptionItemsTests
{
    [Fact]
    public async Task ALanguageChange_RelabelsTheSameItems_AndKeepsEverySelection()
    {
        try
        {
            using var fx = await ViewModelFixture.StartAsync([]);
            var game = fx.Game("Alpha Quest");
            var (added, message) = await fx.Vm.TryAddManualGameAsync(new()
            {
                Name = game.Name, InstallPath = game.InstallPath, ExecutablePath = game.ExecutablePath, Runner = "Manual Windows"
            });
            Assert.True(added, message);
            var runtime = fx.Vm.Runtime;
            runtime.SelectedGraphicsApiOverride = runtime.GraphicsApiOverrideOptions.For(GraphicsApi.DirectX11);
            runtime.SelectedHdrMode = runtime.HdrModeOptions.For(LinuxHdrModes.Gamescope);
            var lists = (runtime.GraphicsApiOverrideOptions, runtime.HdrModeOptions, runtime.LumaModeOptions, runtime.OptiScalerVariantOptions);
            var selected = (runtime.SelectedGraphicsApiOverride, runtime.SelectedHdrMode, runtime.SelectedLumaModePreference, runtime.SelectedOptiScalerVariant);
            var libraryBefore = File.ReadAllText(fx.Library.LibraryPath);
            var relabelled = new List<string?>();
            runtime.SelectedHdrMode.PropertyChanged += (_, e) => relabelled.Add(e.PropertyName);
            var automatic = runtime.GraphicsApiOverrideOptions.For(null);

            var settings = fx.Vm.GetLinuxSettingsSnapshot().Settings;
            settings.UiLanguage = "pt-BR";
            fx.Vm.SaveLinuxSettings(settings);

            Assert.Equal(lists, (runtime.GraphicsApiOverrideOptions, runtime.HdrModeOptions, runtime.LumaModeOptions, runtime.OptiScalerVariantOptions));
            Assert.Equal(selected, (runtime.SelectedGraphicsApiOverride, runtime.SelectedHdrMode, runtime.SelectedLumaModePreference, runtime.SelectedOptiScalerVariant));
            Assert.Equal((GraphicsApi?)GraphicsApi.DirectX11, runtime.SelectedGraphicsApiOverride.Value);
            Assert.Equal(LinuxHdrModes.Gamescope, runtime.SelectedHdrMode.Value);
            Assert.Contains("Display", relabelled);
            Assert.Equal(UiLocalizationService.TextFor("pt-BR", "Ui.OptionAutomatic"), automatic.Display);
            Assert.Equal(libraryBefore, File.ReadAllText(fx.Library.LibraryPath)); // the HDR profile was not saved again
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }

    [Fact]
    public void OptionItems_FindTheirItem_ByValue_OrByAnyLabel()
    {
        var items = RHI.Desktop.ViewModels.OptionLists.HdrMode.Options();
        Assert.Same(items.For(LinuxHdrModes.NativeWayland), items.ForDisplay(UiLocalizationService.TextFor("pt-BR", "Ui.HdrNativeWayland")));
        Assert.Same(items.For(LinuxHdrModes.NativeWayland), items.ForDisplay(UiLocalizationService.TextFor("en-US", "Ui.HdrNativeWayland")));
        Assert.Same(items.For(LinuxHdrModes.Off), items.For("no-such-mode")); // unknown values fall back, as the stores do
    }
}
