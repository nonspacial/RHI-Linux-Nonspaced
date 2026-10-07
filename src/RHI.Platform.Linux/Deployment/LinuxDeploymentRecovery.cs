using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

/// <summary>Journal step that undoes a Wine DLL override (<see cref="WineDllOverrideService.Restore"/> is idempotent).</summary>
public sealed class WineOverrideCompensator(WineDllOverrideService overrides) : IDeploymentCompensator
{
    public const string KindName = "wine-dll-override";

    public string Kind => KindName;

    public static string Describe(string prefixPath, WineOverrideRecord record)
        => JsonSerializer.Serialize(new Payload { PrefixPath = prefixPath, Record = record });

    public void Compensate(string data)
    {
        var payload = JsonSerializer.Deserialize<Payload>(data) ?? throw new InvalidDataException("Empty Wine override journal step.");
        if (payload.Record is null || string.IsNullOrWhiteSpace(payload.PrefixPath))
            throw new InvalidDataException("Incomplete Wine override journal step.");
        if (!Directory.Exists(payload.PrefixPath)) return; // prefix deleted since: nothing left to undo
        overrides.Restore(payload.PrefixPath, payload.Record);
    }

    private sealed class Payload
    {
        public string PrefixPath { get; set; } = "";
        public WineOverrideRecord? Record { get; set; }
    }
}

/// <summary>
/// Start-up recovery for deployments (#23): rolls back operations a crash interrupted, using the
/// deployment journal, then sweeps transaction leftovers from the directories RHI has records for.
/// An operation counts as committed when its record (the commit point) carries its operation id.
/// </summary>
public static class LinuxDeploymentRecovery
{
    /// <summary>Record metadata key holding the id of the journaled operation that wrote the record.</summary>
    public const string OperationIdMetadataKey = "Rhi.OperationId";

    /// <summary>Leftovers younger than this may belong to a running operation of another RHI process.</summary>
    public static readonly TimeSpan SweepMinimumAge = TimeSpan.FromHours(6);

    public static string JournalDirectory => Path.Combine(LinuxPaths.DataHome, "rhi-linux", "journal");

    public static string QuarantineDirectory => Path.Combine(LinuxPaths.DataHome, "rhi-linux", "recovered");

    public static DeploymentJournal CreateJournal(WineDllOverrideService overrides, string? directory = null)
        => new(directory ?? JournalDirectory, [new WineOverrideCompensator(overrides)]);

    /// <summary>Commit probe for an operation whose commit point is saving <paramref name="component"/>'s record.</summary>
    public static string RecordCommitProbe(DetectedGame game, ManagedComponentKind component)
        => $"record|{component}|{LinuxDeploymentRecordStore.GameKey(game)}";

    /// <summary>
    /// Rolls back interrupted operations. Deferred (returns null, touches nothing) while the ledger is
    /// unreadable: without it a committed operation cannot be told from an interrupted one.
    /// </summary>
    public static async Task<JournalRecoveryReport?> RecoverAsync(DeploymentJournal journal, LinuxDeploymentRecordStore records)
    {
        if (records.CorruptLedgerPath is not null) return null;
        return await journal.RecoverAsync(entry => IsCommitted(entry, records)).ConfigureAwait(false);
    }

    public static LeftoverSweepReport Sweep(
        DeploymentJournal journal,
        LinuxDeploymentRecordStore records,
        IEnumerable<string>? extraDirectories = null,
        string? quarantineRoot = null,
        TimeSpan? minimumAge = null)
    {
        if (records.CorruptLedgerPath is not null)
            return new LeftoverSweepReport([], [], ["The deployment ledger is unreadable; the leftover sweep was skipped."]);
        var directories = new List<string>();
        foreach (var record in records.All())
        {
            directories.Add(record.BinaryDirectory);
            directories.AddRange(record.Files.Select(file => Path.GetDirectoryName(file.Path) ?? ""));
        }
        if (extraDirectories is not null) directories.AddRange(extraDirectories);
        return DeploymentLeftoverSweeper.Sweep(
            directories,
            journal.ReferencedOperationIds(),
            quarantineRoot ?? QuarantineDirectory,
            minimumAge ?? SweepMinimumAge);
    }

    public static string LogPath => Path.Combine(LinuxPaths.DataHome, "rhi-linux", "recovery.log");

    /// <summary>
    /// Appends what recovery and the sweep did to <paramref name="logPath"/> (default <see cref="LogPath"/>)
    /// and returns the counts the UI reports. Nothing is written when there is nothing to say.
    /// </summary>
    public static DeploymentRecoverySummary Record(
        JournalRecoveryReport? recovery,
        int deferredEntries,
        LeftoverSweepReport? sweep,
        string? logPath = null)
    {
        logPath ??= LogPath;
        var lines = new List<string>();
        foreach (var entry in recovery?.Entries ?? [])
        {
            lines.Add($"{entry.Outcome}: {entry.Description} [{entry.Id}] {string.Join(", ", entry.Paths)}");
            lines.AddRange(entry.Problems.Select(problem => "  " + problem));
        }
        if (deferredEntries > 0)
            lines.Add($"Deferred: {deferredEntries} interrupted operation(s) wait until the deployment ledger is readable again.");
        foreach (var path in sweep?.Deleted ?? []) lines.Add($"Removed leftover: {path}");
        foreach (var (from, to) in sweep?.Quarantined ?? []) lines.Add($"Moved leftover: {from} -> {to}");
        foreach (var problem in sweep?.Problems ?? []) lines.Add($"Sweep problem: {problem}");

        if (lines.Count > 0)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.AppendAllLines(logPath, lines.Select(line => $"{DateTime.UtcNow:O} {line}"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The notice still goes out; it points at the reason the details are missing.
                logPath = $"{logPath} (could not be written: {ex.Message})";
            }
        }
        return new DeploymentRecoverySummary(
            recovery?.RolledBack ?? 0,
            recovery?.Failed ?? 0,
            deferredEntries,
            sweep?.Quarantined.Count ?? 0,
            logPath);
    }

    private static bool IsCommitted(JournalEntryInfo entry, LinuxDeploymentRecordStore records)
    {
        if (entry.CommitProbe is not { } probe || !probe.StartsWith("record|", StringComparison.Ordinal)) return false;
        var parts = probe.Split('|', 3);
        if (parts.Length != 3 || !Enum.TryParse<ManagedComponentKind>(parts[1], out var component)) return false;
        return records.All().Any(record =>
            record.GameKey == parts[2] &&
            record.Component == component &&
            record.Metadata.TryGetValue(OperationIdMetadataKey, out var id) &&
            id == entry.Id);
    }
}

/// <summary>Counts shown to the user after start-up recovery; details are in <see cref="LogPath"/>.</summary>
public sealed record DeploymentRecoverySummary(int RolledBack, int RollbackFailed, int Deferred, int Quarantined, string LogPath)
{
    public bool NeedsNotice => RolledBack + RollbackFailed + Deferred + Quarantined > 0;
}
