using RHI.Core.Services;
using RHI.Desktop.Localization;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// Status line after a library scan. A detector that failed or skipped entries is named, so an
/// unreadable Steam mount no longer reads as "Steam has no games" (Refs #56). Details go to the log.
/// </summary>
public static class LibraryScanStatusText
{
    public static string Format(GameLibraryScanResult scan, int gameCount, string? logPath)
    {
        var done = UiLocalizationService.ChooseFormat("Verificação concluída — {0} jogos detectados", "Scan complete — {0} games detected", gameCount);
        if (!scan.HasProblems) return done;

        var problems = new List<string>();
        foreach (var detector in scan.Detectors)
        {
            if (detector.Failed)
                problems.Add(UiLocalizationService.ChooseFormat("{0}: falhou ({1})", "{0}: failed ({1})", detector.Name, detector.Error));
            else if (detector.Warnings.Count > 0)
                problems.Add(UiLocalizationService.ChooseFormat("{0}: {1} item(ns) ignorado(s)", "{0}: {1} item(s) skipped", detector.Name, detector.Warnings.Count));
        }
        foreach (var policy in scan.PolicyFailures)
        {
            var name = policy.Name == RHI.Platform.Linux.Detection.UpstreamGameLibraryPolicyService.PolicyName
                ? UiLocalizationService.Choose("Manifesto do RHI upstream", "Upstream RHI manifest")
                : policy.Name;
            problems.Add(UiLocalizationService.ChooseFormat("{0}: não aplicado ({1})", "{0}: not applied ({1})", name, policy.Error));
        }

        var text = UiLocalizationService.ChooseFormat("{0} · Problemas: {1}", "{0} · Problems: {1}", done, string.Join("; ", problems));
        return string.IsNullOrWhiteSpace(logPath)
            ? text
            : UiLocalizationService.ChooseFormat("{0} · Detalhes em {1}", "{0} · Details in {1}", text, logPath);
    }
}
