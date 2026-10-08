using System.Text;
using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// Journal step that undoes a Wine DLL override. It only undoes what RHI set: when the override no
/// longer holds RHI's <c>native,builtin</c>, someone changed it after the crash and their value is
/// kept. While a wineserver runs for the prefix the undo is deferred, because wine rewrites
/// <c>user.reg</c> from memory when it exits and would silently drop it.
/// </summary>
public sealed class WineOverrideCompensator(WineDllOverrideService overrides, Func<string, bool>? isPrefixInUse = null) : IDeploymentCompensator
{
    public const string KindName = "wine-dll-override";
    private const string RhiValue = "native,builtin";

    private readonly Func<string, bool> _isPrefixInUse = isPrefixInUse ?? (prefix => WineServerProbe.IsRunningFor(prefix));

    public string Kind => KindName;

    public static string Describe(string prefixPath, WineOverrideRecord record)
        => JsonSerializer.Serialize(new Payload { PrefixPath = prefixPath, Record = record });

    public CompensationResult Compensate(string data)
    {
        var payload = JsonSerializer.Deserialize<Payload>(data) ?? throw new InvalidDataException("Empty Wine override journal step.");
        if (payload.Record is not { } record || string.IsNullOrWhiteSpace(payload.PrefixPath))
            throw new InvalidDataException("Incomplete Wine override journal step.");
        var prefix = payload.PrefixPath;
        if (!record.ChangedByRhi || !Directory.Exists(prefix)) return CompensationResult.Done; // nothing RHI set, or prefix deleted since
        var registry = Path.Combine(prefix, "user.reg");
        if (!File.Exists(registry)) return CompensationResult.Done;
        if (_isPrefixInUse(prefix))
            return new(CompensationStatus.Deferred,
                $"The {record.DllName} override in {prefix} is undone at the next start: Wine is running in that prefix and would overwrite the change when it exits.");

        var current = WineDllOverrideService.TryReadOverride(File.ReadAllText(registry), record.DllName, record.ApplicationName);
        if (string.Equals(current, record.PreviousValue, StringComparison.OrdinalIgnoreCase))
            return CompensationResult.Done; // never written, or already undone
        if (!string.Equals(current, RhiValue, StringComparison.OrdinalIgnoreCase))
            return new(CompensationStatus.KeptLaterChange,
                $"The {record.DllName} override in {prefix} was changed to '{current ?? "(removed)"}' after the interruption; it was left as it is.");
        overrides.Restore(prefix, record);
        return CompensationResult.Done;
    }

    private sealed class Payload
    {
        public string PrefixPath { get; set; } = "";
        public WineOverrideRecord? Record { get; set; }
    }
}

/// <summary>Finds a running wineserver for a Wine prefix from <c>/proc</c> (its <c>WINEPREFIX</c>, default <c>~/.wine</c>).</summary>
public static class WineServerProbe
{
    public static bool IsRunningFor(string prefixPath, string procRoot = "/proc")
    {
        var wanted = Normalise(prefixPath);
        IEnumerable<string> processes;
        try
        {
            processes = Directory.EnumerateDirectories(procRoot).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        foreach (var process in processes)
        {
            if (!int.TryParse(Path.GetFileName(process), out _)) continue;
            try
            {
                var comm = File.ReadAllText(Path.Combine(process, "comm")).Trim();
                if (!comm.StartsWith("wineserver", StringComparison.Ordinal)) continue;
                var environment = File.ReadAllText(Path.Combine(process, "environ")).Split('\0');
                string? Variable(string name) => environment.FirstOrDefault(entry => entry.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];
                var prefix = Variable("WINEPREFIX") is { Length: > 0 } explicitPrefix
                    ? explicitPrefix
                    : Variable("HOME") is { Length: > 0 } home ? Path.Combine(home, ".wine") : null;
                if (prefix is not null && string.Equals(Normalise(prefix), wanted, StringComparison.Ordinal)) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // exited meanwhile, or another user's process
            }
        }
        return false;
    }

    private static string Normalise(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        try
        {
            // compatdata folders are often reached through a symlinked library path
            if (new DirectoryInfo(full).ResolveLinkTarget(returnFinalTarget: true) is { } target)
                full = Path.TrimEndingDirectorySeparator(target.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return full;
    }
}

/// <summary>
/// Start-up recovery for deployments (#23): rolls back operations a crash interrupted, using the
/// deployment journal, then sweeps transaction leftovers from the directories RHI has records for.
/// Both run before any deployment service exists (<see cref="RunAtStartupAsync"/>), and the sweep
/// only while no other RHI process runs. An operation counts as committed when a record (the commit
/// point) carries its operation id.
/// </summary>
public static class LinuxDeploymentRecovery
{
    /// <summary>Record metadata key holding the id of the journaled operation that wrote the record.</summary>
    public const string OperationIdMetadataKey = "Rhi.OperationId";

    /// <summary>
    /// Leftovers whose inode changed more recently than this are kept (second line of defence behind
    /// the instance gate: an older build's operation may still be finishing).
    /// </summary>
    public static readonly TimeSpan SweepMinimumAge = TimeSpan.FromHours(6);

    /// <summary>How long a starting instance waits for another instance's start-up sweep.</summary>
    public static readonly TimeSpan StartupLockTimeout = TimeSpan.FromMinutes(2);

    private const long LogRotateBytes = 1024 * 1024;

    public static string JournalDirectory => Path.Combine(LinuxPaths.DataHome, "rhi-linux", "journal");

    /// <summary>
    /// Instance registrations live next to the journal and ledger they protect: two processes share
    /// deployments exactly when they share this directory.
    /// </summary>
    public static string InstanceDirectory => Path.Combine(LinuxPaths.DataHome, "rhi-linux", "instances");

    public static string LogPath => Path.Combine(LinuxPaths.LogDirectory, "recovery.log");

    public static DeploymentJournal CreateJournal(WineDllOverrideService overrides, string? directory = null, Func<string, bool>? isPrefixInUse = null)
        => new(directory ?? JournalDirectory, [new WineOverrideCompensator(overrides, isPrefixInUse)]);

    /// <summary>Commit probe for an operation whose commit point is saving <paramref name="component"/>'s record.</summary>
    public static string RecordCommitProbe(DetectedGame game, ManagedComponentKind component)
        => $"record|{component}|{LinuxDeploymentRecordStore.GameKey(game)}";

    /// <summary>
    /// Everything start-up does for deployments, in order, under the instance gate's start-up lock:
    /// journal recovery, then the leftover sweep unless another RHI instance is alive. Must finish
    /// before anything can deploy. Never throws: a failure is reported, and the app still starts.
    /// Without a <paramref name="gate"/> (registration failed) the sweep is skipped.
    /// <para>Lock wait: a second instance blocks here (no window yet, UI thread waiting) for up to
    /// <see cref="StartupLockTimeout"/> while the first one recovers and sweeps. If it times out it skips its own
    /// sweep and starts its services while the first instance's sweep may still be running. That is safe: the
    /// sweep asks the journal per file (<c>IsReferenced</c>, including the operation lock file), so a journaled
    /// operation the second instance starts is never swept, and legacy leftovers are only taken when their
    /// change time is older than the sweep's minimum age (6 h), which no fresh temp file can be.</para>
    /// </summary>
    public static async Task<DeploymentStartupReport> RunAtStartupAsync(
        DeploymentJournal journal,
        LinuxDeploymentRecordStore records,
        DeploymentInstanceGate? gate,
        TimeSpan? startupLockTimeout = null,
        TimeSpan? sweepMinimumAge = null)
    {
        JournalRecoveryReport? recovery = null;
        var deferred = 0;
        LeftoverSweepReport sweep;
        var errors = new List<string>();
        using var startupLock = gate?.AcquireStartupLock(startupLockTimeout ?? StartupLockTimeout);
        try
        {
            recovery = await RecoverAsync(journal, records).ConfigureAwait(false);
            if (recovery is null) deferred = journal.ReferencedOperationIds().Count;
        }
        catch (Exception ex)
        {
            errors.Add($"Recovery of interrupted deployments failed: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            if (gate is null)
                sweep = LeftoverSweepReport.Skipped("this instance could not register, so other instances cannot be ruled out");
            else if (startupLock is null)
                sweep = LeftoverSweepReport.Skipped("another RHI instance is still starting up");
            else if (gate.OtherInstanceRunning())
                sweep = LeftoverSweepReport.Skipped("another RHI instance is running and may be deploying");
            else
                sweep = Sweep(journal, records, minimumAge: sweepMinimumAge);
        }
        catch (Exception ex)
        {
            sweep = new LeftoverSweepReport([], [], [$"{ex.GetType().Name}: {ex.Message}"]);
        }
        return new DeploymentStartupReport(recovery, deferred, sweep, errors);
    }

    /// <summary>
    /// Rolls back interrupted operations. Deferred (returns null, touches nothing) while the ledger is
    /// unreadable: without it a committed operation cannot be told from an interrupted one.
    /// </summary>
    public static async Task<JournalRecoveryReport?> RecoverAsync(DeploymentJournal journal, LinuxDeploymentRecordStore records)
    {
        if (records.CorruptLedgerPath is not null) return null;
        return await journal.RecoverAsync(entry => IsCommitted(entry, records)).ConfigureAwait(false);
    }

    /// <summary>Sweeps the recorded directories and the journal's own directory (its temporaries).</summary>
    public static LeftoverSweepReport Sweep(
        DeploymentJournal journal,
        LinuxDeploymentRecordStore records,
        IEnumerable<string>? extraDirectories = null,
        TimeSpan? minimumAge = null,
        DateTime? nowUtc = null)
    {
        if (records.CorruptLedgerPath is not null)
            return new LeftoverSweepReport([], [], ["The deployment ledger is unreadable; the leftover sweep was skipped."]);
        var directories = new List<string> { journal.Directory };
        foreach (var record in records.All())
        {
            directories.Add(record.BinaryDirectory);
            directories.AddRange(record.Files.Select(file => Path.GetDirectoryName(file.Path) ?? ""));
        }
        if (extraDirectories is not null) directories.AddRange(extraDirectories);
        return DeploymentLeftoverSweeper.Sweep(directories, journal.IsReferenced, minimumAge ?? SweepMinimumAge, nowUtc);
    }

    /// <summary>
    /// Appends what start-up recovery and the sweep did to <paramref name="logPath"/> (default
    /// <see cref="LogPath"/>, rotated to <c>.1</c> at 1 MiB, fsynced), sends problems to
    /// <see cref="AppLog"/> as well, and returns the counts the UI reports.
    /// </summary>
    public static DeploymentRecoverySummary Record(DeploymentStartupReport startup, string? logPath = null)
    {
        logPath ??= LogPath;
        var recovery = startup.Recovery;
        var sweep = startup.Sweep;
        var lines = new List<string>();
        var problems = new List<string>(startup.Errors);
        foreach (var entry in recovery?.Entries ?? [])
        {
            lines.Add($"{entry.Outcome}: {entry.Description} [{entry.Id}] {string.Join(", ", entry.Paths)}");
            lines.AddRange(entry.Kept.Select(kept => "  kept: " + kept));
            lines.AddRange(entry.Problems.Select(problem => "  " + problem));
            if (entry.Outcome is JournalRecoveryOutcome.RollbackFailed or JournalRecoveryOutcome.Unreadable)
                problems.AddRange(entry.Problems.Select(problem => $"{entry.Description}: {problem}"));
        }
        if (startup.DeferredEntries > 0)
            lines.Add($"Deferred: {startup.DeferredEntries} interrupted operation(s) wait until the deployment ledger is readable again.");
        foreach (var path in sweep?.Deleted ?? []) lines.Add($"Removed leftover: {path}");
        foreach (var (from, to) in sweep?.Quarantined ?? []) lines.Add($"Moved leftover: {from} -> {to}");
        foreach (var problem in sweep?.Problems ?? []) problems.Add($"Sweep: {problem}");
        if (sweep?.SkippedReason is { } skipped) lines.Add($"Leftover sweep skipped: {skipped}.");
        lines.AddRange(problems.Select(problem => "Problem: " + problem));
        foreach (var problem in problems) AppLog.Warn("deployment-recovery", problem);

        if (lines.Count > 0)
        {
            try
            {
                AppendDurably(logPath, lines.Select(line => $"{DateTime.UtcNow:O} {line}"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("deployment-recovery", $"Could not write {logPath}", ex);
                // The notice still goes out; it points at the reason the details are missing.
                logPath = $"{logPath} (could not be written: {ex.Message})";
            }
        }
        return new DeploymentRecoverySummary(
            recovery?.RolledBack ?? 0,
            (recovery?.Failed ?? 0) + startup.Errors.Count,
            startup.DeferredEntries + (recovery?.Deferred ?? 0),
            sweep?.Quarantined.Count ?? 0,
            logPath,
            recovery?.KeptLaterChanges ?? 0);
    }

    private static void AppendDurably(string path, IEnumerable<string> lines)
    {
        AtomicFile.CreateDirectoryDurable(Path.GetDirectoryName(path)!);
        var info = new FileInfo(path);
        if (info.Exists && info.Length > LogRotateBytes) AtomicFile.Rename(path, path + ".1", overwrite: true);
        var created = !File.Exists(path);
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            var bytes = Encoding.UTF8.GetBytes(string.Concat(lines.Select(line => line + Environment.NewLine)));
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        if (created) AtomicFile.FsyncDirectoryOf(path);
    }

    /// <summary>
    /// Committed when any record carries the operation's id (ids are unique). Not matched on the game
    /// key: a record re-keyed (#38) or rebased by "Repair after move" since must still count.
    /// </summary>
    private static bool IsCommitted(JournalEntryInfo entry, LinuxDeploymentRecordStore records)
    {
        if (entry.CommitProbe is not { } probe || !probe.StartsWith("record|", StringComparison.Ordinal)) return false;
        return records.All().Any(record =>
            record.Metadata.TryGetValue(OperationIdMetadataKey, out var id) &&
            id == entry.Id);
    }
}

/// <summary>What <see cref="LinuxDeploymentRecovery.RunAtStartupAsync"/> did.</summary>
public sealed record DeploymentStartupReport(
    JournalRecoveryReport? Recovery,
    int DeferredEntries,
    LeftoverSweepReport? Sweep,
    IReadOnlyList<string> Errors);

/// <summary>Counts shown to the user after start-up recovery; details are in <see cref="LogPath"/>.</summary>
public sealed record DeploymentRecoverySummary(int RolledBack, int RollbackFailed, int Deferred, int Quarantined, string LogPath, int KeptLaterChanges = 0)
{
    public bool NeedsNotice => RolledBack + RollbackFailed + Deferred + Quarantined + KeptLaterChanges > 0;
}
