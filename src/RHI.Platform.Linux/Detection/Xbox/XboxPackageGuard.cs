using System.Text.Json;
using RHI.Core.Models;

namespace RHI.Platform.Linux.Detection.Xbox;

/// <summary>
/// Refuses any RHI change to an Xbox / Game Pass game while PartingX has a job queued or running for it (Refs #90):
/// an install, update or repair re-fetches package files under RHI's feet, Prepare rewrites the prefix, a running
/// game has its files open. Read from PartingX's <c>jobs.json</c> <c>queue</c> (finished jobs move to <c>history</c>).
/// </summary>
public static class XboxPackageGuard
{
    /// <summary>The refusal while PartingX has a job for the game (the job itself is written to the log).</summary>
    public const string BusyMessage = "PartingX is working on this game (installing, updating, preparing or running it); RHI changes nothing until it has finished.";

    /// <summary>Fixed start of the refusal when the job list cannot be read; its path follows.</summary>
    public const string UnreadablePrefix = "PartingX's job list could not be read, so RHI cannot tell whether PartingX is changing this game. File: ";

    /// <summary>The refusal for changing <paramref name="game"/> now; null when nothing stands in the way.</summary>
    public static string? Refusal(DetectedGame game)
    {
        if (game.XboxPackage is not { JobsFile: { } jobs, StoreId: { } storeId }) return null;
        if (!File.Exists(jobs)) return null;
        try
        {
            using var doc = JsonDocument.Parse(PartingXInstance.ReadSmall(jobs, 16 * 1024 * 1024));
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("queue", out var queue) || queue.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var job in queue.EnumerateArray())
            {
                if (!string.Equals(PartingXInstance.String(job, "titleId"), storeId, StringComparison.OrdinalIgnoreCase)) continue;
                RHI.Core.Services.AppLog.Warn("deploy",
                    $"PartingX job {PartingXInstance.String(job, "kind")} ({PartingXInstance.String(job, "state")}) for {storeId}: RHI changes refused");
                return BusyMessage;
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            RHI.Core.Services.AppLog.Warn("deploy", $"PartingX job list {jobs} unreadable", ex);
            return UnreadablePrefix + jobs;
        }
    }
}
