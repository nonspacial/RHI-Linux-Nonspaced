using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Core.Tests;

/// <summary>
/// #295 (upstream 2.8.9 ⚙ and Settings saving fixes): a typed add-on watch folder that does not exist is refused with a
/// message (the rest of the Settings save), and an invalid ReLimiter custom FPS says so instead of silently becoming "no cap".
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class SettingsSavingFixesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-settings-fixes-" + Guid.NewGuid().ToString("N"));

    public SettingsSavingFixesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        UiLocalizationService.Apply("en-US");
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("en-US", "The add-on watch folder {0} does not exist, so it was not saved; the other settings were.")]
    [InlineData("pt-BR", "A pasta de add-ons observada {0} não existe, então não foi salva; as outras configurações foram.")]
    public async Task MissingWatchFolder_IsNotSaved_TheRestIs_AndTheLineSaysSo(string language, string expected)
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        UiLocalizationService.Apply(language);
        var good = Directory.CreateDirectory(Path.Combine(_root, "Downloads")).FullName;
        var settings = fx.Vm.Settings.GetLinuxSettingsSnapshot().Settings;
        settings.AddonWatchFolder = good;
        settings.UiLanguage = language;
        fx.Vm.Settings.SaveLinuxSettings(settings);

        var missing = Path.Combine(_root, "Nowhere");
        settings = fx.Vm.Settings.GetLinuxSettingsSnapshot().Settings;
        settings.AddonWatchFolder = missing;
        settings.ReShadeVariableListUseTabs = !settings.ReShadeVariableListUseTabs;
        var line = fx.Vm.Settings.SaveLinuxSettings(settings);

        Assert.EndsWith(string.Format(System.Globalization.CultureInfo.InvariantCulture, expected, missing), line);
        Assert.Equal(good, fx.Settings.Load().AddonWatchFolder);
        Assert.Equal(settings.ReShadeVariableListUseTabs, fx.Settings.Load().ReShadeVariableListUseTabs);
    }

    [Fact]
    public async Task ExistingOrEmptyWatchFolder_IsSaved()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var settings = fx.Vm.Settings.GetLinuxSettingsSnapshot().Settings;
        settings.AddonWatchFolder = _root;
        Assert.DoesNotContain("does not exist", fx.Vm.Settings.SaveLinuxSettings(settings), StringComparison.Ordinal);
        Assert.Equal(_root, fx.Settings.Load().AddonWatchFolder);
        settings.AddonWatchFolder = "";
        fx.Vm.Settings.SaveLinuxSettings(settings);
        Assert.Equal("", fx.Settings.Load().AddonWatchFolder);
    }

    [Theory]
    [InlineData(165.0, 120, 165, false)]
    [InlineData(null, 120, 120, true)] // an empty box: the saved cap stays and the line says why
    [InlineData(5.0, 120, 120, true)]
    [InlineData(2000.0, 0, 0, true)]
    public void CustomFps_Invalid_KeepsTheSavedCap_AndSaysSo(double? typed, int saved, int expected, bool problem)
    {
        var fps = IniPresetsPresenter.CustomTargetFps(typed is null ? null : (decimal)typed, saved, out var message);

        Assert.Equal(expected, fps);
        Assert.Equal(problem, message is not null);
        if (problem) Assert.Equal("The ReLimiter custom FPS must be a whole number from 20 to 1000; the saved cap was kept.", message);
    }
}
