using System.Diagnostics;

namespace RHI.Core.Services;

/// <summary>
/// Lets a detector report a problem it recovered from (one unreadable Steam manifest, a damaged
/// <c>pga.db</c>, a Lutris config that failed to parse) without failing the whole scan. Reports are
/// written as trace warnings and collected per async flow, so tests and the scanner can read them
/// without extra parameters through the detector code.
/// </summary>
/// <remarks>
/// Same surface as w1/policy's <c>DetectorDiagnostics</c> (which also writes <c>AppLog</c>); on merge
/// keep that implementation, every call site here already matches it.
/// </remarks>
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
        Trace.TraceWarning("[RHI scan] " + text);
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
