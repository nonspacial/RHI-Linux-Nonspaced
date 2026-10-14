using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// RE Framework ⚙ (#159, upstream 2.8.6 PAR-D72): shows where the selected game's <c>_storage_</c> cache folder is (or
/// that there is none) and deletes it on the user's request, under the busy gate and the patch-target guard. The row's
/// ⚙ shows only while RE Framework is installed, as upstream enables it.
/// </summary>
public sealed class ReFrameworkStoragePresenter(MainWindowViewModel vm, LinuxManagedComponentService managed)
{
    /// <summary>Whether a component row carries the RE Framework ⚙ (installed, by RHI or found installed).</summary>
    public static bool HasSettings(ManagedComponent component)
        => component.Kind == ManagedComponentKind.REFramework
           && component.Availability is ComponentAvailability.Installed or ComponentAvailability.UpdateAvailable;

    public static string SettingsTip => UiLocalizationService.Text("Journal.RefSettingsTip");

    /// <summary>The selected game's <c>_storage_</c> folder, or null when it has none (read on each call).</summary>
    public string? FolderPath => vm.SelectedGame is { } game ? REFrameworkDeploymentService.FindStorageFolder(game) : null;

    /// <summary>"Found:" (the path is shown on its own line) or the not-found line.</summary>
    public string StatusLine => UiLocalizationService.Text(FolderPath is null ? "Journal.RefStorageNotFound" : "Journal.RefStorageFound");

    public bool CanDelete => FolderPath is not null;

    /// <summary>
    /// Deletes the folder (the service refuses links, folders outside the game and a running prefix). The result line
    /// also goes to the status bar; another running operation refuses it without touching anything.
    /// </summary>
    public async Task<ComponentOperationResult> DeleteAsync()
    {
        if (vm.SelectedGame is not { } game) return ComponentOperationResult.Fail(UiLocalizationService.Text("Journal.RefStorageNoGame"));
        using var op = vm.Operations.TryBegin("reframework-storage", cancellable: false);
        if (op is null)
        {
            var busy = UiLocalizationService.Text("Vm.AnotherRHIOperationAlreadyRunning");
            vm.StatusText = busy;
            return ComponentOperationResult.Fail(busy);
        }
        var result = await managed.DeleteReFrameworkStorageFolderAsync(game, op.Token);
        var line = UiLocalizationService.Present(result.Message);
        vm.StatusText = line;
        return result with { Message = line };
    }
}
