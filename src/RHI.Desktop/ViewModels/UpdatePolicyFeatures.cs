using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// w5/update-policy: what the shell carries as <see cref="MainWindowViewModel.UpdatePolicy"/> (set by the app
/// composition and the test fixture). #177: following the newest DLSS / Streamline, run by <see cref="UpdatesViewModel"/>
/// after start-up, every Full Refresh and every background pass.
/// </summary>
public sealed class UpdatePolicyFeatures
{
    public UpdatePolicyFeatures(DlssAutoFollowService follow)
    {
        Follow = follow;
    }

    public DlssAutoFollowService Follow { get; }

    /// <summary>The two Settings toggles.</summary>
    public static DlssFollowChoice ChoiceFrom(LinuxAppSettings settings)
        => new(settings.FollowNewestDlss, settings.FollowNewestStreamline);

    /// <summary>
    /// The status line for a pass that changed (or failed to change, or put off) a game file; null when it did none of
    /// those (seeded, current, offline): an automatic pass that did nothing stays quiet.
    /// </summary>
    public static string? FollowSummary(DlssFollowResult result)
    {
        if (result.UpdatedCount == 0 && result.FailedCount == 0 && result.DeferredGames == 0) return null;
        var text = UiLocalizationService.Format("Upd.FollowSummary", result.UpdatedCount);
        if (result.FailedCount > 0) text += UiLocalizationService.Format("Upd.FollowFailedPart", result.FailedCount);
        if (result.DeferredGames > 0) text += UiLocalizationService.Format("Upd.FollowDeferredPart", result.DeferredGames);
        return text + ".";
    }
}
