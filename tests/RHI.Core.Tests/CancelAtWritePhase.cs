using System.Security.Cryptography;
using RHI.Core.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// Shared harness for "Cancel must never break a rollback" (w2/vmsplit review, finding 1). Runs an operation with a
/// token whose Cancel is pressed the moment the operation enters its write phase (<see cref="OperationCancellation.BeginWrites"/>)
/// and is honoured anyway, as a caller's plain token would be: a service that kept listening to its token after its
/// first change stops half-way, or rolls back with a cancelled token, and the caller's assertions catch it. Also checks
/// that the write phase was entered and that nothing under the watched folders changed before it (Cancel is honoured
/// only before the first game file is written).
/// </summary>
internal static class CancelAtWritePhase
{
    public sealed record Outcome<T>(T Result, IReadOnlyDictionary<string, string> Before);

    /// <param name="atWritePhase">Runs right after Cancel is pressed (e.g. make the record save fail to force the rollback).</param>
    public static async Task<Outcome<T>> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        IEnumerable<string> watched,
        Action? atWritePhase = null)
    {
        var dirs = watched.ToArray();
        var before = Snapshot(dirs);
        using var cancel = new OperationCancellation { HonourLateCancelForTesting = true };
        var entered = 0;
        var accepted = false;
        string? changedEarly = null;
        // No asserts in here: the callback runs inside the service, which may swallow what it throws.
        cancel.WritesStarting += () =>
        {
            entered++;
            changedEarly = Describe(Diff(before, Snapshot(dirs)));
            accepted = cancel.TryCancel();
            atWritePhase?.Invoke();
        };

        var result = await operation(cancel.Token);

        Assert.True(entered == 1, $"The operation entered its write phase {entered} time(s); expected once.");
        Assert.True(changedEarly is null, $"Changed before the write phase began (Cancel would have broken it): {changedEarly}");
        Assert.True(accepted && cancel.Token.IsCancellationRequested);
        return new(result, before);
    }

    /// <summary>Every file under <paramref name="dirs"/> (recursively) with its SHA-256.</summary>
    public static IReadOnlyDictionary<string, string> Snapshot(params string[] dirs)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var dir in dirs.Where(Directory.Exists))
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                map[file] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        return map;
    }

    /// <summary>Asserts the folders hold exactly the files (and bytes) they held before the operation.</summary>
    public static void AssertUnchanged(IReadOnlyDictionary<string, string> before, params string[] dirs)
    {
        var diff = Describe(Diff(before, Snapshot(dirs)));
        Assert.True(diff is null, $"Not rolled back: {diff}");
    }

    private static IEnumerable<string> Diff(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after)
        => before.Keys.Union(after.Keys).Order(StringComparer.Ordinal).Where(path =>
            !before.TryGetValue(path, out var a) || !after.TryGetValue(path, out var b) || a != b)
            .Select(path => !before.ContainsKey(path) ? $"+{path}" : !after.ContainsKey(path) ? $"-{path}" : $"~{path}");

    private static string? Describe(IEnumerable<string> diff)
    {
        var list = diff.ToList();
        return list.Count == 0 ? null : string.Join(", ", list);
    }
}
