using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>A preset RHI put into a game folder (recorded on the game's ReShade record).</summary>
/// <param name="FileName">The file name in the game's binary folder (no folders: a moved game keeps its record).</param>
/// <param name="Sha256">What RHI wrote, so a later install over it and its removal know whether the user changed it.</param>
public sealed record InstalledReShadePreset(string FileName, string Sha256);

/// <summary>
/// ReShade presets (#180; upstream 2.8.7 <c>PresetValidator</c>, <c>TechniquesParser</c>, <c>ShaderResolver</c>,
/// <c>PresetPopupHelper</c>, <c>DragDropHandler.Preset</c>, <c>MassPresetInstall</c>): the user's presets live in
/// <c>~/.config/rhi-linux/inis/reshade-presets</c> (upstream's folder under its INIs folder); installing one copies it
/// beside the game's ReShade, points ReShade at it (<c>[GENERAL] PresetPath</c> in ReShade.ini) and works out which shader
/// packs provide the effects its <c>Techniques=</c> lines name. The copy is journaled through ReShade's record (the
/// record carries the preset list, so a crash before the save takes the copy back and a later install or removal knows
/// RHI put it there). A game file of the same name RHI did not write, or one the user changed since, is never
/// overwritten; removing a preset leaves a copy the user changed.
/// </summary>
public sealed class ReShadePresetService(LinuxDeploymentRecordStore records, DeploymentJournal? journal, Func<DetectedGame, string?> writeRefusal)
{
    /// <summary>ReShade record metadata: JSON list of <see cref="InstalledReShadePreset"/>.</summary>
    public const string MetadataKey = "ReShade.Presets";

    /// <summary>The largest preset RHI copies (presets are a few KiB).</summary>
    public const long MaxPresetBytes = 1024 * 1024;

    public const string NotAPresetPrefix = "Not a ReShade preset (it needs a Techniques= line naming at least one .fx effect): ";
    public const string TooLargePrefix = "Preset not used: it is larger than 1 MiB: ";
    public const string NoManagedReShadePrefix = "Install ReShade with RHI first; presets go into games whose ReShade RHI manages: ";
    public const string NameTakenPrefix = "A file with the preset's name is already in the game folder and RHI did not put it there (or you changed it), so it was left as it is: ";
    public const string InstalledPrefix = "Preset installed and selected in ReShade.ini: ";
    public const string RemovedPrefix = "Preset removed: ";
    public const string KeptChangedPrefix = "Preset removed from RHI's list; the file was kept because it changed since RHI put it there: ";
    public const string NotInstalledPrefix = "RHI did not install this preset: ";

    // ---- parsing -----------------------------------------------------------------------------

    /// <summary>Upstream <c>PresetValidator.IsReShadePreset</c>: a <c>Techniques=</c> line with at least one <c>name@file.fx</c>.</summary>
    public static bool IsPreset(string content) => RequiredEffects(content).Any(fx => fx.EndsWith(".fx", StringComparison.OrdinalIgnoreCase) && fx.Length > 3);

    /// <summary>Every effect file (<c>Techniques=Name@File.fx,…</c>) the preset uses, across all its Techniques lines.</summary>
    public static IReadOnlySet<string> RequiredEffects(string content)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in content.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.TrimStart();
            var eq = line.IndexOf('=');
            if (eq < 0 || !line[..eq].TrimEnd().Equals("Techniques", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var entry in line[(eq + 1)..].Split(','))
            {
                var at = entry.IndexOf('@');
                if (at < 0) continue;
                var fx = entry[(at + 1)..].Trim();
                if (fx.Length > 0) result.Add(fx);
            }
        }
        return result;
    }

    /// <summary>
    /// Upstream <c>ShaderResolver.Resolve</c>: the packs holding at least one required effect (matched by file name, any
    /// case), and the effects no pack has.
    /// </summary>
    public static (IReadOnlyList<string> PackIds, IReadOnlyList<string> Unresolved) Resolve(
        IEnumerable<string> effects, IReadOnlyDictionary<string, IReadOnlyList<string>> packFiles)
    {
        var packs = new List<string>();
        var unresolved = new List<string>();
        foreach (var effect in effects.Where(e => !string.IsNullOrWhiteSpace(e)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var found = false;
            foreach (var (packId, files) in packFiles)
            {
                if (!files.Any(file => Path.GetFileName(file.Replace('\\', '/')).Equals(effect, StringComparison.OrdinalIgnoreCase))) continue;
                found = true;
                if (!packs.Contains(packId, StringComparer.OrdinalIgnoreCase)) packs.Add(packId);
            }
            if (!found) unresolved.Add(effect);
        }
        return (packs, unresolved);
    }

    // ---- the user's presets folder ------------------------------------------------------------------

    /// <summary>Upstream's presets folder under the INIs folder.</summary>
    public static string PresetsFolder(string inisFolder) => Path.Combine(inisFolder, "reshade-presets");

    /// <summary>The presets in the folder (by name), never following links out of it.</summary>
    public static IReadOnlyList<string> ListPresets(string folder)
    {
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder, "*.ini", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true })
            .Where(path => new FileInfo(path).LinkTarget is null)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Reads a preset (size-capped) and checks it is one; throws <see cref="InvalidDataException"/> otherwise.</summary>
    public static string ReadPreset(string path)
    {
        var length = new FileInfo(path).Length;
        if (length > MaxPresetBytes) throw new InvalidDataException(TooLargePrefix + path);
        var content = File.ReadAllText(path);
        if (!IsPreset(content)) throw new InvalidDataException(NotAPresetPrefix + Path.GetFileName(path));
        return content;
    }

    /// <summary>
    /// A dropped preset is kept in the presets folder. Upstream replaces a preset of the same name; here a preset already
    /// in the folder is never overwritten (review finding 3): the same content under that name (any case, or a numbered
    /// copy from an earlier drop) is reused, other content goes in under the first free <c>Name (2).ini</c>,
    /// <c>Name (3).ini</c>… Names compare case-insensitively, as Wine does.
    /// </summary>
    public static string StorePreset(string source, string folder)
    {
        ReadPreset(source);
        var bytes = File.ReadAllBytes(source);
        var sha = FileHash.Sha256HexOf(bytes);
        Directory.CreateDirectory(folder);
        var fullSource = Path.GetFullPath(source);
        var stem = Path.GetFileNameWithoutExtension(source);
        var extension = Path.GetExtension(source);
        for (var n = 1; ; n++)
        {
            var name = n == 1 ? Path.GetFileName(source) : $"{stem} ({n}){extension}";
            var existing = ExistingCaseInsensitive(folder, name);
            if (existing is null)
            {
                var target = Path.Combine(folder, name);
                AtomicFile.WriteNew(target, bytes); // never replaces a file that appeared meanwhile
                return target;
            }
            if (Path.GetFullPath(existing).Equals(fullSource, StringComparison.Ordinal)) return existing;
            if (File.Exists(existing) && new FileInfo(existing).LinkTarget is null && FileHash.HexEquals(FileHash.Sha256Hex(existing), sha))
                return existing;
        }
    }

    // The file of that name in the folder in any case (Wine's lookup), or null.
    private static string? ExistingCaseInsensitive(string folder, string name)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(folder)
                .FirstOrDefault(path => Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Path.Combine(folder, name) is var exact && Path.Exists(exact) ? exact : null;
        }
    }

    // ---- one game ----------------------------------------------------------------------------------

    /// <summary>The presets RHI installed in the game (from its ReShade record).</summary>
    public IReadOnlyList<InstalledReShadePreset> Installed(DetectedGame game)
        => Read(records.Find(game, ManagedComponentKind.ReShade));

    internal static IReadOnlyList<InstalledReShadePreset> Read(LinuxDeploymentRecord? record)
    {
        if (record?.Metadata.GetValueOrDefault(MetadataKey) is not { Length: > 0 } json) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<InstalledReShadePreset>>(json) ?? [])
                .Where(p => p is { FileName.Length: > 0 } && Path.GetFileName(p.FileName) == p.FileName)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Copies <paramref name="presetPath"/> beside the game's ReShade and selects it in ReShade.ini. Refused before any
    /// write where an install would be, without an RHI ReShade, or when the name is taken by a file RHI does not own.
    /// The shader packs it needs are worked out apart (<see cref="RequiredEffects"/> + <see cref="Resolve"/>): that needs
    /// the packs' file lists, which may download.
    /// </summary>
    public async Task<ComponentOperationResult> InstallAsync(DetectedGame game, string presetPath, CancellationToken cancellationToken = default)
    {
        static ComponentOperationResult Refused(string message) => ComponentOperationResult.Fail(message);
        try { ReadPreset(presetPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return Refused(ex.Message); }
        if (writeRefusal(game) is { } refused) return Refused(refused);
        var record = records.Find(game, ManagedComponentKind.ReShade);
        if (record is null || string.IsNullOrWhiteSpace(game.BinaryDirectory) || !Directory.Exists(game.BinaryDirectory))
            return Refused(NoManagedReShadePrefix + game.Name);

        // Wine finds names in any case: a game file whose name differs only in case is the same name (review finding 4).
        var target = ExistingCaseInsensitive(game.BinaryDirectory, Path.GetFileName(presetPath)) ?? Path.Combine(game.BinaryDirectory, Path.GetFileName(presetPath));
        var fileName = Path.GetFileName(target);
        byte[] bytes;
        try { bytes = File.ReadAllBytes(presetPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Refused(ex.Message); }
        var installed = Read(record).ToList();
        var previous = installed.FirstOrDefault(p => p.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        if (File.Exists(target) || new FileInfo(target).LinkTarget is not null)
        {
            var current = new FileInfo(target).LinkTarget is null ? FileHash.Sha256Hex(target) : null;
            var ours = previous is not null && current is not null && FileHash.HexEquals(current, previous.Sha256);
            var same = current is not null && FileHash.HexEquals(current, FileHash.Sha256HexOf(bytes));
            if (!ours && !same) return Refused(NameTakenPrefix + target);
        }
        if (XboxWriteGate.Refusal(target) is { } protectedFile) return Refused(protectedFile);
        // #213: ReShade.ini may link to the user's shared file in ~/.config/ReShade (the one ReShade.ini link rule): the
        // journal tracks the file the write replaces, never the link, so a rollback keeps the link and puts the shared
        // file back. A link the rule refuses is refused here, before anything is written.
        var ini = ReShadeIniManager.ResolveIniPath(game.BinaryDirectory);
        string written;
        try { written = ReShadeIniManager.ResolveWriteTarget(ini, game.BinaryDirectory, game.InstallPath); }
        catch (IOException ex) { return Refused(ex.Message); }

        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(journal, $"ReShade preset: {game.Name}", game, ManagedComponentKind.ReShade, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return Refused(journalRefusal);
        await using var scope = opened!;
        try
        {
            cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
            var sha = await scope.PlaceBytesAsync(target, bytes, cancellationToken).ConfigureAwait(false);
            await scope.TrackAsync(written, cancellationToken).ConfigureAwait(false);
            ReShadeIniManager.SetPresetPath(game.BinaryDirectory, fileName, game.InstallPath);
            await scope.DeclareWrittenContentAsync(written, cancellationToken).ConfigureAwait(false);

            installed.RemoveAll(p => p.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
            installed.Add(new InstalledReShadePreset(fileName, sha));
            // Find hands out a copy: changing it touches the ledger only through Upsert, after the files are in place.
            record.Metadata[MetadataKey] = JsonSerializer.Serialize(installed);
            records.Upsert(scope.Stamp(record));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            return Refused(ex.Message + DeploymentScope.RollbackNote(rollback));
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok(InstalledPrefix + target, target);
    }

    /// <summary>
    /// Takes an installed preset back: the file is deleted while it still holds what RHI wrote (a copy the user changed
    /// stays), ReShade.ini's PresetPath is cleared when it still names it, and the record forgets it.
    /// </summary>
    public async Task<ComponentOperationResult> RemoveAsync(DetectedGame game, string fileName, CancellationToken cancellationToken = default)
    {
        if (writeRefusal(game) is { } refused) return ComponentOperationResult.Fail(refused);
        var record = records.Find(game, ManagedComponentKind.ReShade);
        var installed = Read(record).ToList();
        var preset = installed.FirstOrDefault(p => p.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        if (record is null || preset is null || string.IsNullOrWhiteSpace(game.BinaryDirectory))
            return ComponentOperationResult.Fail(NotInstalledPrefix + fileName);
        var target = Path.Combine(game.BinaryDirectory, preset.FileName);
        var unchanged = File.Exists(target) && new FileInfo(target).LinkTarget is null && FileHash.HexEquals(FileHash.Sha256Hex(target), preset.Sha256);
        // #213: the file a PresetPath edit replaces (a linked shared file, never the link). A link the rule refuses was
        // made by the user since (RHI never writes through it): it is left as it is, and the preset still comes out.
        var ini = ReShadeIniManager.ResolveIniPath(game.BinaryDirectory);
        string? written;
        try { written = ReShadeIniManager.ResolveWriteTarget(ini, game.BinaryDirectory, game.InstallPath); }
        catch (IOException) { written = null; }

        var (opened, journalRefusal) = await DeploymentScope.BeginAsync(journal, $"ReShade preset removal: {game.Name}", game, ManagedComponentKind.ReShade, cancellationToken).ConfigureAwait(false);
        if (journalRefusal is not null) return ComponentOperationResult.Fail(journalRefusal);
        await using var scope = opened!;
        try
        {
            cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
            if (unchanged) await scope.DeleteAsync(target, cancellationToken).ConfigureAwait(false);
            if (written is not null && File.Exists(written))
            {
                await scope.TrackAsync(written, cancellationToken).ConfigureAwait(false);
                if (ReShadeIniManager.ClearPresetPath(game.BinaryDirectory, preset.FileName, game.InstallPath))
                    await scope.DeclareWrittenContentAsync(written, cancellationToken).ConfigureAwait(false);
            }
            installed.Remove(preset);
            if (installed.Count == 0) record.Metadata.Remove(MetadataKey);
            else record.Metadata[MetadataKey] = JsonSerializer.Serialize(installed);
            records.Upsert(scope.Stamp(record));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            return ComponentOperationResult.Fail(ex.Message + DeploymentScope.RollbackNote(rollback));
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok((unchanged ? RemovedPrefix : KeptChangedPrefix) + target);
    }
}
