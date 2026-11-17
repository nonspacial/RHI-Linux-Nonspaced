using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// #272 (upstream 2.9.3 All Settings, Save ini / Load saved ini): every OptiScaler.ini setting of a game, described by the
/// installed build's own INI, written on Apply with a backup; and saved INIs put into a game.
/// </summary>
public sealed partial class OptiScalerDeploymentService
{
    public const string AllSettingsNotManagedMessage = "OptiScaler is not managed by RHI Linux for this game, so its OptiScaler.ini is not edited here.";
    public const string AllSettingsIniMissingMessage = "This game's OptiScaler.ini was not found.";
    /// <summary>The refused section/key/value follows.</summary>
    public const string AllSettingsValueRefusedPrefix = "This OptiScaler.ini value was not written (one line, at most 256 characters; names of letters, digits, _ and .): ";
    /// <summary>The count follows.</summary>
    public const string AllSettingsAppliedPrefix = "OptiScaler.ini changes written: ";
    public const string AllSettingsFailedPrefix = "OptiScaler.ini was not changed: ";
    /// <summary>The saved INI's name follows.</summary>
    public const string SavedIniLoadedPrefix = "This game's OptiScaler.ini is now your saved ini ";
    public const string SavedIniMissingPrefix = "The saved ini was not found: ";

    /// <summary>
    /// Where the installed build's own OptiScaler.ini is kept per game (upstream's "reference" copy): its comments list that
    /// build's options. RHI's data folder, never the game's.
    /// </summary>
    public string ReferenceIniDirectory { get; set; } = Path.Combine(LinuxPaths.DataHome, "rhi-linux", "optiscaler", "ini-reference");

    public string ReferenceIniPath(DetectedGame game) => Path.Combine(ReferenceIniDirectory, GameFileTag(game) + ".ini");

    private static string GameFileTag(DetectedGame game) => FileTag(LinuxDeploymentRecordStore.GameKey(game));

    private static string FileTag(string key) => string.Concat(key.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));

    /// <summary>w14 final review m5: the reference INI follows a game whose key changed (folder move, Repair after move).</summary>
    internal Settings.IGameStateStore ReferenceIniState => _referenceIniState ??= new ReferenceIniMove(this);

    private Settings.IGameStateStore? _referenceIniState;

    private sealed class ReferenceIniMove(OptiScalerDeploymentService owner) : Settings.IGameStateStore
    {
        /// <summary>The old key's copy under the new key's name (the new key's own copy, if any, wins); the old file stays unused.</summary>
        public Persistence.StagedFileWrite? StageMove(Settings.GameStateMove move)
        {
            ArgumentNullException.ThrowIfNull(move);
            var target = Path.Combine(owner.ReferenceIniDirectory, FileTag(move.NewRecordKey) + ".ini");
            if (File.Exists(target)) return null;
            foreach (var old in move.OldRecordKeys)
            {
                var source = Path.Combine(owner.ReferenceIniDirectory, FileTag(old) + ".ini");
                if (File.Exists(source)) return new Persistence.StagedFileWrite(target, null, File.ReadAllBytes(source));
            }
            return null;
        }
    }

    /// <summary>
    /// After an install or update: a copy of the package's OptiScaler.ini describes All Settings; a package without one (a
    /// bare custom DLL) removes the old copy, so it never describes another build. A failure is logged, never an install failure.
    /// </summary>
    internal void SaveReferenceIni(DetectedGame game, string packageRoot)
    {
        try
        {
            var reference = ReferenceIniPath(game);
            var packaged = Path.Combine(packageRoot, "OptiScaler.ini");
            if (File.Exists(packaged))
            {
                Directory.CreateDirectory(ReferenceIniDirectory);
                AtomicFile.Copy(packaged, reference);
            }
            else if (File.Exists(reference)) AtomicFile.Delete(reference);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("optiscaler", "The installed build's OptiScaler.ini could not be kept for All Settings", ex);
        }
    }

    /// <summary>The game's OptiScaler.ini settings, described by the installed build's INI; null when RHI does not manage OptiScaler there.</summary>
    public IReadOnlyList<OptiScalerIniEntry>? GetIniEntries(DetectedGame game)
    {
        if (GetRecord(game) is not { } record) return null;
        var ini = Path.Combine(Path.GetFullPath(record.BinaryDirectory), "OptiScaler.ini");
        if (!File.Exists(ini)) return null;
        var reference = ReferenceIniPath(game);
        return OptiScalerIniCatalog.Merge(OptiScalerIniCatalog.ParseFile(ini), File.Exists(reference) ? OptiScalerIniCatalog.ParseFile(reference) : null);
    }

    /// <summary>The game's OptiScaler.ini path while RHI manages OptiScaler there (Save ini).</summary>
    public string? ManagedIniPath(DetectedGame game)
        => GetRecord(game) is { } record && Path.Combine(Path.GetFullPath(record.BinaryDirectory), "OptiScaler.ini") is var ini && File.Exists(ini) ? ini : null;

    /// <summary>
    /// Writes <paramref name="values"/> into the game's OptiScaler.ini, only those lines (a key the file lacks goes into its
    /// section). With <paramref name="backup"/> the file is copied to RHI's data folder first (Apply); re-applying saved
    /// edits after an install takes none. Every name and value is checked first: nothing is written when one is refused.
    /// </summary>
    public async Task<ComponentOperationResult> ApplyIniValuesAsync(DetectedGame game, IReadOnlyList<(string Section, string Key, string Value)> values,
        bool backup = true, CancellationToken cancellationToken = default)
    {
        if (GetRecord(game) is not { } record) return ComponentOperationResult.Fail(AllSettingsNotManagedMessage);
        var ini = Path.Combine(Path.GetFullPath(record.BinaryDirectory), "OptiScaler.ini");
        if (!File.Exists(ini)) return ComponentOperationResult.Fail(AllSettingsIniMissingMessage);
        foreach (var (section, key, value) in values)
            if (!OptiScalerIniCatalog.IsWritableName(section) || !OptiScalerIniCatalog.IsWritableName(key) || !OptiScalerIniCatalog.IsWritableValue(value))
                return ComponentOperationResult.Fail(AllSettingsValueRefusedPrefix + $"[{section}] {key}={value}");
        if (values.Count == 0) return ComponentOperationResult.Ok(AllSettingsAppliedPrefix + "0");
        string? copy = null;
        try
        {
            if (backup) copy = await BackUpIniAsync(game, ini, cancellationToken).ConfigureAwait(false);
            OptiScalerIniEditor.SetValuesInFile(ini, values);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ComponentOperationResult.Fail(AllSettingsFailedPrefix + ex.Message);
        }
        return ComponentOperationResult.Ok(AllSettingsAppliedPrefix + values.Count + (copy is null ? "" : FreshIniBackupPrefix + copy), ini);
    }

    /// <summary>
    /// "Load saved ini" (upstream <c>OsIniLibrary.Deploy</c>): the saved INI becomes the game's OptiScaler.ini (the old one
    /// backed up, the copy journaled), with what RHI always sets: LoadAsiPlugins, LoadReshade while RHI's ReShade is
    /// chained, and the global overlay key.
    /// </summary>
    public async Task<ComponentOperationResult> ReplaceIniWithSavedAsync(DetectedGame game, string savedIni, string name, CancellationToken cancellationToken = default)
    {
        if (GetRecord(game) is not { } record) return ComponentOperationResult.Fail(AllSettingsNotManagedMessage);
        if (!File.Exists(savedIni)) return ComponentOperationResult.Fail(SavedIniMissingPrefix + name);
        return await ReplaceIniAsync(game, record, savedIni, SavedIniLoadedPrefix + $"\"{name}\".", cancellationToken, writeMenuKey: true).ConfigureAwait(false);
    }
}
