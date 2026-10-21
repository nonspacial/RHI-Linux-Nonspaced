using System.Globalization;
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
/// <para>The hive is read first (no stopped Wine needed for a read): a step whose override is at its earlier value
/// was never written (the edit was refused) or is already undone, so it is done at once, never left to undo a later
/// install's override. An override a record committed after this step was journaled now depends on (same game and
/// override; the user installed again while this undo waited) is left in place, and when that record did not own it
/// (it found RHI's value already there) it takes over this step's ownership, so its removal restores the value the
/// override had before either install.</para>
/// </summary>
public sealed class WineOverrideCompensator(
    WineDllOverrideService overrides, Func<string, bool>? isPrefixInUse = null, LinuxDeploymentRecordStore? records = null) : IDeploymentCompensator
{
    public const string KindName = "wine-dll-override";
    private readonly Func<string, bool> _isPrefixInUse = isPrefixInUse ?? overrides.Registry.IsPrefixInUse;

    public string Kind => KindName;

    /// <param name="gameKey">The record key of the operation's game (from its commit probe), for the later-record check.</param>
    /// <param name="journaledUtc">When the step was journaled: only a record installed after it can supersede it.</param>
    public static string Describe(string prefixPath, WineOverrideRecord record, string? gameKey = null, DateTime? journaledUtc = null)
        => JsonSerializer.Serialize(new Payload { PrefixPath = prefixPath, Record = record, GameKey = gameKey, JournaledUtc = journaledUtc });

    public CompensationResult Compensate(string data)
    {
        var payload = JsonSerializer.Deserialize<Payload>(data) ?? throw new InvalidDataException("Empty Wine override journal step.");
        if (payload.Record is not { } record || string.IsNullOrWhiteSpace(payload.PrefixPath))
            throw new InvalidDataException("Incomplete Wine override journal step.");
        var prefix = payload.PrefixPath;
        if (!record.ChangedByRhi || !Directory.Exists(prefix)) return CompensationResult.Done; // nothing RHI set, or prefix deleted since
        if (!WineRegistryEditor.HasUserHive(prefix)) return CompensationResult.Done;

        var current = WineDllOverrideService.CurrentValue(prefix, record);
        // Never written (refused edit) or already undone: nothing to do, whether or not Wine runs now.
        if (WineDllOverrideService.IsPreviousValue(current, record)) return CompensationResult.Done;
        if (!WineDllOverrideService.SameValue(current, WineDllOverrideService.WrittenBy(record)))
            return KeptLaterValue(record, prefix, current?.Text);
        if (SupersededByLaterRecord(payload, record)) return CompensationResult.Done;
        if (_isPrefixInUse(prefix)) return DeferredWhileWineRuns(record, prefix);

        WineOverrideRestoreResult result;
        try
        {
            // Restore acts only while the override holds RHI's value; a later value is kept (#30).
            result = overrides.Restore(prefix, record);
        }
        catch (WineRegistryEditRefusedException ex) when (ex.PrefixInUse)
        {
            return DeferredWhileWineRuns(record, prefix);
        }
        return result.Outcome == WineOverrideRestoreOutcome.KeptLaterValue
            ? KeptLaterValue(record, prefix, result.CurrentValue)
            : CompensationResult.Done;
    }

    private static CompensationResult KeptLaterValue(WineOverrideRecord record, string prefix, string? current)
        => new(CompensationStatus.KeptLaterChange,
            $"This Wine override was changed after the interruption and was left as it is: {record.DllName} ({prefix}) = '{current ?? "-"}'");

    /// <summary>
    /// True when a record of the step's game, saved after the step was journaled, holds the same override: the user
    /// installed again (or an operation such as the DX9 migration updated its record in place, keeping its install
    /// time) while this undo waited, and that record depends on RHI's value. "After" is the record's last save
    /// (<see cref="LinuxDeploymentRecord.SavedAtUtc"/>; install time for records saved before that existed). A record
    /// that found the value already there (<see cref="WineOverrideRecord.ChangedByRhi"/> false) takes over the step's
    /// ownership; with several, the earliest installed does, as an uninterrupted first install would have. An
    /// unreadable ledger cannot say, so the step is kept for the next start (an I/O failure).
    /// </summary>
    private bool SupersededByLaterRecord(Payload payload, WineOverrideRecord step)
    {
        if (records is null || payload.GameKey is not { Length: > 0 } gameKey || payload.JournaledUtc is not { } journaled) return false;
        if (records.CorruptLedgerPath is { } corrupt)
            throw new IOException(UnreadableLedgerPrefix + corrupt);
        var later = records.All()
            .Where(record => string.Equals(record.GameKey, gameKey, StringComparison.Ordinal) && LastSaved(record) >= journaled)
            .Select(record => (Record: record, Override: OverridesOf(record).FirstOrDefault(o => WineDllOverrideService.SameOverride(o, step))))
            .Where(claim => claim.Override is not null)
            .OrderBy(claim => claim.Record.InstalledAtUtc)
            .ToList();
        if (later.Count == 0) return false;
        if (later.All(claim => !claim.Override!.ChangedByRhi))
        {
            var (record, adopted) = later[0];
            adopted!.ChangedByRhi = true;
            adopted.PreviousValue = step.PreviousValue;
            adopted.PreviousValueIsRaw = step.PreviousValueIsRaw;
            adopted.SectionCreatedByRhi = step.SectionCreatedByRhi;
            adopted.WrittenValue = WineDllOverrideService.WrittenBy(step);
            try
            {
                records.Upsert(record);
            }
            catch (InvalidOperationException ex)
            {
                // Unreadable ledger: the journal keeps the step (an I/O failure) and the next start tries again.
                throw new IOException(ex.Message, ex);
            }
        }
        return true;
    }

    /// <summary>Fixed text of the refusal when the ledger cannot be read; its quarantine copy's path follows.</summary>
    public const string UnreadableLedgerPrefix =
        "The deployment ledger cannot be read, so this Wine override undo is kept for the next start. Ledger copy: ";

    private static DateTime LastSaved(LinuxDeploymentRecord record) => Utc(record.SavedAtUtc ?? record.InstalledAtUtc);

    private static DateTime Utc(DateTime time) => time.Kind switch
    {
        DateTimeKind.Local => time.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(time, DateTimeKind.Utc),
        _ => time
    };

    private static IEnumerable<WineOverrideRecord> OverridesOf(LinuxDeploymentRecord record)
        => record.WineOverride is { } main ? record.AdditionalWineOverrides.Prepend(main) : record.AdditionalWineOverrides;

    private static CompensationResult DeferredWhileWineRuns(WineOverrideRecord record, string prefix)
        => new(CompensationStatus.Deferred,
            $"Wine is running in this prefix and would overwrite the change when it exits, so this Wine override is undone at the next start: {record.DllName} ({prefix})");

    private sealed class Payload
    {
        public string PrefixPath { get; set; } = "";
        public WineOverrideRecord? Record { get; set; }
        public string? GameKey { get; set; }
        public DateTime? JournaledUtc { get; set; }
    }
}

/// <summary>
/// Journal step of a removal that restores a Wine DLL override (<see cref="WineDllOverrideService.RestoreAsync"/>): when
/// the removal is rolled back after a crash (its record is still in the ledger and describes the restored files again),
/// RHI's value is put back, but only while the override still holds the value the restore left; a value set after the
/// crash is kept. Deferred while a wineserver runs for the prefix, like <see cref="WineOverrideCompensator"/>.
/// </summary>
public sealed class WineOverrideReapplyCompensator(WineDllOverrideService overrides, Func<string, bool>? isPrefixInUse = null) : IDeploymentCompensator
{
    public const string KindName = "wine-dll-override-reapply";
    private readonly Func<string, bool> _isPrefixInUse = isPrefixInUse ?? overrides.Registry.IsPrefixInUse;

    public string Kind => KindName;

    public static string Describe(string prefixPath, WineOverrideRecord record)
        => JsonSerializer.Serialize(new Payload { PrefixPath = prefixPath, Record = record });

    public CompensationResult Compensate(string data)
    {
        var payload = JsonSerializer.Deserialize<Payload>(data) ?? throw new InvalidDataException("Empty Wine override journal step.");
        if (payload.Record is not { } record || string.IsNullOrWhiteSpace(payload.PrefixPath))
            throw new InvalidDataException("Incomplete Wine override journal step.");
        var prefix = payload.PrefixPath;
        if (!record.ChangedByRhi || !Directory.Exists(prefix) || !WineRegistryEditor.HasUserHive(prefix)) return CompensationResult.Done;
        var current = WineDllOverrideService.CurrentValue(prefix, record);
        // Never restored (the crash came first) or already put back: nothing to do, whether or not Wine runs now.
        if (WineDllOverrideService.SameValue(current, WineDllOverrideService.WrittenBy(record))) return CompensationResult.Done;
        if (!WineDllOverrideService.IsPreviousValue(current, record)) return KeptLaterValue(record, prefix, current?.Text);
        if (_isPrefixInUse(prefix)) return DeferredWhileWineRuns(record, prefix);
        WineOverrideRestoreResult result;
        try
        {
            result = overrides.Reapply(prefix, record);
        }
        catch (WineRegistryEditRefusedException ex) when (ex.PrefixInUse)
        {
            return DeferredWhileWineRuns(record, prefix);
        }
        return result.Outcome == WineOverrideRestoreOutcome.KeptLaterValue
            ? KeptLaterValue(record, prefix, result.CurrentValue)
            : CompensationResult.Done;
    }

    private static CompensationResult KeptLaterValue(WineOverrideRecord record, string prefix, string? current)
        => new(CompensationStatus.KeptLaterChange,
            $"This Wine override was changed after the interruption and was left as it is: {record.DllName} ({prefix}) = '{current ?? "-"}'");

    private static CompensationResult DeferredWhileWineRuns(WineOverrideRecord record, string prefix)
        => new(CompensationStatus.Deferred,
            $"{DeferredPrefix}{record.DllName} ({prefix})");

    /// <summary>Fixed text of the note when the put-back waits for Wine to exit; "dll (prefix)" follows.</summary>
    public const string DeferredPrefix =
        "Wine is running in this prefix and would overwrite the change when it exits, so this Wine override is put back at the next start: ";

    private sealed class Payload
    {
        public string PrefixPath { get; set; } = "";
        public WineOverrideRecord? Record { get; set; }
    }
}

/// <summary>
/// Finds a running wineserver for a Wine prefix from <c>/proc</c>. A wineserver matches when its <c>WINEPREFIX</c>
/// (default <c>~/.wine</c>) is the prefix's path, or when its working directory is the prefix's server directory:
/// wineserver runs in <c>/tmp/.wine-&lt;uid&gt;/server-&lt;dev&gt;-&lt;inode&gt;</c> of the prefix folder, an identity
/// that also holds for a relative <c>WINEPREFIX</c> or a prefix reached through a bind mount.
/// </summary>
public static class WineServerProbe
{
    public static bool IsRunningFor(string prefixPath, string procRoot = "/proc")
    {
        var wanted = Normalise(prefixPath);
        var serverDirectory = FileIdentity.TryGet(prefixPath) is { } identity ? ServerDirectoryName(identity) : null;
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
                if (serverDirectory is not null && string.Equals(Path.GetFileName(WorkingDirectory(process)), serverDirectory, StringComparison.Ordinal))
                    return true;
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

    /// <summary>The name of the server directory wineserver works in for a prefix folder with this identity.</summary>
    public static string ServerDirectoryName(FileIdentity prefix)
        => string.Create(CultureInfo.InvariantCulture, $"server-{prefix.Device:x}-{prefix.Inode:x}");

    private static string? WorkingDirectory(string process) => new FileInfo(Path.Combine(process, "cwd")).LinkTarget;

    // compatdata folders are often reached through a symlinked library path: resolve every component.
    private static string Normalise(string path) => PathIdentity.Canonicalize(path);
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

    /// <param name="records">
    /// The app's ledger: lets a deferred override undo see a later install that now depends on it, and (#204) is the
    /// commit point a failed operation reads again before it rolls back (<see cref="LedgerCommitCheck"/>).
    /// </param>
    public static DeploymentJournal CreateJournal(
        WineDllOverrideService overrides, string? directory = null, Func<string, bool>? isPrefixInUse = null, LinuxDeploymentRecordStore? records = null)
        => new(directory ?? JournalDirectory,
            [new WineOverrideCompensator(overrides, isPrefixInUse, records), new WineOverrideReapplyCompensator(overrides, isPrefixInUse),
             // w5/reshade-core (#92): registry values (the Vulkan implicit layer in system.reg)
             new WineRegistryValueCompensator(new WineRegistryValueService(overrides.Registry), isPrefixInUse, records),
             new WineRegistryValueReapplyCompensator(new WineRegistryValueService(overrides.Registry), isPrefixInUse),
             // w5/reshade-core (#92 review finding 3): folders an operation created (the layer's ProgramData folders)
             new CreatedDirectoryCompensator(),
             // w8/renodx hook (#205): a user file's mode (a locked Engine.ini) back after the file rollback
             new UserFileModeCompensator()],
            records is null ? null : new LedgerCommitCheck(records));

    /// <summary>Commit probe for an operation whose commit point is saving <paramref name="component"/>'s record.</summary>
    public static string RecordCommitProbe(DetectedGame game, ManagedComponentKind component)
        => $"record|{component}|{LinuxDeploymentRecordStore.GameKey(game)}";

    /// <summary>
    /// Record metadata key holding the id of the journaled removal now taking the record away (see
    /// <see cref="BeginRecordRemovalAsync"/>). A key left by a removal that was rolled back is inert: no journal entry
    /// carries that id any more, and the next removal overwrites it.
    /// </summary>
    public const string RemovalOperationIdMetadataKey = "Rhi.RemovalOperationId";

    /// <summary>Commit probe for a removal whose commit point is taking <paramref name="component"/>'s record out of the ledger.</summary>
    public static string RecordRemovalProbe(DetectedGame game, ManagedComponentKind component)
        => $"{RecordRemovedProbePrefix}{component}|{LinuxDeploymentRecordStore.GameKey(game)}";

    private const string RecordRemovedProbePrefix = "record-removed|";

    /// <summary>The game key of a <see cref="RecordCommitProbe"/> or <see cref="RecordRemovalProbe"/>, or null for any other probe.</summary>
    public static string? GameKeyOfCommitProbe(string? probe)
    {
        var parts = probe?.Split('|', 3);
        return parts is { Length: 3 } && parts[0] is "record" or "record-removed" && parts[2].Length > 0 ? parts[2] : null;
    }

    /// <summary>Fixed text of the refusal when a removal cannot be marked in the ledger; the reason follows.</summary>
    public const string RemovalUnmarkedPrefix = "The deployment ledger could not be updated before the removal (";

    /// <summary>
    /// Opens the journaled operation of a removal whose commit point is taking <paramref name="component"/>'s record out
    /// of the ledger (<see cref="LinuxDeploymentRecordStore.Remove"/>), then stamps the operation id on that record
    /// (<see cref="RemovalOperationIdMetadataKey"/>; <see cref="LinuxDeploymentRecordStore.StampRemoval"/>). Start-up
    /// recovery counts the removal committed once no record carries that id: a crash before the record went away rolls
    /// the tracked files back (the record still describes them), a crash after it keeps them removed. Call before the
    /// first change, track every file before deleting or restoring it, and <see cref="DeploymentOperation.CommitAsync"/>
    /// right after <c>Remove</c>. A removal that ends by saving a narrowed record instead calls
    /// <see cref="EndRemoval"/> on it before the save, which then is the commit point.
    /// No journal (tests, tools): no operation and no refusal, and the ledger is not touched. A journal or ledger that
    /// cannot be written refuses before anything changes (the opened operation is rolled back; it changed nothing).
    /// </summary>
    public static async Task<(DeploymentOperation? Operation, string? Refusal)> BeginRecordRemovalAsync(
        DeploymentJournal? journal, LinuxDeploymentRecordStore records, string description, DetectedGame game,
        ManagedComponentKind component, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (journal is null) return (null, null);
        DeploymentOperation operation;
        try
        {
            operation = await journal.BeginAsync(description, RecordRemovalProbe(game, component), cancellationToken).ConfigureAwait(false);
        }
        catch (DeploymentDecisionPendingException ex)
        {
            return (null, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"{JournalUnavailablePrefix}{ex.Message}); nothing was changed.");
        }
        try
        {
            records.StampRemoval(game, component, operation.Id);
            return (operation, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Nothing tracked yet: the rollback only drops the entry.
            await operation.RollbackAsync().ConfigureAwait(false);
            return (null, $"{RemovalUnmarkedPrefix}{ex.Message}); nothing was changed.");
        }
    }

    /// <summary>
    /// For a removal that keeps a narrowed record (part of the component stays): drops the removal stamp from
    /// <paramref name="record"/> so saving it commits the removal. Returns the record.
    /// </summary>
    public static LinuxDeploymentRecord EndRemoval(LinuxDeploymentRecord record)
    {
        record.Metadata.Remove(RemovalOperationIdMetadataKey);
        return record;
    }

    /// <summary>Fixed text of the refusal when an operation cannot be journaled; the reason follows.</summary>
    public const string JournalUnavailablePrefix = "The deployment journal could not be written (";

    /// <summary>
    /// Opens the journaled operation of an install whose commit point is <paramref name="component"/>'s record (put
    /// <see cref="DeploymentOperation.Id"/> under <see cref="OperationIdMetadataKey"/> and commit right after the
    /// save). No journal (tests, tools): no operation and no refusal. A journal that cannot be written refuses before
    /// anything changes.
    /// </summary>
    public static async Task<(DeploymentOperation? Operation, string? Refusal)> BeginRecordOperationAsync(
        DeploymentJournal? journal, string description, DetectedGame game, ManagedComponentKind component, CancellationToken cancellationToken)
    {
        if (journal is null) return (null, null);
        try
        {
            return (await journal.BeginAsync(description, RecordCommitProbe(game, component), cancellationToken).ConfigureAwait(false), null);
        }
        catch (DeploymentDecisionPendingException ex)
        {
            return (null, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"{JournalUnavailablePrefix}{ex.Message}); nothing was changed.");
        }
    }

    /// <summary>Puts <paramref name="operation"/>'s id into <paramref name="record"/>'s metadata (its commit point); returns the record.</summary>
    public static LinuxDeploymentRecord StampOperation(DeploymentOperation? operation, LinuxDeploymentRecord record)
    {
        if (operation is not null) record.Metadata[OperationIdMetadataKey] = operation.Id;
        return record;
    }

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
            if (recovery is null)
            {
                deferred = journal.ReferencedOperationIds().Count;
                // Review w8 finding 2: once the ledger reads again in this session, each kept entry is settled before its
                // game's next operation (a retry would otherwise be undone by the start that finally recovers it).
                journal.WaitOnKeptEntries();
            }
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
        return await journal.RecoverAsync(entry => IsCommitted(entry, records.All())).ConfigureAwait(false);
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
    /// <para>A removal (<see cref="RecordRemovalProbe"/>) is committed once no record carries its id under
    /// <see cref="RemovalOperationIdMetadataKey"/>: the record it stamped was removed (or saved narrowed through
    /// <see cref="EndRemoval"/>). Matched on the id alone for the same reason.</para>
    /// <para>One rule for start-up recovery and for an operation that failed in process (#204,
    /// <see cref="LedgerCommitCheck"/>), so both always agree on what a landed commit write means.</para>
    /// </summary>
    internal static bool IsCommitted(JournalEntryInfo entry, IReadOnlyList<LinuxDeploymentRecord> records)
    {
        if (entry.CommitProbe is not { } probe) return false;
        if (probe.StartsWith(RecordRemovedProbePrefix, StringComparison.Ordinal))
            return !records.Any(record => CarriesId(record, RemovalOperationIdMetadataKey, entry.Id));
        if (!probe.StartsWith("record|", StringComparison.Ordinal)) return false;
        return records.Any(record => CarriesId(record, OperationIdMetadataKey, entry.Id));
    }

    private static bool CarriesId(LinuxDeploymentRecord record, string key, string id)
        => record.Metadata.TryGetValue(key, out var value) && value == id;
}

/// <summary>
/// The commit decision of a failed operation (#204): reads the ledger FILE again (never the store's cache, which a real
/// failure inside the write — a directory fsync after the rename — leaves holding the old ledger) and applies the rule
/// start-up recovery applies (<see cref="LinuxDeploymentRecovery.IsCommitted"/>). When the file cannot be read (damaged,
/// permission, I/O error) the decision comes from the store, not the bytes (review w8 finding 1): no write of the ledger
/// renamed into place since the operation began (<see cref="LinuxDeploymentRecordStore.LandedWrites"/>; every save
/// refused or failed before its rename) means nothing of it can have landed — not committed, roll back now; otherwise
/// undecided, and the entry waits (settled before the game's next operation, or at the next start). An operation of this
/// session whose commit point is not a ledger record (no record probe) is never committed by the ledger: rolled back as
/// before. An entry kept by a deferred start-up (no mark) is decided only by a readable ledger, as start-up would.
/// </summary>
internal sealed class LedgerCommitCheck(LinuxDeploymentRecordStore records) : IDeploymentCommitCheck
{
    public string? Mark() => records.LandedWrites.ToString(CultureInfo.InvariantCulture);

    public CommitDecision Decide(JournalEntryInfo entry, string? mark)
    {
        if (records.ReadFileFresh() is { } ledger)
            return LinuxDeploymentRecovery.IsCommitted(entry, ledger) ? CommitDecision.Committed : CommitDecision.NotCommitted;
        // No mark: an entry a deferred start-up kept (begun in an earlier session) — only a readable ledger decides it.
        if (!long.TryParse(mark, NumberStyles.None, CultureInfo.InvariantCulture, out var landedAtBegin)) return CommitDecision.Undecided;
        if (LinuxDeploymentRecovery.GameKeyOfCommitProbe(entry.CommitProbe) is null) return CommitDecision.NotCommitted;
        return landedAtBegin == records.LandedWrites ? CommitDecision.NotCommitted : CommitDecision.Undecided;
    }

    /// <summary>
    /// Same game (the record key in both probes). A new operation without a record probe (a plain file transaction) is
    /// not held up: the ledger does not commit it, and start-up recovery keeps what it writes as a later change.
    /// </summary>
    public bool HoldsUp(string? waitingProbe, string? newProbe)
    {
        if (LinuxDeploymentRecovery.GameKeyOfCommitProbe(newProbe) is not { } game) return false;
        return LinuxDeploymentRecovery.GameKeyOfCommitProbe(waitingProbe) is not { } waiting || string.Equals(waiting, game, StringComparison.Ordinal);
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
