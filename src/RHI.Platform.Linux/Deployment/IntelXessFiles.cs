using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

/// <summary>One file of Intel's XeSS SDK as RHI installs it: the SDK path, its size and pinned SHA-256.</summary>
public sealed record IntelXessFile(string Name, string RepoPath, long Size, string Sha256)
{
    /// <summary>The file at the pinned SDK tag on Intel's repository (upstream's source).</summary>
    public string Url => $"https://raw.githubusercontent.com/intel/xess/{IntelXessFiles.SdkTag}/{RepoPath}";

    public bool IsDll => Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
}

/// <summary>How RHI placed one XeSS file: a file the folder did not have, or the game's own copy replaced in place.</summary>
public enum IntelXessPlacement { Added, Replaced }

/// <summary>One XeSS file RHI placed for a component (kept in that component's record).</summary>
public sealed class IntelXessEntry
{
    public string File { get; set; } = "";
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public IntelXessPlacement Placement { get; set; }
    /// <summary>Replaced only: where the game's own copy is kept until removal.</summary>
    public string? Backup { get; set; }
}

/// <summary>One step of a placement: <paramref name="Target"/> gets <paramref name="File"/>.</summary>
/// <param name="Backup">
/// Null: add the file only where the folder has none (a file already there that is not RHI's is the user's or the
/// game's and is left alone). Set: the game's own copy at <paramref name="Target"/> is replaced in place and kept under
/// this name until removal (an identical copy is left as it is).
/// </param>
public sealed record IntelXessStep(IntelXessFile File, string Target, string? Backup = null);

/// <summary>What a placement did: the entries to record, and the files it left alone, with why.</summary>
public sealed record IntelXessPlaced(IReadOnlyList<IntelXessEntry> Entries, IReadOnlyList<string> KeptUserFiles, IReadOnlyList<string> BackupInTheWay);

/// <summary>What a removal did: the entries it could not undo (left on disk, named), for the caller's message.</summary>
public sealed record IntelXessRemoved(IReadOnlyList<string> ChangedLeft, IReadOnlyList<string> BackupsLeft);

/// <summary>The state of one XeSS file a component needs (⚙ status lines).</summary>
public enum IntelXessFileState { Missing, Accepted, OtherVersion }

/// <summary>
/// w14/extras-fg: Intel's XeSS SDK files the frame-generation mods need (upstream 2.8.9–2.9.3 <c>Rtx40MfgService</c> XeSS
/// staging and its RTX Encore part): Universal MFG Unlocker's XeMFG standalone mode (#278), MFG Unlock's XeMFG modes
/// (#279) and RTX Encore's XeSS upscaler and XeFG engine (#280). One shared place for the download and the file moves:
/// <list type="bullet">
/// <item>Downloads come from Intel's repository at SDK <see cref="SdkTag"/> (upstream's source) under the
/// <see cref="DownloadSources.IntelXess"/> policy, and each file must match its pinned SHA-256 (upstream pins them for
/// RTX Encore only; RHI Linux checks them for every mod, fail-closed). A DLL must also be a 64-bit Windows DLL.</item>
/// <item><see cref="PlaceAsync"/> runs inside the component's journaled <see cref="DeploymentScope"/>: a step adds a file
/// the folder lacks (a file already there that RHI did not place is left alone), or replaces the game's own copy in place
/// after copying it to a backup name. An existing backup is never overwritten: that step is skipped and named.</item>
/// <item><see cref="RemoveAsync"/> undoes only what still holds RHI's bytes: an added file is deleted, a replaced one gets
/// the game's copy back from its backup. A file changed since (a game update) is left as it is, with its backup, and
/// named.</item>
/// </list>
/// The entries go in the component's record metadata (<see cref="Save"/>/<see cref="Load"/>).
/// </summary>
public sealed class IntelXessFiles
{
    public const string SdkTag = "v3.0.2";

    public static readonly IntelXessFile LibXess = new("libxess.dll", "bin/libxess.dll", 77795704,
        "251659DD84A3E84DE67C886A4186E01F3ECA49B00641906FE38BB6B807E5D5B7");
    public static readonly IntelXessFile LibXessFg = new("libxess_fg.dll", "bin/libxess_fg.dll", 22957432,
        "EC5E0C65E075570C6EDE72618BB666D0BE0C2E10B2EA9762C0FE8CB8E375AB27");
    public static readonly IntelXessFile LibXell = new("libxell.dll", "bin/libxell.dll", 415368,
        "D2030DCD694FDA8F2EC7E044B13E6DB8F0B56D4BA9113A5EFAD334E3F3DED8C7");
    /// <summary>Intel's licence (RTX Encore's folder gets it as <c>license.txt</c>, as RTX Encore Hub does).</summary>
    public static readonly IntelXessFile License = new("LICENSE.txt", "LICENSE.txt", 4194,
        "784BE42C39A4A4D03CF85D88E15E59D75828FCA773CE45B8DBFFFBD7EAF208DF");

    /// <summary>The two files XeSS frame generation (XeMFG) needs: Universal MFG Unlocker and MFG Unlock.</summary>
    public static readonly IReadOnlyList<IntelXessFile> FrameGeneration = [LibXessFg, LibXell];

    /// <summary>RTX Encore's three DLLs (XeSS upscaler and XeFG engine).</summary>
    public static readonly IReadOnlyList<IntelXessFile> Encore = [LibXess, LibXessFg, LibXell];

    // Fixed messages, values last (PT-BR presentation pairs in UiLocalizationService).
    public const string DownloadFailedPrefix = "Intel's XeSS files could not be downloaded: ";
    public const string NotX64Prefix = "This Intel XeSS file is not a 64-bit Windows DLL: ";
    public const string KeptUserFilesPrefix = " Intel XeSS files that were already there and are not RHI's were left as they are: ";
    public const string BackupInTheWayPrefix = " These Intel XeSS files were not replaced, because a backup of the game's copy is already there: ";
    public const string ChangedLeftPrefix = " These Intel XeSS files changed since RHI placed them, so they were left as they are: ";
    public const string BackupsLeftPrefix = " The game's own copies stay in these backups, because the file changed since RHI placed it: ";

    private readonly HttpClient _http;
    private readonly string _cacheRoot;

    public IntelXessFiles(HttpClient http, string? cacheRoot = null)
    {
        _http = http;
        _cacheRoot = cacheRoot ?? System.IO.Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "intel-xess");
    }

    /// <summary>The files XeMFG needs, as this instance places them (<see cref="FrameGeneration"/>; tests substitute small files).</summary>
    public IReadOnlyList<IntelXessFile> FrameGenerationFiles { get; init; } = FrameGeneration;

    /// <summary>RTX Encore's DLLs, as this instance places them (<see cref="Encore"/>; tests substitute small files).</summary>
    public IReadOnlyList<IntelXessFile> EncoreFiles { get; init; } = Encore;

    /// <summary>Intel's licence, as this instance places it (<see cref="License"/>).</summary>
    public IntelXessFile LicenseFile { get; init; } = License;

    /// <summary>The cache path of <paramref name="file"/>.</summary>
    public string CachePath(IntelXessFile file) => System.IO.Path.Combine(_cacheRoot, SdkTag, file.Name);

    /// <summary>
    /// Downloads (or reuses from the cache) every file in <paramref name="files"/>, each checked against its pinned
    /// SHA-256. Returns the verified cache path per file. Throws <see cref="InvalidDataException"/> (the message starts with
    /// <see cref="DownloadFailedPrefix"/>) when one cannot be had; nothing in a game folder has changed then.
    /// </summary>
    public async Task<IReadOnlyDictionary<IntelXessFile, string>> EnsureCachedAsync(IEnumerable<IntelXessFile> files, CancellationToken cancellationToken = default)
    {
        var cached = new Dictionary<IntelXessFile, string>();
        foreach (var file in files.Distinct())
        {
            var entry = CachePath(file);
            try
            {
                await TrustedDownload.EnsureVerifiedAsync(_http, file.Url, entry, DownloadSources.IntelXess, SdkTag, file.Sha256,
                    validate: file.IsDll ? (path, _) => { RequireX64(path, file); return Task.CompletedTask; } : null,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UntrustedDownloadException
                                           or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new InvalidDataException(DownloadFailedPrefix + file.Name + ": " + ex.Message, ex);
            }
            cached[file] = entry;
        }
        return cached;
    }

    private static void RequireX64(string path, IntelXessFile file)
    {
        if (PeArchitectureDetector.Detect(path) != PeArchitecture.X64)
            throw new InvalidDataException(NotX64Prefix + file.Name);
    }

    /// <summary>
    /// Places the <paramref name="steps"/> inside <paramref name="scope"/> (each change tracked first, so a failure or a
    /// crash undoes it with the component's own operation). Folders a step needs are created; a folder RHI created is
    /// recorded so removal can take it away again when empty.
    /// </summary>
    public static async Task<IntelXessPlaced> PlaceAsync(
        DeploymentScope scope, IReadOnlyList<IntelXessStep> steps, IReadOnlyDictionary<IntelXessFile, string> cached, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var entries = new List<IntelXessEntry>();
        var kept = new List<string>();
        var backupInTheWay = new List<string>();
        foreach (var step in steps)
        {
            var source = cached[step.File];
            var sha = step.File.Sha256;
            if (File.Exists(step.Target))
            {
                var current = await FileHash.Sha256HexAsync(step.Target, cancellationToken).ConfigureAwait(false);
                if (step.Backup is null)
                {
                    // Already the file RHI places (an earlier install, or the same file the user put there): nothing to do,
                    // and nothing RHI owns. Anything else is not RHI's and stays.
                    if (!FileHash.HexEquals(current, sha)) kept.Add(step.Target);
                    continue;
                }
                if (FileHash.HexEquals(current, sha)) continue; // the game already ships this exact file
                if (File.Exists(step.Backup) || Directory.Exists(step.Backup))
                {
                    backupInTheWay.Add(step.Backup);
                    continue;
                }
                await scope.PlaceCopyAsync(step.Target, step.Backup, cancellationToken, current, overwrite: false).ConfigureAwait(false);
                await scope.PlaceCopyAsync(source, step.Target, cancellationToken, sha).ConfigureAwait(false);
                entries.Add(new IntelXessEntry { File = step.File.Name, Path = step.Target, Sha256 = sha, Placement = IntelXessPlacement.Replaced, Backup = step.Backup });
                continue;
            }
            var folder = System.IO.Path.GetDirectoryName(step.Target)!;
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
                entries.Add(new IntelXessEntry { File = "", Path = folder, Placement = IntelXessPlacement.Added });
            }
            await scope.PlaceCopyAsync(source, step.Target, cancellationToken, sha, overwrite: false).ConfigureAwait(false);
            entries.Add(new IntelXessEntry { File = step.File.Name, Path = step.Target, Sha256 = sha, Placement = IntelXessPlacement.Added });
        }
        return new IntelXessPlaced(entries, kept, backupInTheWay);
    }

    /// <summary>
    /// Undoes <paramref name="entries"/> inside <paramref name="scope"/>: an added file still holding RHI's bytes is
    /// deleted; a replaced one still holding them gets the game's copy back and its backup goes. A file changed since is
    /// left as it is (and so is its backup). Folders RHI created go after the commit (<see cref="DeleteEmptyFolders"/>): the
    /// scope keeps its snapshots beside the files until then.
    /// </summary>
    public static async Task<IntelXessRemoved> RemoveAsync(DeploymentScope scope, IReadOnlyList<IntelXessEntry> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var changed = new List<string>();
        var backupsLeft = new List<string>();
        foreach (var entry in entries.Where(e => e.File.Length > 0))
        {
            if (!File.Exists(entry.Path))
            {
                // Gone (the user or the game deleted it): a replaced file's backup is the game's copy, put it back.
                if (entry.Placement == IntelXessPlacement.Replaced && entry.Backup is { } gone && File.Exists(gone))
                {
                    await scope.PlaceCopyAsync(gone, entry.Path, cancellationToken, overwrite: false).ConfigureAwait(false);
                    await scope.DeleteAsync(gone, cancellationToken).ConfigureAwait(false);
                }
                continue;
            }
            var current = await FileHash.Sha256HexAsync(entry.Path, cancellationToken).ConfigureAwait(false);
            if (!FileHash.HexEquals(current, entry.Sha256))
            {
                changed.Add(entry.Path);
                if (entry.Backup is { } kept && File.Exists(kept)) backupsLeft.Add(kept);
                continue;
            }
            if (entry.Placement == IntelXessPlacement.Replaced && entry.Backup is { } backup && File.Exists(backup))
            {
                await scope.PlaceCopyAsync(backup, entry.Path, cancellationToken).ConfigureAwait(false);
                await scope.DeleteAsync(backup, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await scope.DeleteAsync(entry.Path, cancellationToken).ConfigureAwait(false);
            }
        }
        return new IntelXessRemoved(changed, backupsLeft);
    }

    /// <summary>
    /// After a removal's commit: the folders RHI created for <paramref name="entries"/>, deepest first, when they are
    /// empty. Never throws (a folder that cannot go stays).
    /// </summary>
    public static void DeleteEmptyFolders(IReadOnlyList<IntelXessEntry> entries)
    {
        foreach (var folder in entries.Where(e => e.File.Length == 0).Select(e => e.Path).OrderByDescending(p => p.Length))
        {
            try
            {
                if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The message tail of a placement (files left alone), "" when there is none.</summary>
    public static string Notes(IntelXessPlaced placed)
        => (placed.KeptUserFiles.Count > 0 ? KeptUserFilesPrefix + string.Join(", ", placed.KeptUserFiles) : "")
           + (placed.BackupInTheWay.Count > 0 ? BackupInTheWayPrefix + string.Join(", ", placed.BackupInTheWay) : "");

    /// <summary>The message tail of a removal (files left on disk), "" when there is none.</summary>
    public static string Notes(IntelXessRemoved removed)
        => (removed.ChangedLeft.Count > 0 ? ChangedLeftPrefix + string.Join(", ", removed.ChangedLeft) : "")
           + (removed.BackupsLeft.Count > 0 ? BackupsLeftPrefix + string.Join(", ", removed.BackupsLeft) : "");

    private static string MetadataKey(string prefix) => prefix + ".IntelXess";

    /// <summary>Stores <paramref name="entries"/> in a record's metadata under <paramref name="prefix"/> (replacing what was there).</summary>
    public static void Save(IDictionary<string, string> metadata, string prefix, IReadOnlyList<IntelXessEntry> entries)
    {
        if (entries.Count == 0) metadata.Remove(MetadataKey(prefix));
        else metadata[MetadataKey(prefix)] = JsonSerializer.Serialize(entries);
    }

    /// <summary>The entries a record holds under <paramref name="prefix"/>; empty for a record from before (or an unreadable value).</summary>
    public static IReadOnlyList<IntelXessEntry> Load(IReadOnlyDictionary<string, string> metadata, string prefix)
    {
        if (!metadata.TryGetValue(MetadataKey(prefix), out var json) || string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<IntelXessEntry>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    /// <summary>Whether a record has placed (or deliberately recorded) XeSS files under <paramref name="prefix"/>.</summary>
    public static bool HasEntries(IReadOnlyDictionary<string, string> metadata, string prefix) => metadata.ContainsKey(MetadataKey(prefix));

    /// <summary>Copies every component's XeSS entries from <paramref name="previous"/> into a record rebuilt by an update.</summary>
    public static void CarryMetadata(LinuxDeploymentRecord? previous, LinuxDeploymentRecord rebuilt)
    {
        if (previous is null) return;
        foreach (var (key, value) in previous.Metadata.Where(pair => pair.Key.EndsWith(".IntelXess", StringComparison.OrdinalIgnoreCase)))
            rebuilt.Metadata.TryAdd(key, value);
    }

    /// <summary>Marks a record as having had its XeSS step, even when nothing needed placing (an existing install is then not asked again).</summary>
    public static void MarkDone(IDictionary<string, string> metadata, string prefix, IReadOnlyList<IntelXessEntry> entries)
        => metadata[MetadataKey(prefix)] = JsonSerializer.Serialize(entries);

    /// <summary>The state of the file at <paramref name="path"/>: RHI's exact file, another version, or none.</summary>
    public static IntelXessFileState StateOf(IntelXessFile file, string path)
    {
        if (!File.Exists(path)) return IntelXessFileState.Missing;
        try { return FileHash.HexEquals(FileHash.Sha256Hex(path), file.Sha256) ? IntelXessFileState.Accepted : IntelXessFileState.OtherVersion; }
        catch (IOException) { return IntelXessFileState.Missing; }
        catch (UnauthorizedAccessException) { return IntelXessFileState.Missing; }
    }

    /// <summary>The file's version (the PE version resource), for "other version" lines; null when it has none.</summary>
    public static string? VersionOf(string path)
        => PeVersionReader.Read(path) is { } info ? info.FixedFileVersion?.ToString() ?? info.FileVersion : null;

    /// <summary>Whether <paramref name="path"/> is one of Intel's XeSS files (its version resource names Intel), as upstream's scan checks.</summary>
    public static bool IsIntelFile(string path)
        => PeVersionReader.Read(path)?.CompanyName?.Contains("Intel", StringComparison.OrdinalIgnoreCase) == true;
}
