using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// #200 DX9 HDR: the row's ⚙ (the dxvk.conf preset, upstream's six tiers) and the install confirmation that says, in
/// plain words, what the install changes — including that an RHI ReShade installed as d3d9.dll moves to its Vulkan layer
/// (and back on removal). The ⚙ shows only while RHI's DX9 HDR is installed (the preset is written into its dxvk.conf).
/// </summary>
public sealed class DxvkHdrPresenter(MainWindowViewModel vm, LinuxManagedComponentService managed)
{
    private static string T(string key) => UiLocalizationService.Text(key);

    /// <summary>Whether a component row carries the DX9 HDR ⚙: RHI's DX9 HDR is installed.</summary>
    public static bool HasSettings(ManagedComponent component)
        => component.Kind == ManagedComponentKind.DxvkHdr && component.ManagedByRhi
           && component.Availability is ComponentAvailability.Installed or ComponentAvailability.UpdateAvailable;

    /// <summary>The ⚙'s 2 s tooltip: what it is, and the provenance (author, fork, upstream project).</summary>
    public static string SettingsTip => T("Dxvk.SettingsTip");

    /// <summary>The preset labels in the current language, safest first.</summary>
    public IReadOnlyList<string> PresetItems => OptionLists.DxvkHdrPreset.Items;

    /// <summary>The selected game's installed preset, as its label.</summary>
    public string CurrentPreset => OptionLists.DxvkHdrPreset.Display(vm.SelectedGame is { } game && managed.DxvkHdr is { } service
        ? service.PresetOf(game)
        : DxvkHdrPresets.Default);

    /// <summary>Whether the selected game's HDR launch setting is Off (DX9 HDR then shows no HDR).</summary>
    public bool HdrLaunchOff => vm.SelectedGame is { } game && !HdrLaunchOn(((IPanelHost)vm).LibraryState, game);

    /// <summary>Whether the game's saved HDR launch profile is on (any mode but Off).</summary>
    public static bool HdrLaunchOn(LinuxGameLibraryState state, DetectedGame game)
        => state.HdrProfiles.TryGetValue(LinuxGameLibraryStore.GameKey(game), out var profile)
           && profile.Normalize().Mode != LinuxHdrModes.Off;

    /// <summary>
    /// The install confirmation (title, what changes, accept). Returns false — and says the install was cancelled — when
    /// the user declines; true without a window that can ask (tests and headless runs answer through <see cref="MainWindowViewModel.ConfirmAsync"/>).
    /// </summary>
    public async Task<bool> ConfirmInstallAsync(DetectedGame game)
    {
        if (vm.ConfirmAsync is not { } confirm) return true;
        var body = T("Dxvk.ConfirmBody");
        if (managed.DxvkHdr?.WillMoveReShade(game) == true) body += "\n\n" + T("Dxvk.ConfirmReShade");
        if (await confirm(UiLocalizationService.Format("Dxvk.ConfirmTitle", game.Name), body, T("Vm.Install"))) return true;
        vm.StatusText = UiLocalizationService.Format("Vm.InstallationCancelled", DxvkHdrDeploymentService.DisplayName);
        return false;
    }

    /// <summary>
    /// Writes the chosen preset into the game's dxvk.conf (journaled; refused when the user changed the file), under the
    /// busy gate. The result line also goes to the status bar; returns the shown line.
    /// </summary>
    public async Task<string> ApplyPresetAsync(string? display)
    {
        if (vm.SelectedGame is not { } game) return T("Vm.SelectGameFirst");
        using var op = vm.Operations.TryBegin("dxvk-hdr-preset", cancellable: false);
        if (op is null)
        {
            var busy = T("Vm.AnotherRHIOperationAlreadyRunning");
            vm.StatusText = busy;
            return busy;
        }
        var result = await managed.SetDxvkHdrPresetAsync(game, OptionLists.DxvkHdrPreset.Value(display), op.Token);
        var line = UiLocalizationService.Present(result.Message);
        vm.StatusText = line;
        return line;
    }
}
