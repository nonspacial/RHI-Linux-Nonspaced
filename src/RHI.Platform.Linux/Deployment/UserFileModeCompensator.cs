using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// #205: puts back the Unix mode a user file had before an operation (a read-only Engine.ini the user locked so the game
/// cannot rewrite it, or the lock RHI adds with RenoDX's Engine.ini keys, #217). The journal's file rollback restores a
/// tracked file's bytes from a fresh copy, which comes back with the default mode: without this step a rollback, in
/// process or at the next start, would unlock a file the user had locked. Journaled before the file changes; runs after
/// the file rollback (compensations run after files), and only while the file holds its pre-operation bytes again — a
/// file someone else changed after the interruption keeps whatever mode it has. Idempotent.
/// </summary>
public sealed class UserFileModeCompensator : IDeploymentCompensator
{
    public const string KindName = "user-file-mode";

    public string Kind => KindName;

    /// <summary>The journal step for <paramref name="path"/> as it is now; add it before the file changes.</summary>
    public static string? Describe(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path)) return null; // a new file is deleted by the file rollback
        return JsonSerializer.Serialize(new Step
        {
            Path = Path.GetFullPath(path),
            Mode = (int)File.GetUnixFileMode(path),
            Sha256 = FileHash.Sha256Hex(path)
        });
    }

    public CompensationResult Compensate(string data)
    {
        var step = JsonSerializer.Deserialize<Step>(data) ?? throw new InvalidDataException("user-file-mode step without data");
        if (string.IsNullOrWhiteSpace(step.Path) || !Path.IsPathFullyQualified(step.Path) || string.IsNullOrWhiteSpace(step.Sha256))
            throw new InvalidDataException("user-file-mode step without a full path or hash");
        Restore(step.Path, (UnixFileMode)step.Mode, step.Sha256);
        return CompensationResult.Done;
    }

    /// <summary>The step itself (also what an unjournaled scope runs after its in-process rollback).</summary>
    internal static void Restore(string path, UnixFileMode mode, string sha256)
    {
        if (OperatingSystem.IsWindows()) return;
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null) return;
        if (File.GetUnixFileMode(path) == mode) return;
        // Someone else's content (kept by crash recovery) keeps its own mode.
        if (!FileHash.HexEquals(FileHash.Sha256Hex(path), sha256)) return;
        File.SetUnixFileMode(path, mode);
    }

    internal static void RestoreDescribed(string data)
    {
        var step = JsonSerializer.Deserialize<Step>(data);
        if (step is { Path.Length: > 0, Sha256.Length: > 0 }) Restore(step.Path, (UnixFileMode)step.Mode, step.Sha256);
    }

    private sealed class Step
    {
        public string Path { get; set; } = "";
        public int Mode { get; set; }
        public string Sha256 { get; set; } = "";
    }
}

/// <summary>
/// #205: the user INI files one RenoDX operation edits in place (ReShade.ini under the one ReShade.ini link rule,
/// Engine.ini under its own link policy), tracked at the file a write through the path replaces — never a link — with
/// Engine.ini's Unix mode journaled (<see cref="UserFileModeCompensator"/>) so a rollback keeps a user's lock.
/// Without a journal the mode steps are kept here and run by <see cref="RestoreModesAfterRollback"/>.
/// </summary>
internal sealed class RenoDxIniTracking(DeploymentScope scope)
{
    private readonly List<string> _unjournaledModeSteps = [];
    private readonly HashSet<string> _tracked = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Tracked => _tracked;

    /// <summary>Tracks the ReShade.ini a RenoDX write to <paramref name="iniPath"/> replaces (the link's allowed target).</summary>
    public async Task<string> TrackReShadeIniAsync(string iniPath, string binaryDirectory, string? gameRoot, CancellationToken cancellationToken)
    {
        var target = ReShadeIniManager.ResolveWriteTarget(iniPath, binaryDirectory, gameRoot);
        if (_tracked.Add(Path.GetFullPath(target))) await scope.TrackAsync(target, cancellationToken).ConfigureAwait(false);
        return target;
    }

    /// <summary>
    /// Tracks the Engine.ini a write to <paramref name="path"/> replaces, journals its mode first, and (journaled) the folders
    /// a write would create, so a rollback leaves no empty <c>Saved/Config/…</c> tree behind.
    /// </summary>
    public async Task<string> TrackEngineIniAsync(string path, CancellationToken cancellationToken)
    {
        var target = LumaEngineIniOverrideManager.ResolveWriteTarget(path);
        if (!_tracked.Add(Path.GetFullPath(target))) return target;
        if (UserFileModeCompensator.Describe(target) is { } mode)
        {
            if (scope.Operation is { } operation)
                await operation.AddCompensationAsync(UserFileModeCompensator.KindName, mode, cancellationToken).ConfigureAwait(false);
            else _unjournaledModeSteps.Add(mode);
        }
        if (scope.Operation is { } journaled)
        {
            var missing = new List<string>();
            for (var dir = Path.GetDirectoryName(Path.GetFullPath(target)); !string.IsNullOrEmpty(dir) && !Directory.Exists(dir); dir = Path.GetDirectoryName(dir))
                missing.Add(dir);
            missing.Reverse(); // outermost first: compensations run in reverse, so the innermost folder goes first
            foreach (var dir in missing)
                await journaled.AddCompensationAsync(CreatedDirectoryCompensator.KindName, CreatedDirectoryCompensator.Describe(dir), cancellationToken).ConfigureAwait(false);
        }
        await scope.TrackAsync(target, cancellationToken).ConfigureAwait(false);
        return target;
    }

    /// <summary>Declares what every tracked INI now holds as this operation's (straight after the in-place edits).</summary>
    public async Task DeclareWrittenAsync(CancellationToken cancellationToken)
    {
        foreach (var path in _tracked) await scope.DeclareWrittenContentAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>After an unjournaled scope's rollback: the modes back (a journaled scope's rollback already ran them).</summary>
    public void RestoreModesAfterRollback()
    {
        if (scope.Operation is not null) return;
        foreach (var step in _unjournaledModeSteps)
        {
            try { UserFileModeCompensator.RestoreDescribed(step); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("renodx-rollback", "Could not put a file's earlier mode back", ex);
            }
        }
    }
}
