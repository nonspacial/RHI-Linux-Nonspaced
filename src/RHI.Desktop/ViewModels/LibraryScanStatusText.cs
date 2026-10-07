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

    /// <summary>The problems notice, or null for a clean scan.</summary>
    public static string? Problems(GameLibraryScanResult scan, string? logPath)
    {
        if (!scan.HasProblems) return null;

        var problems = new List<string>();
        foreach (var detector in scan.Detectors)
        {
            if (detector.Failed)
                problems.Add(UiLocalizationService.Format("Scan.DetectorFailed", detector.Name, detector.Error));
            else if (detector.Warnings.Count > 0)
                problems.Add(UiLocalizationService.Format("Scan.DetectorSkippedItems", detector.Name, detector.Warnings.Count));
        }
        foreach (var policy in scan.PolicyFailures)
        {
            var name = policy.Name == RHI.Platform.Linux.Detection.UpstreamGameLibraryPolicyService.PolicyName
                ? UiLocalizationService.Text("Scan.UpstreamManifest")
                : policy.Name;
            problems.Add(UiLocalizationService.Format("Scan.PolicyNotApplied", name, policy.Error));
        }

        var text = UiLocalizationService.Format("Scan.Problems", string.Join("; ", problems));
        return string.IsNullOrWhiteSpace(logPath)
            ? text
            : UiLocalizationService.Format("Scan.DetailsIn", text, logPath);
    }
}
