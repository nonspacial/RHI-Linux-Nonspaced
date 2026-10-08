using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>A DLSS DLL that the Control UE step changed, with what is needed to put the previous state back.</summary>
internal sealed record ControlUeOwnedDll(
    string Path,
    string Version,
    string AppliedSha256,
    bool ExistedBefore,
    bool OriginalBackupCreatedByRhi,
    string? PreviousSnapshotPath);

/// <summary>renderer.ini edit: the exact pre-edit bytes plus the key value they held.</summary>
internal sealed record ControlUeRendererIniEdit(
    string Path,
    string OriginalBase64,
    string AppliedSha256,
    string PreviousValue);

internal sealed record ControlUePostInstallState(
    IReadOnlyList<ControlUeOwnedDll> Dlls,
    ControlUeRendererIniEdit? RendererIni)
{
    [JsonIgnore]
    public bool IsEmpty => Dlls.Count == 0 && RendererIni is null;
}

internal sealed record ControlUeSources(
    (string Path, string Version)? SuperResolution,
    (string Path, string Version)? RayReconstruction,
    IReadOnlyList<string> Notes);

internal sealed record ControlUePostInstallResult(ControlUePostInstallState? State, IReadOnlyList<string> Notes);

/// <summary>Restore outcome: whatever could not be put back stays owned in <see cref="Remaining"/>.</summary>
internal sealed record ControlUeRestoreResult(ControlUePostInstallState? Remaining, IReadOnlyList<string> Notes);

/// <summary>
/// Linux port of upstream RHI 2.7.9 <c>ControlUePostInstallService</c>. When the Control Ultimate
/// Edition RR add-on (<c>renodx-control-rr.addon64</c>) is installed, upstream also upgrades
/// <c>nvngx_dlss.dll</c>, deploys <c>nvngx_dlssd.dll</c>, sets <c>"m_eHDRPreset": 2</c> in
/// <c>renderer.ini</c> and clears the DLSS SR preset in the NVIDIA driver profile. All three
/// files live in the game folder (Steam/Epic/GOG), not in the Wine prefix. The driver-profile step
/// has no Wine/Proton equivalent and is skipped. Unlike upstream, every change is recorded on the
/// RenoDX record and undone when RenoDX is removed; files changed outside RHI since are left alone.
/// </summary>
public sealed class ControlUePostInstallService
{
    public const string MetadataKey = "ControlUe.PostInstall";
    public const string TriggerAddonFileName = "renodx-control-rr.addon64";
    public const string GameName = "Control Ultimate Edition";
    public const string RendererIniName = "renderer.ini";
    public const string HdrPresetValue = "2";
    internal const string PreviousSnapshotSuffix = ".rhi-control-previous";
    internal const string DriverProfileNote =
        "NVIDIA driver-profile SR preset step skipped: no driver profiles under Wine/Proton.";
    private const int MaxRendererIniBytes = 1024 * 1024;

    private static readonly Regex HdrPresetPattern = new("\"m_eHDRPreset\"\\s*:\\s*(-?\\d+)", RegexOptions.CultureInvariant);

    private readonly IFileDeploymentService _files;
    private readonly Func<DlssComponentKind, CancellationToken, Task<(string Path, string Version)?>>? _newestDll;

    public ControlUePostInstallService(IFileDeploymentService files, DlssManagerService dlss)
        : this(files, (kind, ct) => dlss.EnsureCachedVersionAsync(kind, null, ct))
    {
    }

    internal ControlUePostInstallService(
        IFileDeploymentService files,
        Func<DlssComponentKind, CancellationToken, Task<(string Path, string Version)?>>? newestDll)
    {
        _files = files;
        _newestDll = newestDll;
    }

    /// <summary>Upstream gates the confirmation dialog on the game name.</summary>
    public static bool IsControlUltimateEdition(DetectedGame game)
        => string.Equals(game.Name?.Trim(), GameName, StringComparison.OrdinalIgnoreCase)
           || string.Equals(game.CanonicalManifestName?.Trim(), GameName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Upstream runs the post-install on the add-on name; Linux also requires the title.</summary>
    public static bool Applies(DetectedGame game, string addonFileName)
        => IsControlUltimateEdition(game) && addonFileName.Equals(TriggerAddonFileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Downloads (or reuses) the newest SR and RR DLLs. Runs before any game file changes.</summary>
    internal async Task<ControlUeSources> PrepareAsync(CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        var sr = await FetchAsync(DlssComponentKind.SuperResolution, "nvngx_dlss.dll", notes, cancellationToken).ConfigureAwait(false);
        var rr = await FetchAsync(DlssComponentKind.RayReconstruction, "nvngx_dlssd.dll", notes, cancellationToken).ConfigureAwait(false);
        return new ControlUeSources(sr, rr, notes);
    }

    private async Task<(string Path, string Version)?> FetchAsync(
        DlssComponentKind kind, string dllName, List<string> notes, CancellationToken cancellationToken)
    {
        if (_newestDll is null)
        {
            notes.Add($"{dllName} skipped: DLSS manifest unavailable.");
            return null;
        }
        try
        {
            var cached = await _newestDll(kind, cancellationToken).ConfigureAwait(false);
            if (cached is { } found && File.Exists(found.Path)) return found;
            notes.Add($"{dllName} skipped: not in the DLSS manifest.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            notes.Add($"{dllName} skipped: download failed ({ex.Message}).");
        }
        return null;
    }

    /// <summary>
    /// Applies the three Linux-applicable steps. Each step stands alone, as upstream: a failure is
    /// reported and the others still run. Not cancellable once started, so a step is never left half done.
    /// </summary>
    internal async Task<ControlUePostInstallResult> ApplyAsync(DetectedGame game, string gameDirectory, ControlUeSources sources)
    {
        var notes = new List<string>(sources.Notes);
        var dlls = new List<ControlUeOwnedDll>();
        foreach (var (source, dllName) in new[] { (sources.SuperResolution, "nvngx_dlss.dll"), (sources.RayReconstruction, "nvngx_dlssd.dll") })
        {
            if (source is not { } found) continue;
            try
            {
                var owned = await DeployDllAsync(found.Path, found.Version, ResolveExisting(gameDirectory, dllName)).ConfigureAwait(false);
                if (owned is null) notes.Add($"{dllName}: already {found.Version}.");
                else
                {
                    dlls.Add(owned);
                    notes.Add($"{dllName} → {found.Version}.");
                }
            }
            catch (Exception ex)
            {
                notes.Add($"{dllName} not changed: {ex.Message}");
            }
        }

        ControlUeRendererIniEdit? ini = null;
        try
        {
            ini = PatchRendererIni(game, gameDirectory, notes);
        }
        catch (Exception ex)
        {
            notes.Add($"{RendererIniName} not changed: {ex.Message}");
        }

        notes.Add(DriverProfileNote);
        var state = new ControlUePostInstallState(dlls, ini);
        return new ControlUePostInstallResult(state.IsEmpty ? null : state, notes);
    }

    private async Task<ControlUeOwnedDll?> DeployDllAsync(string source, string version, string target)
    {
        var existed = File.Exists(target);
        var originalBackup = target + TransactionalFileDeploymentService.BackupExtension;
        var originalExisted = File.Exists(originalBackup);
        string? snapshot = null;
        if (existed && originalExisted)
        {
            // .original already holds the true original from an earlier DLSS swap. Keep the bytes
            // present right now as well, so removal returns to exactly this state.
            if (await FileHash.Sha256HexAsync(source).ConfigureAwait(false) == await FileHash.Sha256HexAsync(target).ConfigureAwait(false))
                return null;
            snapshot = target + PreviousSnapshotSuffix;
            if (File.Exists(snapshot))
                throw new IOException($"a previous snapshot already exists at {snapshot}; move it aside first.");
            AtomicFile.Copy(target, snapshot, overwrite: false);
        }

        var result = await _files.ReplaceAsync(source, target, CancellationToken.None).ConfigureAwait(false);
        if (!result.Success || !result.Changed)
        {
            if (snapshot is not null) FileCleanup.TryDelete(snapshot);
            if (!result.Success) throw new IOException(result.Error ?? "replace failed.");
            return null;
        }

        return new ControlUeOwnedDll(
            target,
            version,
            await FileHash.Sha256HexAsync(target).ConfigureAwait(false),
            existed,
            existed && !originalExisted && File.Exists(originalBackup),
            snapshot);
    }

    private static ControlUeRendererIniEdit? PatchRendererIni(DetectedGame game, string gameDirectory, List<string> notes)
    {
        var path = ResolveRendererIni(game, gameDirectory);
        if (path is null)
        {
            notes.Add($"{RendererIniName} not found in the game folder; launch the game once, then reinstall RenoDX to set the HDR preset.");
            return null;
        }
        if (new FileInfo(path).Length > MaxRendererIniBytes)
            throw new InvalidDataException("file is unexpectedly large.");

        var original = File.ReadAllBytes(path);
        var (text, bom) = Decode(original);
        var match = HdrPresetPattern.Match(text);
        if (!match.Success)
        {
            notes.Add($"{RendererIniName}: m_eHDRPreset key not found; nothing changed.");
            return null;
        }
        var previous = match.Groups[1].Value;
        if (previous == HdrPresetValue)
        {
            notes.Add($"{RendererIniName}: m_eHDRPreset already {HdrPresetValue}.");
            return null;
        }

        var patched = HdrPresetPattern.Replace(text, $"\"m_eHDRPreset\": {HdrPresetValue}");
        var bytes = Encode(patched, bom);
        AtomicFile.ReplaceUserFile(path, bytes);
        notes.Add($"{RendererIniName}: m_eHDRPreset {previous} → {HdrPresetValue}.");
        return new ControlUeRendererIniEdit(path, Convert.ToBase64String(original), FileHash.Sha256HexOf(bytes), previous);
    }

    /// <summary>
    /// Puts back what <see cref="ApplyAsync"/> changed. A file changed outside RHI since is left as
    /// it is (ownership relinquished). Never cancellable: a half-restored game must not be left behind.
    /// </summary>
    internal async Task<ControlUeRestoreResult> RestoreAsync(ControlUePostInstallState state)
    {
        var notes = new List<string>();
        var remaining = new List<ControlUeOwnedDll>();
        foreach (var dll in state.Dlls.Reverse())
        {
            try
            {
                await RestoreDllAsync(dll, notes).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                remaining.Insert(0, dll);
                notes.Add($"{Path.GetFileName(dll.Path)} could not be restored: {ex.Message}");
            }
        }

        var ini = state.RendererIni;
        if (ini is not null)
        {
            try
            {
                RestoreRendererIni(ini, notes);
                ini = null;
            }
            catch (Exception ex)
            {
                notes.Add($"{RendererIniName} could not be restored: {ex.Message}");
            }
        }

        var left = new ControlUePostInstallState(remaining, ini);
        return new ControlUeRestoreResult(left.IsEmpty ? null : left, notes);
    }

    private async Task RestoreDllAsync(ControlUeOwnedDll dll, List<string> notes)
    {
        var name = Path.GetFileName(dll.Path);
        var present = File.Exists(dll.Path);
        if (present && !string.Equals(await FileHash.Sha256HexAsync(dll.Path).ConfigureAwait(false), dll.AppliedSha256, StringComparison.OrdinalIgnoreCase))
        {
            if (dll.PreviousSnapshotPath is { } stale) FileCleanup.TryDelete(stale);
            notes.Add($"{name} was changed outside RHI and was left as it is.");
            return;
        }

        if (!dll.ExistedBefore)
        {
            if (present) File.Delete(dll.Path);
            notes.Add($"{name}: removed.");
            return;
        }

        if (dll.PreviousSnapshotPath is { } snapshot)
        {
            if (!File.Exists(snapshot))
            {
                notes.Add($"{name}: previous copy is missing ({snapshot}); left as it is.");
                return;
            }
            File.Move(snapshot, dll.Path, overwrite: true);
            notes.Add($"{name}: restored.");
            return;
        }

        if (dll.OriginalBackupCreatedByRhi)
        {
            if (!File.Exists(dll.Path + TransactionalFileDeploymentService.BackupExtension))
            {
                notes.Add($"{name}: {TransactionalFileDeploymentService.BackupExtension} backup is missing; left as it is.");
                return;
            }
            var restore = await _files.RestoreAsync(dll.Path, CancellationToken.None).ConfigureAwait(false);
            if (!restore.Success) throw new IOException(restore.Error ?? "restore failed.");
            notes.Add($"{name}: restored.");
        }
    }

    private static void RestoreRendererIni(ControlUeRendererIniEdit ini, List<string> notes)
    {
        if (!File.Exists(ini.Path))
        {
            notes.Add($"{RendererIniName} no longer exists; nothing to restore.");
            return;
        }

        var current = File.ReadAllBytes(ini.Path);
        if (string.Equals(FileHash.Sha256HexOf(current), ini.AppliedSha256, StringComparison.OrdinalIgnoreCase))
        {
            AtomicFile.ReplaceUserFile(ini.Path, Convert.FromBase64String(ini.OriginalBase64));
            notes.Add($"{RendererIniName}: restored.");
            return;
        }

        // The game rewrites renderer.ini when settings change. Keep those changes and only put the
        // preset back, and only while it still holds the value RHI wrote.
        var (text, bom) = Decode(current);
        var match = HdrPresetPattern.Match(text);
        if (!match.Success || match.Groups[1].Value != HdrPresetValue)
        {
            notes.Add($"{RendererIniName}: m_eHDRPreset was changed outside RHI and was left as it is.");
            return;
        }
        AtomicFile.ReplaceUserFile(ini.Path, Encode(HdrPresetPattern.Replace(text, $"\"m_eHDRPreset\": {ini.PreviousValue}"), bom));
        notes.Add($"{RendererIniName}: m_eHDRPreset restored to {ini.PreviousValue}; other settings kept.");
    }

    internal static string? ResolveRendererIni(DetectedGame game, string gameDirectory)
    {
        foreach (var directory in new[] { gameDirectory, game.InstallPath })
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) continue;
            var path = ResolveExisting(directory, RendererIniName);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static string ResolveExisting(string directory, string fileName)
    {
        var exact = Path.Combine(directory, fileName);
        if (File.Exists(exact)) return exact;
        try
        {
            return Directory.EnumerateFiles(directory)
                       .FirstOrDefault(path => Path.GetFileName(path).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                   ?? exact;
        }
        catch { return exact; }
    }

    internal static string Serialize(ControlUePostInstallState state) => JsonSerializer.Serialize(state);

    internal static ControlUePostInstallState? Deserialize(LinuxDeploymentRecord? record)
    {
        if (record is null || !record.Metadata.TryGetValue(MetadataKey, out var json) || string.IsNullOrWhiteSpace(json))
            return null;
        return JsonSerializer.Deserialize<ControlUePostInstallState>(json)
               ?? throw new InvalidDataException("Control UE post-install state is unreadable.");
    }

    private static (string Text, bool Bom) Decode(byte[] bytes)
    {
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return (new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), bom);
    }

    private static byte[] Encode(string text, bool bom)
    {
        var body = new UTF8Encoding(false).GetBytes(text);
        return bom ? [0xEF, 0xBB, 0xBF, .. body] : body;
    }

}
