namespace RHI.Core.Services;

/// <summary>
/// Lets a detector report a problem it recovered from (one unreadable Steam library, a Lutris config
/// that failed to parse) without failing the whole scan. The scanner collects the reports per detector
/// into <see cref="DetectorScanStatus.Warnings"/>; every report is also written to <see cref="AppLog"/>.
/// Reports flow through the async context, so detector code needs no extra parameters.
/// </summary>
public static class DetectorDiagnostics
{
    private static readonly AsyncLocal<List<string>?> Collector = new();

    public static void Warn(string message, Exception? error = null)
    {
        var text = error is null ? message : $"{message}: {error.Message}";
        var collector = Collector.Value;
        if (collector is not null)
        {
            lock (collector) collector.Add(text);
        }
        AppLog.Warn("scan", message, error);
    }

    /// <summary>Runs <paramref name="detect"/> with a fresh collector and returns what it reported.</summary>
    internal static async Task<(T Result, IReadOnlyList<string> Warnings)> CollectAsync<T>(Func<Task<T>> detect)
    {
        var collector = new List<string>();
        Collector.Value = collector;
        var result = await detect().ConfigureAwait(false);
        lock (collector) return (result, collector.ToArray());
    }
}

/// <summary>Outcome of one detector in a library scan.</summary>
/// <param name="Name">Detector name (Steam, Heroic, Lutris, Wine, Manual).</param>
/// <param name="Count">Entries the detector returned.</param>
/// <param name="Error">Set when the detector failed outright; its games are missing from the library.</param>
/// <param name="Warnings">Problems the detector recovered from (entries or libraries it had to skip).</param>
/// <param name="Reused">True when a partial refresh reused the previous scan's result and status.</param>
public sealed record DetectorScanStatus(string Name, int Count, string? Error, IReadOnlyList<string> Warnings, bool Reused = false)
{
    public bool Failed => Error is not null;
}

/// <summary>A library-policy stage (upstream manifest) that could not be applied; the local library stays as detected.</summary>
public sealed record PolicyScanStatus(string Name, string Error);

/// <summary>Library scan output: the merged games plus what each detector and policy stage reported.</summary>
public sealed record GameLibraryScanResult(
    IReadOnlyList<RHI.Core.Models.DetectedGame> Games,
    IReadOnlyList<DetectorScanStatus> Detectors,
    IReadOnlyList<PolicyScanStatus> PolicyFailures)
{
    public bool HasProblems => PolicyFailures.Count > 0 || Detectors.Any(d => d.Failed || d.Warnings.Count > 0);
}
