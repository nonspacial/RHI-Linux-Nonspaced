using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Deployment;

/// <summary>How a batch over the library ended: counts only (the UI words the summary), problems go to rhi.log.</summary>
public sealed record IniBatchResult(int Updated, int Refused, int Failed)
{
    public int Total => Updated + Refused + Failed;
}

/// <summary>
/// INI deployment from the user's templates and Settings into games (#181; upstream 2.8.7 <c>AuxInstallService.Ini.cs</c>,
/// <c>MassDeployHandler</c>, <c>ApplyScreenshotPath_Click</c>): "Deploy ReShade.ini / relimiter.ini / DisplayCommander.ini"
/// for one game, the same for every game that has the component, and "Apply to all games" for the ReShade screenshot
/// folder, hotkeys and effect list style. Every write is one atomic user-file replace (a user's read-only lock is kept,
/// never a blocker); each game is refused, without a write, where an install would be (no confirmed patch target,
/// records left at a moved folder, an Xbox title its installer app is working on, a native Linux game).
/// </summary>
public sealed class UserIniService(UserIniTemplates templates, LinuxDeploymentRecordStore records, Func<DetectedGame, string?> writeRefusal,
    DeploymentJournal? journal = null)
{
    /// <summary>Fixed text before the game name when a game is not a Windows game (localisation key).</summary>
    public const string NativeGamePrefix = "INI files were not written: this is a native Linux game: ";

    /// <summary>Fixed text before the folder when the game's binary folder is gone (localisation key).</summary>
    public const string NoFolderPrefix = "INI files were not written: the game's folder is not available: ";

    /// <summary>
    /// Fixed text before the game name when RHI did not install the component there (localisation key): a copy the user
    /// installed is theirs, so its INI is never touched (review finding 1).
    /// </summary>
    public const string NotManagedPrefix = "INI files were not written: RHI did not install this component in this game, and a copy it did not install is left alone: ";

    public UserIniTemplates Templates => templates;

    /// <summary>The file name a component's INI is deployed under (beside its add-on).</summary>
    public static string? AddonIniName(ManagedComponentKind kind) => kind switch
    {
        ManagedComponentKind.ReLimiter => UserIniTemplates.FileName(UserIniTemplate.ReLimiter),
        ManagedComponentKind.DisplayCommander => UserIniTemplates.FileName(UserIniTemplate.DisplayCommander),
        _ => null
    };

    /// <summary>
    /// "Deploy ReShade.ini" (upstream <c>MergeRsIni</c>): the template's keys and the Settings values go into the game's
    /// ReShade.ini (created from the template when there is none); every other line of the file stays.
    /// </summary>
    public ComponentOperationResult DeployReShadeIni(DetectedGame game, LinuxAppSettings settings, CancellationToken cancellationToken = default)
    {
        if (Refusal(game) is { } refused) return ComponentOperationResult.Fail(refused);
        if (records.Find(game, ManagedComponentKind.ReShade) is not { } record) return ComponentOperationResult.Fail(NotManagedPrefix + game.Name);
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        // w5 integration: a ReShade installed as the Vulkan layer (#92) gets the Vulkan template, as its install did.
        var defaults = ReShadeGlobalSettingsApplier.InstallDefaults(settings, game, templates, vulkan: ReShadeVulkanLayer.IsLayerRecord(record));
        var result = ReShadeIniManager.Apply(game.BinaryDirectory!, [], [], defaults, game.InstallPath);
        return ComponentOperationResult.Ok(DeployedPrefix + result.Path, result.Path);
    }

    /// <summary>Fixed text before the path after a deployed INI (localisation key).</summary>
    public const string DeployedPrefix = "INI deployed from your template: ";

    /// <summary>Fixed text before the backup's path after a deploy that kept the game's earlier file (localisation key).</summary>
    public const string DeployedBackupPrefix = "INI deployed from your template; the game's earlier file is kept beside it as: ";

    /// <summary>Fixed text before the path when the game's file changed since RHI wrote it and nobody confirmed (localisation key).</summary>
    public const string ChangedPrefix = "INI not written: the game's file changed since RHI wrote it (or RHI did not write it); confirm to replace it, a backup is kept: ";

    /// <summary>Fixed text before the path after a backup was put back (localisation key).</summary>
    public const string RestoredPrefix = "Your earlier INI is back: ";

    /// <summary>Fixed text before the backup's path when nothing can be restored (localisation key).</summary>
    public const string NoBackupPrefix = "No INI backup RHI kept is left to restore: ";

    /// <summary>Fixed text before the path when a restore would lose changes made since RHI wrote the file (localisation key).</summary>
    public const string RestoreChangedPrefix = "The backup was not restored: the game's file changed since RHI wrote it; the backup stays beside it: ";

    /// <summary>Component record metadata: SHA-256 of the add-on INI RHI last wrote (install or Deploy).</summary>
    public const string WrittenSha256Key = "UserIni.WrittenSha256";

    /// <summary>Component record metadata: file name of the latest backup Deploy kept beside the INI.</summary>
    public const string BackupKey = "UserIni.Backup";

    /// <summary>Component record metadata: SHA-256 of that backup.</summary>
    public const string BackupSha256Key = "UserIni.BackupSha256";

    /// <summary>Every <c>UserIni.*</c> key: an add-on update builds a fresh record and carries these over.</summary>
    public const string MetadataKeyPrefix = "UserIni.";

    /// <summary>What a Deploy of the add-on INI would replace.</summary>
    public enum AddonIniState
    {
        /// <summary>No file yet.</summary>
        Absent,
        /// <summary>The file already holds the template.</summary>
        Template,
        /// <summary>The file is what RHI last wrote (install or Deploy): nothing of the user's is lost.</summary>
        RhiCopy,
        /// <summary>The file changed since RHI wrote it, or RHI never wrote it: Deploy asks and keeps a backup.</summary>
        Changed
    }

    private static UserIniTemplate TemplateOf(ManagedComponentKind kind)
        => kind == ManagedComponentKind.ReLimiter ? UserIniTemplate.ReLimiter : UserIniTemplate.DisplayCommander;

    /// <summary>The add-on INI's path in the game (the existing file in any case, else the template's name).</summary>
    public static string AddonIniPath(DetectedGame game, ManagedComponentKind kind)
    {
        var name = AddonIniName(kind) ?? throw new ArgumentOutOfRangeException(nameof(kind), kind, "No INI template for this component.");
        var directory = RenoDxDeploymentService.ResolveAddonDeployPath(game.BinaryDirectory!);
        return ExistingCaseInsensitive(directory, name) ?? Path.Combine(directory, name);
    }

    /// <summary>What a Deploy of <paramref name="kind"/>'s INI into <paramref name="game"/> would replace (review finding 2).</summary>
    public AddonIniState InspectAddonIni(DetectedGame game, ManagedComponentKind kind)
    {
        if (string.IsNullOrWhiteSpace(game.BinaryDirectory) || !Directory.Exists(game.BinaryDirectory)) return AddonIniState.Absent;
        var target = AddonIniPath(game, kind);
        if (!File.Exists(target)) return AddonIniState.Absent;
        if (IniLines.Read(target).SequenceEqual(IniLines.FromBytes(templates.Read(TemplateOf(kind), out _)), StringComparer.Ordinal))
            return AddonIniState.Template;
        var written = records.Find(game, kind)?.Metadata.GetValueOrDefault(WrittenSha256Key);
        return !string.IsNullOrWhiteSpace(written) && FileHash.HexEquals(FileHash.Sha256Hex(target), written)
            ? AddonIniState.RhiCopy
            : AddonIniState.Changed;
    }

    /// <summary>
    /// "Deploy relimiter.ini / DisplayCommander.ini" (upstream <c>CopyUlIni</c>/<c>CopyDcIni</c>): the game's file becomes a
    /// copy of the template, beside the add-on. Upstream overwrites; here the game's file is never lost (review finding 2):
    /// a file that changed since RHI wrote it (or that RHI never wrote) is replaced only with
    /// <paramref name="replaceChanged"/>, and then kept beside it as <c>&lt;name&gt;.rhi-backup-&lt;utc&gt;</c>, recorded
    /// on the component's record so <see cref="RestoreAddonIniAsync"/> can put it back. Journaled on that record: a
    /// crash before the record is saved takes the new file and the backup back.
    /// </summary>
    public async Task<ComponentOperationResult> DeployAddonIniAsync(DetectedGame game, ManagedComponentKind kind, bool replaceChanged = false, CancellationToken cancellationToken = default)
    {
        _ = AddonIniName(kind) ?? throw new ArgumentOutOfRangeException(nameof(kind), kind, "No INI template for this component.");
        if (Refusal(game) is { } refused) return ComponentOperationResult.Fail(refused);
        // Find hands out a copy: the ledger changes only through the stamped Upsert below.
        if (records.Find(game, kind) is not { } record) return ComponentOperationResult.Fail(NotManagedPrefix + game.Name);
        var lines = IniLines.FromBytes(templates.Read(TemplateOf(kind), out _));
        var target = AddonIniPath(game, kind);
        var roots = ReShadeGlobalSettingsApplier.GameRoots(game, game.BinaryDirectory!);
        XboxWriteGate.ThrowIfRefused(target);
        var written = IniLines.ResolveTarget(target, roots); // a link is followed only to a same-named file in the game
        var state = InspectAddonIni(game, kind);
        if (state == AddonIniState.Changed && !replaceChanged) return ComponentOperationResult.Fail(ChangedPrefix + target);
        var previous = state == AddonIniState.Changed ? File.ReadAllBytes(written) : null;
        var backup = previous is null ? null : FreeBackupPath(target);

        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(journal, $"{AddonIniName(kind)} deploy: {game.Name}", game, kind, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        try
        {
            cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
            if (backup is not null)
            {
                record.Metadata[BackupSha256Key] = await scope.PlaceBytesAsync(backup, previous!, cancellationToken).ConfigureAwait(false);
                record.Metadata[BackupKey] = Path.GetFileName(backup);
            }
            await scope.TrackAsync(written, cancellationToken).ConfigureAwait(false);
            IniLines.Write(target, lines, roots);
            await scope.DeclareWrittenContentAsync(written, cancellationToken).ConfigureAwait(false);
            record.Metadata[WrittenSha256Key] = FileHash.Sha256Hex(written);
            records.Upsert(scope.Stamp(record));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            return ComponentOperationResult.Fail(ex.Message + DeploymentScope.RollbackNote(rollback));
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return backup is null
            ? ComponentOperationResult.Ok(DeployedPrefix + target, target)
            : ComponentOperationResult.Ok(DeployedBackupPrefix + backup, target, backup);
    }

    /// <summary>
    /// An add-on install or update builds a fresh record: the INI deploy state (<c>UserIni.*</c>) of the record it replaces
    /// is carried over, and a config the install just created from the template counts as RHI's write.
    /// </summary>
    public static void CarryMetadata(LinuxDeploymentRecord? previous, LinuxDeploymentRecord next, string? createdConfigSha256)
    {
        foreach (var (key, value) in previous?.Metadata ?? [])
            if (key.StartsWith(MetadataKeyPrefix, StringComparison.Ordinal)) next.Metadata[key] = value;
        if (!string.IsNullOrWhiteSpace(createdConfigSha256)) next.Metadata[WrittenSha256Key] = createdConfigSha256;
    }

    /// <summary>The backup Deploy kept for this game's add-on INI, while it is still there unchanged; else null.</summary>
    public string? AddonIniBackup(DetectedGame game, ManagedComponentKind kind)
    {
        if (string.IsNullOrWhiteSpace(game.BinaryDirectory) || !Directory.Exists(game.BinaryDirectory)) return null;
        var metadata = records.Find(game, kind)?.Metadata;
        if (metadata?.GetValueOrDefault(BackupKey) is not { Length: > 0 } name || name != Path.GetFileName(name)) return null;
        var path = Path.Combine(Path.GetDirectoryName(AddonIniPath(game, kind))!, name);
        return File.Exists(path) && new FileInfo(path).LinkTarget is null
               && metadata.GetValueOrDefault(BackupSha256Key) is { Length: > 0 } sha && FileHash.HexEquals(FileHash.Sha256Hex(path), sha)
            ? path
            : null;
    }

    /// <summary>
    /// Puts the backup Deploy kept back in place of the add-on INI (journaled on the component's record) and deletes
    /// the backup. Refused, with the backup kept, when the game's file changed since RHI wrote it.
    /// </summary>
    public async Task<ComponentOperationResult> RestoreAddonIniAsync(DetectedGame game, ManagedComponentKind kind, CancellationToken cancellationToken = default)
    {
        if (Refusal(game) is { } refused) return ComponentOperationResult.Fail(refused);
        if (records.Find(game, kind) is not { } record) return ComponentOperationResult.Fail(NotManagedPrefix + game.Name);
        var target = AddonIniPath(game, kind);
        if (AddonIniBackup(game, kind) is not { } backup) return ComponentOperationResult.Fail(NoBackupPrefix + target);
        var roots = ReShadeGlobalSettingsApplier.GameRoots(game, game.BinaryDirectory!);
        XboxWriteGate.ThrowIfRefused(target);
        var written = IniLines.ResolveTarget(target, roots);
        if (File.Exists(written) && InspectAddonIni(game, kind) == AddonIniState.Changed)
            return ComponentOperationResult.Fail(RestoreChangedPrefix + backup);
        var bytes = File.ReadAllBytes(backup);

        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(journal, $"{AddonIniName(kind)} restore: {game.Name}", game, kind, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        try
        {
            cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
            await scope.TrackAsync(written, cancellationToken).ConfigureAwait(false);
            IniLines.WriteBytes(target, bytes, roots);
            await scope.DeclareWrittenContentAsync(written, cancellationToken).ConfigureAwait(false);
            await scope.DeleteAsync(backup, cancellationToken).ConfigureAwait(false);
            // The file is the user's again: RHI no longer holds a write there.
            foreach (var key in new[] { WrittenSha256Key, BackupKey, BackupSha256Key }) record.Metadata.Remove(key);
            records.Upsert(scope.Stamp(record));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            return ComponentOperationResult.Fail(ex.Message + DeploymentScope.RollbackNote(rollback));
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok(RestoredPrefix + target, target);
    }

    // <name>.rhi-backup-<utc>, never an existing name.
    private static string FreeBackupPath(string target)
    {
        var stem = $"{target}.rhi-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        var path = stem;
        for (var n = 2; File.Exists(path) || new FileInfo(path).LinkTarget is not null; n++) path = $"{stem}-{n}";
        return path;
    }

    /// <summary>
    /// Mass INI deploy (upstream <c>MassDeployRsIni/UlIni/DcIni_Click</c>): every game where RHI manages the component.
    /// One game per unit: Cancel stops between games, a game whose write failed keeps its file. An add-on INI that
    /// changed since RHI wrote it is replaced only with <paramref name="replaceChanged"/> (the inline question names how
    /// many), and is then kept as a backup beside it (review finding 2); without it such a game is counted as skipped.
    /// </summary>
    public Task<IniBatchResult> DeployToAllAsync(IReadOnlyList<DetectedGame> games, ManagedComponentKind kind, LinuxAppSettings settings,
        bool replaceChanged = false, CancellationToken cancellationToken = default)
        => RunBatchAsync(
            DeployTargets(games, kind),
            (game, token) => kind == ManagedComponentKind.ReShade
                ? Task.FromResult(DeployReShadeIni(game, settings, token))
                : DeployAddonIniAsync(game, kind, replaceChanged, token),
            "ini-deploy", cancellationToken);

    /// <summary>The games a mass deploy of <paramref name="kind"/>'s INI writes: where RHI manages the component.</summary>
    public IReadOnlyList<DetectedGame> DeployTargets(IReadOnlyList<DetectedGame> games, ManagedComponentKind kind)
        => games.Where(game => records.Find(game, kind) is not null).ToList();

    /// <summary>How many of those hold an add-on INI that changed since RHI wrote it (each kept as a backup when replaced).</summary>
    public int ChangedAddonInis(IReadOnlyList<DetectedGame> games, ManagedComponentKind kind)
        => AddonIniName(kind) is null ? 0 : DeployTargets(games, kind).Count(game => InspectAddonIni(game, kind) == AddonIniState.Changed);

    /// <summary>
    /// "Apply to all games" (upstream <c>ApplyScreenshotPath_Click</c>): screenshot folder, hotkeys and effect list style
    /// into every game whose ReShade RHI installed and that has a ReShade.ini (main and numbered files). A ReShade the
    /// user installed is left alone (review finding 1).
    /// </summary>
    public Task<IniBatchResult> ApplyReShadeSettingsToAllAsync(IReadOnlyList<DetectedGame> games, LinuxAppSettings settings, CancellationToken cancellationToken = default)
        => RunBatchAsync(
            ReShadeSettingsTargets(games),
            (game, token) =>
            {
                if (Refusal(game) is { } refused) return Task.FromResult(ComponentOperationResult.Fail(refused));
                _ = OperationCancellation.BeginWrites(token);
                return Task.FromResult(ComponentOperationResult.Ok(ReShadeGlobalSettingsApplier.ApplyToGame(settings, game)));
            },
            "reshade-settings", cancellationToken);

    /// <summary>
    /// ReLimiter "Apply to all games" (#188; upstream <c>ApplyUlOsdHotkey_Click</c>): the four globals go into every game's
    /// relimiter.ini (beside the add-on, and RE Framework's <c>_storage_/relimiter.ini</c>) where RHI installed ReLimiter;
    /// only those keys change.
    /// </summary>
    public Task<IniBatchResult> ApplyReLimiterSettingsToAllAsync(IReadOnlyList<DetectedGame> games, ReLimiterGlobals globals, CancellationToken cancellationToken = default)
        => RunBatchAsync(
            ReLimiterSettingsTargets(games),
            (game, token) =>
            {
                if (Refusal(game) is { } refused) return Task.FromResult(ComponentOperationResult.Fail(refused));
                var inis = ReLimiterInis(game);
                var roots = ReShadeGlobalSettingsApplier.GameRoots(game, game.BinaryDirectory!);
                foreach (var ini in inis) XboxWriteGate.ThrowIfRefused(ini);
                var writes = new List<IniFileWrite>();
                foreach (var ini in inis)
                {
                    var lines = IniLines.Read(ini);
                    if (ReLimiterIniEditor.Apply(lines, globals)) writes.Add(IniLines.PlannedWrite(ini, lines, roots));
                }
                foreach (var write in writes) IniLines.CheckLinks(write.Path, roots); // refused before anything is written
                _ = OperationCancellation.BeginWrites(token);
                // A game is updated whole or not at all: a failed write puts back the files already written (review 6).
                IniLines.WriteAll(writes);
                return Task.FromResult(ComponentOperationResult.Ok(string.Join(", ", inis)));
            },
            "relimiter-settings", cancellationToken);

    /// <summary>
    /// The globals also go into the user's relimiter.ini template (upstream updates its template), so every later
    /// ReLimiter install starts with them. Returns the template's path; throws when it cannot be written.
    /// </summary>
    public string ApplyReLimiterSettingsToTemplate(ReLimiterGlobals globals)
    {
        var path = templates.EnsureSeeded(UserIniTemplate.ReLimiter);
        var lines = IniLines.Read(path);
        if (ReLimiterIniEditor.Apply(lines, globals)) IniLines.Write(path, lines, [templates.Directory]);
        return path;
    }

    /// <summary>The games "Apply to all games" writes ReShade settings into: RHI's ReShade, a ReShade.ini there.</summary>
    public IReadOnlyList<DetectedGame> ReShadeSettingsTargets(IReadOnlyList<DetectedGame> games)
        => games.Where(game => records.Find(game, ManagedComponentKind.ReShade) is not null && HasReShadeIni(game)).ToList();

    /// <summary>The games ReLimiter's "Apply to all games" writes into: RHI's ReLimiter, a relimiter.ini there.</summary>
    public IReadOnlyList<DetectedGame> ReLimiterSettingsTargets(IReadOnlyList<DetectedGame> games)
        => games.Where(game => records.Find(game, ManagedComponentKind.ReLimiter) is not null && ReLimiterInis(game).Count > 0).ToList();

    /// <summary>The game's relimiter.ini files: beside the ReShade add-ons, and in RE Framework's <c>_storage_</c> folder.</summary>
    public static IReadOnlyList<string> ReLimiterInis(DetectedGame game)
    {
        if (game.BinaryDirectory is not { } bin || !Directory.Exists(bin)) return [];
        var found = new List<string>();
        var name = UserIniTemplates.FileName(UserIniTemplate.ReLimiter);
        if (ExistingCaseInsensitive(RenoDxDeploymentService.ResolveAddonDeployPath(bin), name) is { } beside) found.Add(beside);
        if (REFrameworkDeploymentService.FindStorageFolder(game) is { } storage && ExistingCaseInsensitive(storage, name) is { } stored)
            found.Add(stored);
        return found.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Whether the game's binary folder holds a ReShade.ini (any case).</summary>
    public static bool HasReShadeIni(DetectedGame game)
        => game.BinaryDirectory is { } dir && Directory.Exists(dir) && ExistingCaseInsensitive(dir, "ReShade.ini") is not null;

    /// <summary>Why nothing may be written into this game now, or null.</summary>
    public string? Refusal(DetectedGame game)
    {
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase)) return NativeGamePrefix + game.Name;
        if (writeRefusal(game) is { } refused) return refused;
        if (string.IsNullOrWhiteSpace(game.BinaryDirectory) || !Directory.Exists(game.BinaryDirectory))
            return NoFolderPrefix + (game.BinaryDirectory ?? game.InstallPath);
        return null;
    }

    /// <summary>
    /// One batch over games: one <see cref="OperationCancellation"/> per game linked to the batch token (Cancel is
    /// honoured between games only), a refused game counted apart from a failed one, nothing aborted by one game's error.
    /// Each game runs off the caller's thread and is awaited (an async step never blocks a pool thread; review nit 10).
    /// </summary>
    internal static async Task<IniBatchResult> RunBatchAsync(
        IReadOnlyList<DetectedGame> games, Func<DetectedGame, CancellationToken, Task<ComponentOperationResult>> perGame, string area,
        CancellationToken cancellationToken)
    {
        int updated = 0, refused = 0, failed = 0;
        foreach (var game in games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var unit = new OperationCancellation(cancellationToken);
            try
            {
                var result = await Task.Run(() => perGame(game, unit.Token), CancellationToken.None).ConfigureAwait(false);
                if (result.Success) updated++;
                else
                {
                    refused++;
                    AppLog.Warn(area, $"{game.Name}: {result.Message}");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                failed++;
                AppLog.Warn(area, $"{game.Name}: {ex.Message}", ex);
            }
        }
        return new IniBatchResult(updated, refused, failed);
    }

    private static string? ExistingCaseInsensitive(string directory, string fileName)
    {
        try
        {
            return Directory.EnumerateFiles(directory)
                .FirstOrDefault(path => Path.GetFileName(path).Equals(fileName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
