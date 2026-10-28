using RHI.Core.Services;
using RHI.Desktop.Localization;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// Status line after a library scan. A detector that failed or skipped entries is named, so an
/// unreadable Steam mount no longer reads as "Steam has no games" (Refs #56). The problems are raised
/// as a one-shot notice, so the selected game's "Ready — …" line that follows the scan still carries
/// them. Details go to the log.
/// </summary>
public static class LibraryScanStatusText
{
    public static string Done(int gameCount)
        => UiLocalizationService.Format("Vm.ScanCompleteGamesDetected", gameCount);

    /// <summary>
    /// The problems notice, or null for a clean scan. w12 final review m1: a detector's "listed without a launch command"
    /// line counts only the games of the final <paramref name="library"/> (Edit game's commands applied) that still have
    /// none, and is left out at 0; with nothing else to say the notice is null, never an empty "Scan problems:".
    /// </summary>
    public static string? Problems(GameLibraryScanResult scan, string? logPath, IReadOnlyCollection<RHI.Core.Models.DetectedGame> library)
    {
        if (!scan.HasProblems) return null;

        // Canonical paths of the library's games that still have no launch command (detected or set in Edit game).
        var stillWithout = new HashSet<string>(StringComparer.Ordinal);
        if (scan.Detectors.Any(detector => detector.ListedWithoutLaunchCommand.Count > 0))
            foreach (var game in library.Where(game => string.IsNullOrWhiteSpace(game.LaunchCommand)))
                foreach (var path in new[] { game.InstallPath, game.LibraryIdentityPath })
                    if (!string.IsNullOrWhiteSpace(path)) stillWithout.Add(PathIdentity.Canonicalize(path));

        var problems = new List<string>();
        foreach (var detector in scan.Detectors)
        {
            if (detector.Failed)
                problems.Add(UiLocalizationService.Format("Scan.DetectorFailed", detector.Name, UiLocalizationService.Present(detector.Error)));
            else if (detector.Warnings.Count > 0)
                problems.Add(UiLocalizationService.Format("Scan.DetectorSkippedItems", detector.Name, detector.Warnings.Count));
            // w12 follow-up (merge review nit 1): listed games without a launch command are not skipped items.
            var listed = detector.Failed ? 0 : detector.ListedWithoutLaunchCommand.Count(stillWithout.Contains);
            if (listed > 0)
                problems.Add(UiLocalizationService.Format("Scan.ListedWithoutLaunchCommand", detector.Name, listed));
        }
        foreach (var policy in scan.PolicyFailures)
        {
            var name = policy.Name == RHI.Platform.Linux.Detection.UpstreamGameLibraryPolicyService.PolicyName
                ? UiLocalizationService.Text("Scan.UpstreamManifest")
                : policy.Name;
            problems.Add(UiLocalizationService.Format("Scan.PolicyNotApplied", name, UiLocalizationService.Present(policy.Error)));
        }

        if (problems.Count == 0) return null;
        var text = UiLocalizationService.Format("Scan.Problems", string.Join("; ", problems));
        return string.IsNullOrWhiteSpace(logPath)
            ? text
            : UiLocalizationService.Format("Scan.DetailsIn", text, logPath);
    }
}
