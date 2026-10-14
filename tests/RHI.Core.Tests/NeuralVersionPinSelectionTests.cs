using System.ComponentModel;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Core.Tests;

/// <summary>
/// #80 (UI-12): Avalonia's ComboBox drops a SelectedItem that is not in its ItemsSource at the moment it is set, and
/// adding the item afterwards does not select it, so a pinned neural version showed a blank box. Every time the view
/// model changes a version selection, the value must already be one of the options.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class NeuralVersionPinSelectionTests
{
    [Fact]
    public async Task SelectingAGameWithPins_EveryPinIsAnOptionBeforeItIsSelected()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var pinned = await AddAsync(fx, "Tunic");
        await AddAsync(fx, "Celeste");
        fx.Managed.SetNeuralAddonVersionPin(pinned, ManagedComponentKind.Dlss5Feeder, "v9.9-feeder");
        fx.Managed.SetNeuralAddonVersionPin(pinned, ManagedComponentKind.Dlss5Dx11Bridge, "v9.9-bridge");
        fx.Managed.SetNeuralAddonVersionPin(pinned, ManagedComponentKind.ShortFuse, "renodx-dlss-SF-9.9");
        fx.Managed.SetNeuralRenderingDllVersionPin(pinned, "310.9.9");
        var violations = Watch(fx.Vm);

        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Name == "Tunic").Game;

        Assert.Empty(violations);
        Assert.Equal(("v9.9-feeder", "v9.9-bridge", "renodx-dlss-SF-9.9", "310.9.9"),
            (fx.Vm.Extras.SelectedDlss5FeederVersion, fx.Vm.Extras.SelectedDlss5BridgeVersion, fx.Vm.Extras.SelectedShortFuseVersion, fx.Vm.Extras.SelectedNeuralRenderingDllVersion));
    }

    [Fact]
    public async Task SwitchingLanguage_KeepsEveryPinAnOptionBeforeItIsSelected()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var pinned = await AddAsync(fx, "Tunic");
        await AddAsync(fx, "Celeste"); // selected after adding
        fx.Managed.SetNeuralAddonVersionPin(pinned, ManagedComponentKind.Dlss5Feeder, "v9.9-feeder");
        fx.Managed.SetNeuralRenderingDllVersionPin(pinned, "310.9.9");
        fx.Vm.SelectedGame = fx.Vm.Games.Single(g => g.Name == "Tunic").Game;
        Assert.Equal("v9.9-feeder", fx.Vm.Extras.SelectedDlss5FeederVersion);
        var violations = Watch(fx.Vm);
        var settings = fx.Vm.Settings.GetLinuxSettingsSnapshot().Settings;
        settings.UiLanguage = "pt-BR";
        try
        {
            fx.Vm.Settings.SaveLinuxSettings(settings);

            Assert.Empty(violations);
            Assert.Equal(("v9.9-feeder", "310.9.9"), (fx.Vm.Extras.SelectedDlss5FeederVersion, fx.Vm.Extras.SelectedNeuralRenderingDllVersion));
            Assert.Equal(UiLocalizationService.TextFor("pt-BR", "Ui.OptionLatest"), fx.Vm.Extras.SelectedDlss5BridgeVersion);
            Assert.Contains(fx.Vm.Extras.SelectedDlss5BridgeVersion, fx.Vm.Extras.Dlss5BridgeVersionOptions);
        }
        finally
        {
            UiLocalizationService.Apply("en-US");
        }
    }

    /// <summary>Records every version selection the view model set while the value was not among its options.</summary>
    private static List<string> Watch(MainWindowViewModel vm)
    {
        var violations = new List<string>();
        var lists = new Dictionary<string, Func<IEnumerable<string>>>
        {
            [nameof(ExtrasViewModel.SelectedDlss5FeederVersion)] = () => vm.Extras.Dlss5FeederVersionOptions,
            [nameof(ExtrasViewModel.SelectedDlss5BridgeVersion)] = () => vm.Extras.Dlss5BridgeVersionOptions,
            [nameof(ExtrasViewModel.SelectedShortFuseVersion)] = () => vm.Extras.ShortFuseVersionOptions,
            [nameof(ExtrasViewModel.SelectedNeuralRenderingDllVersion)] = () => vm.Extras.NeuralRenderingDllVersionOptions,
        };
        vm.Extras.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not { } name || !lists.TryGetValue(name, out var options)) return;
            var value = (string?)typeof(ExtrasViewModel).GetProperty(name)!.GetValue(vm.Extras);
            if (value is not null && !options().Contains(value)) violations.Add($"{name}={value}");
        };
        return violations;
    }

    private static async Task<DetectedGame> AddAsync(ViewModelFixture fx, string name)
    {
        var game = fx.Game(name);
        var (added, message) = await fx.Vm.ManualGames.TryAddManualGameAsync(new()
        {
            Name = game.Name, InstallPath = game.InstallPath, ExecutablePath = game.ExecutablePath, Runner = "Manual Windows"
        });
        Assert.True(added, message);
        return fx.Vm.Games.Single(g => g.Name == name).Game;
    }
}
