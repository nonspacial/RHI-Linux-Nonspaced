using System.Text.Json;
using RHI.Core.Services;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Persistence;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// The Addon Manager's state in the deployment ledger (#168, DEP-S5): one <see cref="ManagedComponentKind.ReShadeAddonManager"/>
/// record per game is the truth (files with their hashes; the selection and each file's add-on id in metadata), so the
/// manager's changes are journaled like every other deployment and a crash rolls them back.
/// <para><c>addon-manager.json</c> stays as the copy older builds read and write (same shape, plus
/// <see cref="StateDocument.LedgerCopySha256"/>, which an older build ignores and drops when it saves). An older build
/// also drops the unknown record kind from the ledger at its next save. <see cref="Reconcile"/> therefore imports the copy
/// into the ledger when it is not the copy this build last wrote (unstamped or the stamp does not match) or when the
/// ledger has lost every manager record while the copy still has games; otherwise the ledger wins and the copy is
/// rewritten from it when they differ. The copy is written inside each operation, tracked and declared, before the
/// commit point, so a crash rolls the ledger and the copy back together.</para>
/// </summary>
public sealed partial class ReShadeAddonManagerService
{
    internal const ManagedComponentKind RecordKind = ManagedComponentKind.ReShadeAddonManager;
    internal const string SelectedIdsMetadataKey = "AddonManager.SelectedIds";
    internal const string AddonIdsMetadataKey = "AddonManager.AddonIds";

    /// <summary>Fixed text before the reason when the Addon Manager cannot bring its saved list and the ledger together (PT-BR pair).</summary>
    public const string ReconcileFailedPrefix =
        "ReShade Addon Manager did not change anything: its saved list could not be brought into the deployment records: ";

    private static readonly JsonSerializerOptions CanonicalOptions = new() { WriteIndented = false };

    internal enum ReconcileOutcome { Unchanged, CopyWritten, Imported, LedgerUnreadable, CopyUnreadable }

    /// <summary>
    /// Brings the ledger and <c>addon-manager.json</c> together (see the class summary). Call under <see cref="_gate"/>.
    /// Throws <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/> when a write fails (nothing half done:
    /// the ledger is written first and the import repeats at the next call while the copy is still unstamped).
    /// </summary>
    internal ReconcileOutcome Reconcile()
    {
        if (_records.CorruptLedgerPath is not null) return ReconcileOutcome.LedgerUnreadable;
        var load = _stateFile.Load();
        if (load.IsUnreadable) return ReconcileOutcome.CopyUnreadable;
        var all = _records.All();
        var ledger = all.Where(record => record.Component == RecordKind).ToList();
        var doc = load.Value;
        if (doc is null)
        {
            if (ledger.Count == 0) return ReconcileOutcome.Unchanged;
            _stateFile.Save(CopyOf(ledger));
            return ReconcileOutcome.CopyWritten;
        }

        var games = JsonStoreMaps.CaseInsensitive(doc.Games);
        foreach (var state in games.Values.Where(state => state is not null))
        {
            state.SelectedIds ??= [];
            state.Files ??= [];
        }
        foreach (var key in games.Where(pair => pair.Value is null).Select(pair => pair.Key).ToArray()) games.Remove(key);
        var stamped = string.Equals(doc.LedgerCopySha256, CopySha256(games), StringComparison.OrdinalIgnoreCase);
        if (!stamped || (ledger.Count == 0 && games.Values.Any(HasContent)))
        {
            var imported = ImportedRecords(games, all);
            _records.ReplaceComponent(RecordKind, imported);
            _stateFile.Save(CopyOf(imported));
            AppLog.Warn("addon-manager", $"Imported the Addon Manager's saved list into the deployment records ({imported.Count} game(s)); " +
                                         (stamped ? "the records had been dropped by an older RHI." : "an older RHI or a hand edit had changed it."));
            return ReconcileOutcome.Imported;
        }

        var projection = CopyOf(ledger);
        if (string.Equals(projection.LedgerCopySha256, doc.LedgerCopySha256, StringComparison.OrdinalIgnoreCase))
            return ReconcileOutcome.Unchanged;
        _stateFile.Save(projection);
        return ReconcileOutcome.CopyWritten;
    }

    /// <summary>
    /// #170 hook for ReShade's switch to and from No Addons: brings the list into the ledger first (the switch then renames
    /// the manager's files through its record) and returns no extra files, or null when the list cannot be read (the switch
    /// then leaves the manager's add-ons alone and says so).
    /// </summary>
    public IReadOnlyList<string>? ReconcileForReShade(DetectedGame game)
    {
        _gate.Wait();
        try
        {
            return TryReconcile(out _) is ReconcileOutcome.LedgerUnreadable or ReconcileOutcome.CopyUnreadable or null ? null : [];
        }
        finally
        {
            _gate.Release();
        }
    }

    // Reconcile that logs and reports a failed write instead of throwing (null = it failed; error says why).
    private ReconcileOutcome? TryReconcile(out string? error)
    {
        error = null;
        try
        {
            return Reconcile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Warn("addon-manager", "Could not reconcile the Addon Manager's saved list with the deployment records", ex);
            error = ex.Message;
            return null;
        }
    }

    private static bool HasContent(GameState state) => state.Files.Count > 0 || state.SelectedIds.Count > 0;

    /// <summary>The record for a game's selection (files as placed, ids in metadata).</summary>
    internal static LinuxDeploymentRecord BuildRecord(
        DetectedGame game, IReadOnlyList<string> selectedIds, IReadOnlyList<(string AddonId, string Path, string Sha256)> files,
        LinuxDeploymentRecord? existing)
    {
        var record = new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game),
            GameName = game.Name,
            Component = RecordKind,
            BinaryDirectory = game.BinaryDirectory ?? "",
            InstalledAtUtc = existing?.InstalledAtUtc ?? DateTime.UtcNow,
            Files = files.Select(file => new LinuxDeployedFile { Path = file.Path, Sha256 = file.Sha256 }).ToList(),
        };
        SetIds(record, selectedIds, files.Select(file => (file.AddonId, file.Path)));
        return record;
    }

    private static void SetIds(LinuxDeploymentRecord record, IEnumerable<string> selectedIds, IEnumerable<(string AddonId, string Path)> files)
    {
        record.Metadata[SelectedIdsMetadataKey] = JsonSerializer.Serialize(selectedIds.ToArray(), CanonicalOptions);
        var ids = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (addonId, path) in files) ids[Path.GetFileName(WithoutOffSuffix(path))] = addonId;
        record.Metadata[AddonIdsMetadataKey] = JsonSerializer.Serialize(ids, CanonicalOptions);
    }

    /// <summary>The selection a manager record holds (empty for a record that cannot be read).</summary>
    internal static IReadOnlyList<string> SelectedIdsOf(LinuxDeploymentRecord? record)
    {
        if (record is null || !record.Metadata.TryGetValue(SelectedIdsMetadataKey, out var json) || string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string AddonIdOf(LinuxDeploymentRecord record, string path)
    {
        if (record.Metadata.TryGetValue(AddonIdsMetadataKey, out var json) && !string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var ids = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (ids is not null && ids.TryGetValue(Path.GetFileName(WithoutOffSuffix(path)), out var id)) return id;
            }
            catch (JsonException)
            {
                // Falls through: an unreadable id map names no add-on.
            }
        }
        return "";
    }

    internal static string WithoutOffSuffix(string path)
        => path.EndsWith(ReShadeAddonSwitch.OffSuffix, StringComparison.Ordinal) ? path[..^ReShadeAddonSwitch.OffSuffix.Length] : path;

    /// <summary>The copy older builds read: the add-on's own name (they look for the <c>.rhi-off</c> name themselves).</summary>
    private static StateDocument CopyOf(IEnumerable<LinuxDeploymentRecord> records)
    {
        var games = new Dictionary<string, GameState>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records.Where(record => record.Component == RecordKind))
        {
            games[record.GameKey] = new GameState
            {
                SelectedIds = SelectedIdsOf(record).ToList(),
                Files = record.Files.Select(file => new StateFile
                {
                    AddonId = AddonIdOf(record, file.Path),
                    Path = WithoutOffSuffix(file.Path),
                    Sha256 = file.Sha256 ?? "",
                }).ToList(),
            };
        }
        return new StateDocument { Games = games, LedgerCopySha256 = CopySha256(games) };
    }

    /// <summary>SHA-256 of the games in a canonical form (keys and files in ordinal order, no formatting).</summary>
    internal static string CopySha256(IReadOnlyDictionary<string, GameState> games)
    {
        var canonical = games
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new
            {
                Key = pair.Key,
                SelectedIds = pair.Value.SelectedIds ?? [],
                Files = (pair.Value.Files ?? []).OrderBy(file => file.Path, StringComparer.Ordinal)
                    .Select(file => new { file.AddonId, file.Path, Sha256 = file.Sha256?.ToUpperInvariant() }).ToArray(),
            })
            .ToArray();
        return FileHash.Sha256HexOf(JsonSerializer.SerializeToUtf8Bytes(canonical, CanonicalOptions));
    }

    /// <summary>
    /// The records an import writes: one per game of the copy (games with nothing in them are dropped), keeping what the
    /// ledger still knows about the game (name, folder, install time) and naming each add-on by the file on disk
    /// (<c>.rhi-off</c> when No Addons turned it off).
    /// </summary>
    private static List<LinuxDeploymentRecord> ImportedRecords(IReadOnlyDictionary<string, GameState> games, IReadOnlyList<LinuxDeploymentRecord> all)
    {
        var result = new List<LinuxDeploymentRecord>();
        foreach (var (key, state) in games.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!HasContent(state)) continue;
            var existing = all.FirstOrDefault(r => r.GameKey == key && r.Component == RecordKind);
            var sibling = existing ?? all.FirstOrDefault(r => r.GameKey == key);
            var files = state.Files.Where(file => !string.IsNullOrWhiteSpace(file.Path)).ToList();
            var record = new LinuxDeploymentRecord
            {
                GameKey = key,
                GameName = sibling?.GameName ?? "",
                Component = RecordKind,
                BinaryDirectory = sibling?.BinaryDirectory ?? (files.Count > 0 ? Path.GetDirectoryName(files[0].Path) ?? "" : ""),
                InstalledAtUtc = existing?.InstalledAtUtc ?? DateTime.UtcNow,
                Files = files.Select(file => new LinuxDeployedFile { Path = OnDisk(file.Path), Sha256 = file.Sha256 }).ToList(),
            };
            SetIds(record, state.SelectedIds, files.Select(file => (file.AddonId, file.Path)));
            result.Add(record);
        }
        return result;

        static string OnDisk(string path)
            => !File.Exists(path) && File.Exists(path + ReShadeAddonSwitch.OffSuffix) ? path + ReShadeAddonSwitch.OffSuffix : path;
    }

    /// <summary>
    /// Writes the copy as it will be once <paramref name="record"/> (null: the game's record goes) is committed, inside
    /// <paramref name="scope"/> (tracked and declared first, so a crash before the commit point puts the old copy back).
    /// </summary>
    private async Task WriteCopyInsideAsync(DeploymentScope scope, DetectedGame game, LinuxDeploymentRecord? record)
    {
        var others = _records.All().Where(r => r.Component == RecordKind && !GameRecordIdentity.Matches(r.GameKey, game));
        var copy = CopyOf(record is null ? others : others.Append(record));
        var bytes = _stateFile.Stage(_ => copy)!.After;
        await scope.TrackAsync(_statePath, CancellationToken.None).ConfigureAwait(false);
        await scope.DeclareWriteAsync(_statePath, FileHash.Sha256HexOf(bytes), CancellationToken.None).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_statePath))!);
        AtomicFile.WriteStoreFile(_statePath, bytes);
    }
}
