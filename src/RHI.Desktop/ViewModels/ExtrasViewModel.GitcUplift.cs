using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RHI.Core.Models;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// w14/nr (#274): GITC Uplift's per-game controls beside the other Neural Rendering versions (upstream's Neural Rendering
/// section: Uplift Version, the iMMERSE Launchpad toggle, the GITC Uplift link). Install / Update / Remove are the GITC Uplift
/// row's in Managed components.
/// </summary>
public sealed partial class ExtrasViewModel
{
    public ObservableCollection<string> GitcUpliftVersionOptions { get; } = [UiLocalizationService.Text("Ui.OptionLatest")];

    [ObservableProperty] private string selectedGitcUpliftVersion = UiLocalizationService.Text("Ui.OptionLatest");

    /// <summary>Shown only where the app was built with the Uplift service.</summary>
    public bool ShowGitcUplift => _managedComponents.GitcUplift is not null;

    public bool CanConfigureUpliftVersionPin => ShowGitcUplift && SelectedGame is not null && !IsBusy && !IsSelectionProbeBusy;

    public bool CanConfigureUpliftLaunchpad => CanConfigureUpliftVersionPin && SelectedGame!.HasConfirmedPatchTarget;

    partial void OnSelectedGitcUpliftVersionChanged(string value)
    {
        if (ShowGitcUplift) SaveNeuralVersionPin(ManagedComponentKind.GitcUplift, value);
    }

    /// <summary>The game's iMMERSE Launchpad choice (installed: what is in the game; else the saved choice or the default).</summary>
    public bool UpliftLaunchpad
    {
        get => SelectedGame is { } game && ShowGitcUplift
               && _managedComponents.GetUpliftLaunchpadChoice(game, Facts?.HasNativeDlssForNeuralRendering ?? false);
        set
        {
            if (SelectedGame is not { } game || !ShowGitcUplift || value == UpliftLaunchpad) return;
            UiExceptionGuard.Forget(SetUpliftLaunchpadAsync(game, value), "SetUpliftLaunchpadAsync");
        }
    }

    private async Task SetUpliftLaunchpadAsync(DetectedGame game, bool on)
    {
        using var op = BeginOperation("uplift-launchpad");
        if (op is null)
        {
            OnPropertyChanged(nameof(UpliftLaunchpad));
            return;
        }
        try
        {
            var result = await _managedComponents.SetUpliftLaunchpadAsync(game, on, op.Token);
            StatusText = OutcomeText(op, result.Success, result.Message);
        }
        catch (Exception ex) when (op.WasCancelled(ex))
        {
            StatusText = OperationCancelledText();
        }
        finally
        {
            OnPropertyChanged(nameof(UpliftLaunchpad));
            Host.NotifyDlss();
        }
    }

    [RelayCommand]
    private void OpenGitcUpliftLink()
    {
        if (!LinuxDesktopOpener.TryOpenUri(GitcUpliftDeploymentService.RepositoryUrl, out var error))
            StatusText = UiLocalizationService.Format("Parity.LinkNotOpened", UiLocalizationService.Present(error));
    }

    private void NotifyUplift()
    {
        OnPropertyChanged(nameof(CanConfigureUpliftVersionPin));
        OnPropertyChanged(nameof(CanConfigureUpliftLaunchpad));
        OnPropertyChanged(nameof(UpliftLaunchpad));
    }

    private void LoadUplift(DetectedGame game)
    {
        if (!ShowGitcUplift) return;
        var pin = _managedComponents.GetNeuralAddonVersionPin(game, ManagedComponentKind.GitcUplift) ?? LatestDisplay();
        EnsureOption(GitcUpliftVersionOptions, pin);
        SelectedGitcUpliftVersion = pin;
        NotifyUplift();
    }

    private void ResetUplift()
    {
        ReplaceLatestLabel(GitcUpliftVersionOptions, LatestDisplay());
        SelectedGitcUpliftVersion = LatestDisplay();
        NotifyUplift();
    }

    private void RefreshUpliftLocalized()
    {
        var pin = IsLatestSelection(SelectedGitcUpliftVersion) ? null : SelectedGitcUpliftVersion;
        ReplaceLatestLabel(GitcUpliftVersionOptions, pin ?? LatestDisplay());
        SelectedGitcUpliftVersion = pin ?? LatestDisplay();
    }

    private void ApplyUpliftVersions(IReadOnlyList<GitHubReleaseVersion>? versions)
    {
        if (versions is null || versions.Count == 0) return;
        var selected = SelectedGitcUpliftVersion;
        ReplaceVersionOptions(GitcUpliftVersionOptions, versions, selected);
        SelectedGitcUpliftVersion = selected ?? LatestDisplay();
    }
}
