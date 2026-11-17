using CommunityToolkit.Mvvm.Input;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Detection;

namespace RHI.Desktop.ViewModels;

// #290 (upstream 2.9.4 "Block Steam updates", Game settings): the selected Steam game's app manifest set to launch-only,
// read-only and immutable (pkexec chattr +i), or back as it was.
public partial class MainWindowViewModel
{
    private bool _steamUpdateBlockRunning;

    /// <summary>The service that blocks and allows Steam updates (tests hand in one with a fake pkexec).</summary>
    public SteamUpdateBlockService SteamUpdates { get; set; } = new();

    private SteamUpdateBlockState SteamUpdateState => SelectedGame is { } game ? SteamUpdates.State(game) : SteamUpdateBlockState.Unavailable;

    /// <summary>The switch shows only for a Steam game whose app manifest RHI found.</summary>
    public bool CanBlockSteamUpdates => SteamUpdateState != SteamUpdateBlockState.Unavailable;

    /// <summary>The switch is on: the block is in place, fully (immutable) or only partly (launch-only, read-only).</summary>
    public bool SteamUpdatesBlocked => SteamUpdateState is SteamUpdateBlockState.Blocked or SteamUpdateBlockState.LaunchOnly;

    /// <summary>"Blocked" only when the manifest's immutable flag is confirmed; the partial block says what it leaves open.</summary>
    public string SteamUpdateBlockStatus => SteamUpdateState switch
    {
        SteamUpdateBlockState.Blocked => UiLocalizationService.Text("Steam.StatusBlocked"),
        SteamUpdateBlockState.LaunchOnly => UiLocalizationService.Text("Steam.StatusLaunchOnly"),
        _ => ""
    };

    public string BlockSteamUpdatesText => UiLocalizationService.Text(SteamUpdatesBlocked ? "Steam.AllowUpdates" : "Steam.BlockUpdates");

    public string BlockSteamUpdatesTip => UiLocalizationService.Text("Steam.BlockUpdatesTip");

    [RelayCommand]
    private async Task ToggleSteamUpdatesAsync()
    {
        if (SelectedGame is not { } game || IsBusy || _steamUpdateBlockRunning) return;
        var block = !SteamUpdatesBlocked;
        _steamUpdateBlockRunning = true;
        try
        {
            var result = await SteamUpdates.SetAsync(game, block);
            StatusText = !result.Success ? SteamUpdateBlockFailure(result.Message)
                : !block ? UiLocalizationService.Format("Steam.UpdatesAllowed", game.Name)
                : result.State == SteamUpdateBlockState.Blocked ? UiLocalizationService.Format("Steam.UpdatesBlocked", game.Name)
                : UiLocalizationService.Format("Steam.UpdatesLaunchOnly", game.Name, SteamUpdateBlockLimitText(result));
        }
        finally
        {
            _steamUpdateBlockRunning = false;
            NotifySteamUpdateBlock();
        }
    }

    internal static string SteamUpdateBlockLimitText(SteamUpdateBlockResult result) => result.Limit switch
    {
        SteamUpdateBlockLimit.NoPkexec => UiLocalizationService.Text("Steam.LimitNoPkexec"),
        SteamUpdateBlockLimit.NoChattr => UiLocalizationService.Text("Steam.LimitNoChattr"),
        SteamUpdateBlockLimit.Unsupported => UiLocalizationService.Text("Steam.LimitUnsupported"),
        SteamUpdateBlockLimit.Cancelled when result.Detail == SteamUpdateBlockService.UpdatingMessage => UiLocalizationService.Text("Steam.UpdatingNow"),
        SteamUpdateBlockLimit.Cancelled => UiLocalizationService.Text("Steam.LimitCancelled"),
        SteamUpdateBlockLimit.Failed => UiLocalizationService.Format("Steam.LimitFailed", result.Detail ?? ""),
        _ => UiLocalizationService.Text("Steam.LimitNotConfirmed")
    };

    internal static string SteamUpdateBlockFailure(string? message) => message switch
    {
        SteamUpdateBlockService.UpdatingMessage => UiLocalizationService.Text("Steam.UpdatingNow"),
        SteamUpdateBlockService.NotSteamMessage => UiLocalizationService.Text("Steam.NoManifest"),
        SteamUpdateBlockService.UnlockCancelledMessage => UiLocalizationService.Text("Steam.UnlockCancelled"),
        { } failed when failed.StartsWith(SteamUpdateBlockService.UnlockFailedPrefix, StringComparison.Ordinal)
            => UiLocalizationService.Format("Steam.UnlockFailed", failed[SteamUpdateBlockService.UnlockFailedPrefix.Length..]),
        { } failed when failed.StartsWith(SteamUpdateBlockService.FailedPrefix, StringComparison.Ordinal)
            => UiLocalizationService.Format("Steam.ManifestNotChanged", failed[SteamUpdateBlockService.FailedPrefix.Length..]),
        _ => UiLocalizationService.Present(message)
    };

    private void NotifySteamUpdateBlock()
    {
        OnPropertyChanged(nameof(CanBlockSteamUpdates));
        OnPropertyChanged(nameof(SteamUpdatesBlocked));
        OnPropertyChanged(nameof(SteamUpdateBlockStatus));
        OnPropertyChanged(nameof(BlockSteamUpdatesText));
    }
}
