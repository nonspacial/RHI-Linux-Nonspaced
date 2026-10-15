using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// w5/update-policy: what the shell carries as <see cref="MainWindowViewModel.UpdatePolicy"/> (set by the app
/// composition and the test fixture). #177: following the newest DLSS / Streamline, run by <see cref="UpdatesViewModel"/>
/// after start-up, every Full Refresh and every background pass. #178: a game's update inclusion (card menu → Update
/// inclusion…), over the same store the component service and the follower read.
/// </summary>
public sealed class UpdatePolicyFeatures
{
    public UpdatePolicyFeatures(DlssAutoFollowService follow, UpdateInclusionStore? inclusion = null)
    {
        Follow = follow;
        Inclusion = inclusion;
    }

    public DlssAutoFollowService Follow { get; }

    /// <summary>#178: the per-game update inclusion; null without one (the menu entry is then left out).</summary>
    public UpdateInclusionStore? Inclusion { get; }

    /// <summary>Set by the window: shows the dialog and returns the components left out, or null when cancelled.</summary>
    public Func<UpdateInclusionDialogModel, Task<IReadOnlySet<ManagedComponentKind>?>>? ShowInclusionAsync { get; set; }

    /// <summary>
    /// #178: the update-inclusion dialog for <paramref name="game"/>, or null when it would offer nothing (the card menu
    /// then leaves the entry out). One box per installed RHI component Update All takes part in, except those Settings
    /// already leaves out for every game (named in a note instead); DLSS and Streamline boxes only while their Follow
    /// newest toggle is on, for a Windows game with a confirmed target.
    /// </summary>
    public UpdateInclusionDialogModel? InclusionFor(DetectedGame game, IEnumerable<ManagedComponentKind> installed, LinuxAppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (Inclusion is null || ShowInclusionAsync is null) return null;
        var excluded = Inclusion.ExcludedFor(game);
        var global = UpdateInclusionStore.GlobalSkipsFrom(settings);
        var components = LinuxUpdateAllService.SupportedComponents.Where(installed.Contains).ToArray();
        var choices = components.Where(kind => !global.Contains(kind))
            .Select(kind => new UpdateInclusionChoice(kind, LinuxManagedComponentService.DisplayDependencyName(kind), !excluded.Contains(kind)))
            .ToList();
        var windowsTarget = game.HasConfirmedPatchTarget && !string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase);
        if (windowsTarget && settings.FollowNewestDlss)
            choices.Add(new(ManagedComponentKind.Dlss, UiLocalizationService.Text("Upd.DialogDlss"), !excluded.Contains(ManagedComponentKind.Dlss)));
        if (windowsTarget && settings.FollowNewestStreamline)
            choices.Add(new(ManagedComponentKind.Streamline, UiLocalizationService.Text("Upd.DialogStreamline"), !excluded.Contains(ManagedComponentKind.Streamline)));
        if (choices.Count == 0) return null;
        var everyGame = components.Where(global.Contains).Select(LinuxManagedComponentService.DisplayDependencyName).ToArray();
        return new UpdateInclusionDialogModel(
            UiLocalizationService.Format("Upd.DialogTitle", game.Name),
            UiLocalizationService.Text("Upd.DialogIntro"),
            choices,
            everyGame.Length == 0 ? null : UiLocalizationService.Format("Upd.DialogAllGamesNote", string.Join(", ", everyGame)));
    }

    /// <summary>
    /// Shows the dialog and saves what it returns through <paramref name="persist"/> (the shell's one store-write helper);
    /// the status line to show, or null when cancelled.
    /// </summary>
    public async Task<string?> EditInclusionAsync(DetectedGame game, UpdateInclusionDialogModel model, Func<Action, string?> persist)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (ShowInclusionAsync is not { } show || Inclusion is not { } store) return null;
        var excluded = await show(model);
        if (excluded is null) return null;
        return persist(() => store.SetExcluded(game, model.Choices.Select(choice => choice.Kind), excluded))
               ?? UiLocalizationService.Format("Upd.InclusionSaved", game.Name);
    }

    /// <summary>The two Settings toggles.</summary>
    public static DlssFollowChoice ChoiceFrom(LinuxAppSettings settings)
        => new(settings.FollowNewestDlss, settings.FollowNewestStreamline);

    /// <summary>
    /// The status line for a pass that changed (or failed to change, or newly put off) a game file; null when it did none
    /// of those (seeded, current, offline, or the same games skipped for the same reason as last time): an automatic pass
    /// with nothing new to say stays quiet. Each skip says why: running, busy in the app that installed it, or the newest
    /// version could not be downloaded.
    /// </summary>
    public static string? FollowSummary(DlssFollowResult result)
    {
        var running = result.NewlySkipped(DlssFollowHoldReason.Running);
        var busy = result.NewlySkipped(DlssFollowHoldReason.InstallerBusy);
        var download = result.NewlySkipped(DlssFollowHoldReason.DownloadFailed);
        if (result.UpdatedCount == 0 && result.FailedCount == 0 && running + busy + download == 0) return null;
        var text = UiLocalizationService.Format("Upd.FollowSummary", result.UpdatedCount);
        if (result.FailedCount > 0) text += UiLocalizationService.Format("Upd.FollowFailedPart", result.FailedCount);
        if (running > 0) text += UiLocalizationService.Format("Upd.FollowDeferredPart", running);
        if (busy > 0) text += UiLocalizationService.Format("Upd.FollowBusyPart", busy);
        if (download > 0) text += UiLocalizationService.Format("Upd.FollowDownloadPart", download);
        return text + ".";
    }
}

/// <summary>One box of the update-inclusion dialog: ticked = Update All (or Follow newest) may change it in this game.</summary>
public sealed record UpdateInclusionChoice(ManagedComponentKind Kind, string Label, bool Included);

/// <summary>#178: what the update-inclusion dialog shows for one game.</summary>
public sealed record UpdateInclusionDialogModel(string Title, string Intro, IReadOnlyList<UpdateInclusionChoice> Choices, string? AllGamesNote);
