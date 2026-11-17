using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Launch.LauncherOptions;

/// <summary>What a launcher write did (the desktop words it).</summary>
public enum LauncherOptionsOutcome
{
    /// <summary>The arguments are in the launcher's options (written now, or already there).</summary>
    Written,
    /// <summary>RHI's part is out of the launcher's options again (the original options back, or only RHI's part taken out).</summary>
    Removed,
    /// <summary>Nothing to do (no arguments in the launcher, and none to add).</summary>
    Nothing,
    /// <summary>The launcher is running; nothing was written (it would write its own copy back).</summary>
    LauncherRunning,
    /// <summary>The game's launcher is not one RHI writes to, or its file is not there.</summary>
    NotSupported,
    /// <summary>The file could not be read, changed or recorded; nothing was changed (rolled back).</summary>
    Failed
}

/// <param name="KeptUserChange">Removal: the user had changed the options since, so only RHI's part was taken out (or nothing, when it was gone).</param>
/// <param name="Value">The options as they now are in the (first) file.</param>
/// <param name="Detail">The English reason of a failure (logged; the desktop shows its own line).</param>
public sealed record LauncherOptionsResult(LauncherOptionsOutcome Outcome, LauncherKind? Launcher, bool KeptUserChange = false, string? Value = null, string? Detail = null);

/// <summary>
/// #318 (a Linux addition): writes a game's launch arguments into its launcher's own launch options (Steam's Launch Options,
/// Lutris' Arguments, Heroic's Game Arguments), so the game gets them however it is started, and takes them out again.
/// <list type="bullet">
/// <item>The user's own options are kept: RHI's arguments are added to them (<see cref="LauncherOptionsText.Merge"/>; for Steam
/// after <c>%command%</c>), and RHI records the options before, what it wrote and exactly what it added
/// (<see cref="LauncherOptionsStore"/>). A second write (the arguments changed, a display mode switch) takes RHI's previous
/// part out first.</item>
/// <item>Removal puts the original options back when the launcher still holds what RHI wrote; when the user changed them
/// since, only RHI's part is taken out; when that part is gone too, the options are left as they are.</item>
/// <item>Nothing is written while the launcher runs (<see cref="ILauncherRunningProbe"/>), checked again right before the
/// write.</item>
/// <item>Every write is journaled: a byte-exact copy of each file goes to the backups folder and an entry naming the files,
/// their hashes before and after and the copy is saved (fsync) before the first file changes; the record is the commit
/// point. A failure rolls the files back from the copies; after a crash <see cref="Recover"/> does the same at start for an
/// entry whose record was never saved (a file the launcher or the user changed since is left alone).</item>
/// </list>
/// </summary>
public sealed class LauncherOptionsService
{
    /// <summary>Backups kept (newest first); older ones are removed after a successful write.</summary>
    public const int BackupsKept = 40;

    private static readonly JsonSerializerOptions JournalJson = new() { WriteIndented = true };

    private readonly LauncherOptionsStore _store;
    private readonly LauncherOptionsLocator _locator;
    private readonly ILauncherRunningProbe _probe;
    private readonly string _journalDirectory;
    private readonly string _backupDirectory;

    /// <param name="stateDirectory">The journal (<c>journal/</c>) and the byte-exact copies (<c>backups/</c>) live here.</param>
    public LauncherOptionsService(LauncherOptionsStore store, string stateDirectory, LauncherOptionsLocator? locator = null, ILauncherRunningProbe? probe = null)
    {
        _store = store;
        _locator = locator ?? new LauncherOptionsLocator();
        _probe = probe ?? new ProcLauncherRunningProbe();
        _journalDirectory = Path.Combine(stateDirectory, "journal");
        _backupDirectory = Path.Combine(stateDirectory, "backups");
    }

    public LauncherOptionsStore Store => _store;
    public string BackupDirectory => _backupDirectory;
    public string JournalDirectory => _journalDirectory;

    /// <summary>Test seam: runs after the journal entry and the copies are saved, before the first file is replaced.</summary>
    internal Action? BeforeFilesReplacedForTesting { get; set; }

    /// <summary>Test seam: runs between the file writes and the record save (throw to "crash" there).</summary>
    internal Action? BeforeCommitForTesting { get; set; }

    /// <summary>The launcher <paramref name="game"/>'s arguments can be written into (no file needed yet), or null.</summary>
    public static LauncherKind? KindOf(DetectedGame game) => LauncherOptionsLocator.KindOf(game);

    /// <summary>The launcher files for <paramref name="game"/>, or null.</summary>
    public LauncherOptionsTarget? Locate(DetectedGame game) => _locator.Locate(game);

    public bool IsRunning(LauncherKind kind) => _probe.IsRunning(kind);

    public LauncherOptionsRecord? RecordFor(string key) => _store.Get(key);

    /// <summary>
    /// Puts <paramref name="arguments"/> (RHI's launch arguments text, Windows rules) into the game's launcher options, merged
    /// with the user's own; replaces RHI's previous part when the game already has a record. Empty arguments remove RHI's part.
    /// </summary>
    /// <param name="keepRecord">
    /// For empty arguments (a display mode with none): take RHI's part out but keep the record, so the launcher's copy comes
    /// back when the game gets arguments again. Otherwise empty arguments remove the record.
    /// </param>
    public LauncherOptionsResult Apply(DetectedGame game, string key, string? arguments, bool keepRecord = false)
    {
        ArgumentNullException.ThrowIfNull(game);
        var words = LaunchArguments.Split(LaunchArguments.Normalize(arguments));
        if (words.Count == 0 && (!keepRecord || _store.Get(key) is null)) return Remove(key);
        var part = LauncherOptionsText.ShellJoin(words);
        var record = _store.Get(key);
        var target = _locator.Locate(game);
        if (target is null) return new(LauncherOptionsOutcome.NotSupported, record?.Launcher);
        if (_probe.IsRunning(target.Kind)) return new(LauncherOptionsOutcome.LauncherRunning, target.Kind);

        var files = new List<LauncherOptionsFileRecord>();
        var writes = new List<PlannedWrite>();
        var paths = target.Files.ToList();
        if (record is not null && record.Launcher == target.Kind)
            foreach (var old in record.Files) if (!paths.Contains(old.Path, StringComparer.Ordinal) && File.Exists(old.Path)) paths.Add(old.Path);
        try
        {
            foreach (var path in paths)
            {
                var before = File.ReadAllBytes(path);
                var (present, current) = ReadValue(target, before);
                var previous = record?.Launcher == target.Kind ? record.Files.FirstOrDefault(f => f.Path == path) : null;
                bool originalPresent;
                string? original;
                string own; // the options without any part of RHI's
                if (previous is not null && present && current == previous.Written)
                {
                    (originalPresent, original) = (previous.OriginalPresent, previous.Original);
                    own = original ?? "";
                }
                else if (previous is not null && present && previous.Added.Length > 0 && LauncherOptionsText.RemovePart(current!, previous.Added) is { } stripped)
                {
                    (originalPresent, original, own) = (true, stripped, stripped);
                }
                else
                {
                    (originalPresent, original, own) = (present, current, current ?? "");
                }
                var already = part.Length == 0 || LauncherOptionsText.Contains(own, part);
                var value = already ? own : LauncherOptionsText.Merge(own, part, target.Kind == LauncherKind.Steam);
                files.Add(new LauncherOptionsFileRecord { Path = path, OriginalPresent = originalPresent, Original = original, Written = value, Added = already ? "" : part });
                var after = present && current == value ? before : WriteValue(target, before, value);
                if (!after.AsSpan().SequenceEqual(before)) writes.Add(new PlannedWrite(path, before, after));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            AppLog.Warn("launcher-options", $"Could not read {target.Kind}'s launch options for {game.Name}", ex);
            return new(LauncherOptionsOutcome.Failed, target.Kind, Detail: ex.Message);
        }

        var id = Guid.NewGuid().ToString("N");
        var newRecord = new LauncherOptionsRecord
        {
            Launcher = target.Kind, Arguments = LaunchArguments.Normalize(arguments) ?? "", OperationId = id, Files = files,
            SteamAppId = target.SteamAppId, HeroicAppName = target.HeroicAppName
        };
        var failure = Commit(id, key, target.Kind, writes, data =>
        {
            data.Games[key] = newRecord;
            return data;
        }, commitWhenRecordIs: id, priorOperation: record?.OperationId);
        if (failure is not null) return failure;
        AppLog.Warn("launcher-options", $"Put launch arguments of {game.Name} into {target.Kind}'s options ({writes.Count} file(s)): {part}");
        return new(LauncherOptionsOutcome.Written, target.Kind, Value: files.FirstOrDefault()?.Written);
    }

    /// <summary>
    /// Takes RHI's part out of the game's launcher options: the original options back when the launcher still holds what RHI
    /// wrote, else only RHI's part (<see cref="LauncherOptionsResult.KeptUserChange"/>). The record goes once the files are done.
    /// </summary>
    public LauncherOptionsResult Remove(string key)
    {
        var record = _store.Get(key);
        if (record is null) return new(LauncherOptionsOutcome.Nothing, null);
        if (_probe.IsRunning(record.Launcher)) return new(LauncherOptionsOutcome.LauncherRunning, record.Launcher);
        var reader = TargetFor(record);
        var writes = new List<PlannedWrite>();
        var keptUserChange = false;
        string? firstValue = null;
        try
        {
            foreach (var file in record.Files)
            {
                if (!File.Exists(file.Path)) continue; // the launcher (or the user) removed the file: nothing of RHI's is left
                var before = File.ReadAllBytes(file.Path);
                var (present, current) = ReadValue(reader, before);
                string? value;
                if (present && current == file.Written)
                {
                    value = file.OriginalPresent ? file.Original ?? "" : null;
                }
                else if (present && file.Added.Length > 0 && LauncherOptionsText.RemovePart(current!, file.Added) is { } stripped)
                {
                    value = stripped.Trim().Length == 0 && !file.OriginalPresent ? null : stripped;
                    keptUserChange = true;
                }
                else
                {
                    // The user changed the options and RHI's part is not there any more (or RHI added nothing): left as they are.
                    keptUserChange |= file.Added.Length > 0;
                    firstValue ??= current;
                    continue;
                }
                firstValue ??= value;
                var after = WriteValue(reader, before, value);
                if (!after.AsSpan().SequenceEqual(before)) writes.Add(new PlannedWrite(file.Path, before, after));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            AppLog.Warn("launcher-options", $"Could not read {record.Launcher}'s launch options for {key}", ex);
            return new(LauncherOptionsOutcome.Failed, record.Launcher, Detail: ex.Message);
        }
        var id = Guid.NewGuid().ToString("N");
        var failure = Commit(id, key, record.Launcher, writes, data =>
        {
            data.Games.Remove(key);
            return data;
        }, commitWhenRecordIs: null, priorOperation: record.OperationId);
        if (failure is not null) return failure;
        AppLog.Warn("launcher-options", $"Took RHI's launch arguments out of {record.Launcher}'s options for {key} ({writes.Count} file(s)).");
        return new(LauncherOptionsOutcome.Removed, record.Launcher, keptUserChange, firstValue);
    }

    // A record's files are read with the ids the record keeps (the game may be gone from the library meanwhile).
    private static LauncherOptionsTarget TargetFor(LauncherOptionsRecord record) => record.Launcher switch
    {
        LauncherKind.Steam => new LauncherOptionsTarget(LauncherKind.Steam, [], SteamAppId: record.SteamAppId ?? throw new FormatException("The record has no Steam AppID.")),
        LauncherKind.Heroic => new LauncherOptionsTarget(LauncherKind.Heroic, [], HeroicAppName: record.HeroicAppName ?? throw new FormatException("The record has no Heroic app name.")),
        _ => new LauncherOptionsTarget(LauncherKind.Lutris, [])
    };

    private static (bool Present, string? Value) ReadValue(LauncherOptionsTarget target, byte[] bytes)
    {
        switch (target.Kind)
        {
            case LauncherKind.Steam:
                var reading = SteamLocalConfig.Read(bytes, target.SteamAppId!.Value);
                return (reading.LaunchOptions is not null, reading.LaunchOptions);
            case LauncherKind.Lutris:
                var args = LutrisGameArgs.Read(bytes);
                return (args is not null, args);
            default:
                var heroic = HeroicGameArgs.Read(bytes, target.HeroicAppName!);
                return (heroic is not null, heroic);
        }
    }

    private static byte[] WriteValue(LauncherOptionsTarget target, byte[] bytes, string? value) => target.Kind switch
    {
        LauncherKind.Steam => SteamLocalConfig.Write(bytes, target.SteamAppId!.Value, value),
        LauncherKind.Lutris => LutrisGameArgs.Write(bytes, value),
        _ => HeroicGameArgs.Write(bytes, target.HeroicAppName!, value)
    };

    // ── the journal ───────────────────────────────────────────────────────────────────────

    private sealed record PlannedWrite(string Path, byte[] Before, byte[] After);

    /// <summary>One journaled launcher file: where it is, its copy, and its hashes before and after RHI's write.</summary>
    public sealed class JournalFile
    {
        public string Path { get; set; } = "";
        public string Backup { get; set; } = "";
        public string BeforeSha256 { get; set; } = "";
        public string AfterSha256 { get; set; } = "";
    }

    /// <summary>
    /// A launcher write in progress. It committed when the record of <see cref="Key"/> carries <see cref="CommitOperation"/>
    /// (a write), or when no record carries <see cref="PriorOperation"/> any more (a removal: <see cref="CommitOperation"/> null).
    /// </summary>
    public sealed class JournalEntry
    {
        public string Id { get; set; } = "";
        public string Key { get; set; } = "";
        public LauncherKind Launcher { get; set; }
        public string? CommitOperation { get; set; }
        public string? PriorOperation { get; set; }
        public List<JournalFile> Files { get; set; } = [];
    }

    private LauncherOptionsResult? Commit(string id, string key, LauncherKind kind, IReadOnlyList<PlannedWrite> writes,
        Func<LauncherOptionsData, LauncherOptionsData> change, string? commitWhenRecordIs, string? priorOperation)
    {
        var entryPath = Path.Combine(_journalDirectory, id + ".json");
        var replaced = new List<PlannedWrite>();
        try
        {
            if (writes.Count > 0)
            {
                AtomicFile.CreateDirectoryDurable(_journalDirectory);
                AtomicFile.CreateDirectoryDurable(_backupDirectory);
                var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
                var entry = new JournalEntry { Id = id, Key = key, Launcher = kind, CommitOperation = commitWhenRecordIs, PriorOperation = priorOperation };
                for (var n = 0; n < writes.Count; n++)
                {
                    var write = writes[n];
                    var backup = Path.Combine(_backupDirectory, $"{stamp}-{id[..8]}-{n}-{kind.ToString().ToLowerInvariant()}-{Path.GetFileName(write.Path)}");
                    AtomicFile.WriteNew(backup, write.Before, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    entry.Files.Add(new JournalFile { Path = write.Path, Backup = backup, BeforeSha256 = Sha(write.Before), AfterSha256 = Sha(write.After) });
                }
                AtomicFile.WriteStoreFile(entryPath, JsonSerializer.SerializeToUtf8Bytes(entry, JournalJson));
                BeforeFilesReplacedForTesting?.Invoke();
                // The launcher may have started while RHI prepared the change.
                if (_probe.IsRunning(kind))
                {
                    FileCleanup(entryPath);
                    return new(LauncherOptionsOutcome.LauncherRunning, kind);
                }
                foreach (var write in writes)
                {
                    if (!File.ReadAllBytes(write.Path).AsSpan().SequenceEqual(write.Before))
                        throw new IOException($"{write.Path} changed while RHI was preparing the change.");
                    replaced.Add(write);
                    AtomicFile.ReplaceUserFile(write.Path, write.After);
                }
            }
            BeforeCommitForTesting?.Invoke();
            _store.Save(change(_store.Load()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var notRestored = RollBack(replaced);
            AppLog.Warn("launcher-options", $"Launcher options write for {key} failed; {(notRestored.Count == 0 ? "every file was put back" : "not put back: " + string.Join(", ", notRestored))}", ex);
            if (notRestored.Count == 0) FileCleanup(entryPath);
            return new(LauncherOptionsOutcome.Failed, kind, Detail: ex.Message);
        }
        FileCleanup(entryPath);
        PruneBackups();
        return null;
    }

    private static List<string> RollBack(IEnumerable<PlannedWrite> replaced)
    {
        var notRestored = new List<string>();
        foreach (var write in replaced.Reverse())
        {
            try
            {
                if (File.Exists(write.Path) && File.ReadAllBytes(write.Path).AsSpan().SequenceEqual(write.Before)) continue;
                AtomicFile.ReplaceUserFile(write.Path, write.Before);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                notRestored.Add(write.Path);
            }
        }
        return notRestored;
    }

    /// <summary>
    /// Start-up recovery: every journal entry left by a write that never reached its commit point has its files put back from
    /// the byte-exact copies, when they still hold what RHI wrote (a file changed since is left alone and logged). Entries of
    /// committed writes are only removed. Returns how many files were put back.
    /// </summary>
    public int Recover()
    {
        if (!Directory.Exists(_journalDirectory)) return 0;
        var restored = 0;
        foreach (var entryPath in Directory.EnumerateFiles(_journalDirectory, "*.json").ToList())
        {
            JournalEntry? entry;
            try { entry = JsonSerializer.Deserialize<JournalEntry>(File.ReadAllBytes(entryPath)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                AppLog.Warn("launcher-options", $"Launcher options journal entry {entryPath} unreadable; left for inspection", ex);
                continue;
            }
            if (entry is null) continue;
            var record = _store.Get(entry.Key);
            var committed = entry.CommitOperation is { } op
                ? record?.OperationId == op
                : record is null || record.OperationId != entry.PriorOperation;
            var allDone = true;
            if (!committed)
            {
                foreach (var file in entry.Files)
                {
                    try
                    {
                        if (!File.Exists(file.Path) || !File.Exists(file.Backup)) continue;
                        var now = Sha(File.ReadAllBytes(file.Path));
                        if (now == file.BeforeSha256) continue;
                        if (now != file.AfterSha256)
                        {
                            AppLog.Warn("launcher-options", $"{file.Path} changed after an interrupted RHI write; left as it is (RHI's copy: {file.Backup}).");
                            continue;
                        }
                        if (_probe.IsRunning(entry.Launcher))
                        {
                            allDone = false; // the launcher would write over the restore; tried again at the next start
                            continue;
                        }
                        var backup = File.ReadAllBytes(file.Backup);
                        if (Sha(backup) != file.BeforeSha256) { AppLog.Warn("launcher-options", $"RHI's copy {file.Backup} does not match; {file.Path} left as it is."); continue; }
                        AtomicFile.ReplaceUserFile(file.Path, backup);
                        restored++;
                        AppLog.Warn("launcher-options", $"Put {file.Path} back after an interrupted RHI write (from {file.Backup}).");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        allDone = false;
                        AppLog.Warn("launcher-options", $"Could not put {file.Path} back after an interrupted RHI write", ex);
                    }
                }
            }
            if (allDone) FileCleanup(entryPath);
        }
        return restored;
    }

    private void PruneBackups()
    {
        try
        {
            foreach (var old in Directory.EnumerateFiles(_backupDirectory).OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(BackupsKept))
                FileCleanup(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void FileCleanup(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
