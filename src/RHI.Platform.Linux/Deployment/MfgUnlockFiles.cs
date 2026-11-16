using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>MFG Unlock's frame generation method (its ReShade panel's setting; upstream 2.8.9 ⚙).</summary>
public enum MfgUnlockMethod { DlssG = 0, XeMfgCuda = 1, XeMfgViaDlssG = 2 }

/// <summary>What MFG Unlock can do with the game's graphics API (upstream's ⚙ warning, as of the mod's 1.8.0).</summary>
public enum MfgUnlockApiSupport { Supported, VulkanExperimental, NotSupported }

/// <summary>One of the Intel files MFG Unlock's XeMFG modes need, as found in the game folder (⚙ status).</summary>
public sealed record MfgUnlockXessFile(string Name, IntelXessFileState State, string? Version);

/// <summary>
/// #279 (upstream 2.8.9 <c>BuildMfgAdaCogContent</c>): MFG Unlock's <c>MFGAdaUnlock.ini</c> in the game's executable folder
/// (the mod reads it from there, not from the add-on folder). Values as upstream writes them: the method is
/// <c>[Backend] Selected</c> (<c>DLSSG</c>/<c>XeMFG</c>) plus <c>[DLSSG] XmxGenerator</c> (<c>0</c>/<c>1</c>); DLSS-SR input is
/// <c>[Adapters] DlssSr</c>. A missing file or value reads as the mod's default (NVIDIA DLSS-G, DLSS-SR off); a write creates
/// the file. Lines, comments and keys RHI does not set are kept.
/// </summary>
public static class MfgUnlockIni
{
    public const string FileName = "MFGAdaUnlock.ini";

    public static string PathIn(string binaryDirectory) => Path.Combine(binaryDirectory, FileName);

    public static MfgUnlockMethod ReadMethod(string binaryDirectory)
    {
        var lines = Lines(binaryDirectory);
        if (string.Equals(IniLines.Get(lines, "Backend", "Selected")?.Trim(), "XeMFG", StringComparison.OrdinalIgnoreCase)) return MfgUnlockMethod.XeMfgCuda;
        return IniLines.Get(lines, "DLSSG", "XmxGenerator")?.Trim() == "1" ? MfgUnlockMethod.XeMfgViaDlssG : MfgUnlockMethod.DlssG;
    }

    public static bool ReadDlssSr(string binaryDirectory) => IniLines.Get(Lines(binaryDirectory), "Adapters", "DlssSr")?.Trim() == "1";

    /// <summary>Writes <paramref name="method"/> (both keys, as upstream). Throws <see cref="IOException"/> when the file cannot be written.</summary>
    public static void WriteMethod(string binaryDirectory, MfgUnlockMethod method)
    {
        var lines = Lines(binaryDirectory);
        IniLines.Set(lines, "Backend", "Selected", method == MfgUnlockMethod.XeMfgCuda ? "XeMFG" : "DLSSG");
        IniLines.Set(lines, "DLSSG", "XmxGenerator", method == MfgUnlockMethod.XeMfgViaDlssG ? "1" : "0");
        IniLines.Write(PathIn(binaryDirectory), lines, [binaryDirectory]);
    }

    public static void WriteDlssSr(string binaryDirectory, bool on)
    {
        var lines = Lines(binaryDirectory);
        IniLines.Set(lines, "Adapters", "DlssSr", on ? "1" : "0");
        IniLines.Write(PathIn(binaryDirectory), lines, [binaryDirectory]);
    }

    private static List<string> Lines(string binaryDirectory)
        => File.Exists(PathIn(binaryDirectory)) ? IniLines.Read(PathIn(binaryDirectory)) : [];

    /// <summary>
    /// The ⚙ warning (upstream, as of MFG Unlock 1.8.0): DirectX 12 games are supported (a dual-API game with DX12 too),
    /// Vulkan is experimental, anything else (DX8–DX11, OpenGL) is not.
    /// </summary>
    public static MfgUnlockApiSupport ApiSupport(DetectedGame game)
    {
        var apis = new HashSet<GraphicsApi>(game.EffectiveGraphicsApis) { game.EffectiveGraphicsApi };
        if (apis.Contains(GraphicsApi.DirectX12)) return MfgUnlockApiSupport.Supported;
        if (apis.Contains(GraphicsApi.Vulkan)) return MfgUnlockApiSupport.VulkanExperimental;
        return apis.Any(api => api is GraphicsApi.DirectX8 or GraphicsApi.DirectX9 or GraphicsApi.DirectX10 or GraphicsApi.DirectX11 or GraphicsApi.OpenGL)
            ? MfgUnlockApiSupport.NotSupported
            : MfgUnlockApiSupport.Supported;
    }
}

/// <summary>
/// #279 (upstream 2.8.9 "XeSS runtime for MFG Ada Unlock"): MFG Unlock's XeMFG modes need Intel's <c>libxess_fg.dll</c>
/// and <c>libxell.dll</c> in the game's executable folder. Installing the mod places them there; a game copy of another
/// build (Of Ash and Steel ships one) is kept as <c>&lt;file&gt;.mfgada-original</c> and put back on removal. Each step is
/// its own journaled operation whose commit point is MFG Unlock's record (the files are listed in it); the add-on itself
/// is installed, updated and removed by its own service. Installs from before get the files through Update / Update All.
/// </summary>
public sealed class MfgUnlockFiles(IntelXessFiles xess, LinuxDeploymentRecordStore records, DeploymentJournal? journal)
{
    public const string BackupSuffix = ".mfgada-original";
    private const string Prefix = "MfgUnlock";

    /// <summary>The builds MFG Unlock accepts (upstream <c>GetAdaXessState</c>): libxess_fg 1.3.1.78, libxell 1.3.2.10 or 1.3.0.5.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> AcceptedVersions = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
    {
        ["libxess_fg.dll"] = ["1.3.1.78"],
        ["libxell.dll"] = ["1.3.2.10", "1.3.0.5"],
    };

    // Fixed messages, values last (PT-BR presentation pairs in UiLocalizationService).
    public const string PlacedMessage = " Intel's XeSS files for XeMFG are in the game folder.";
    public const string NotPlacedPrefix = " Intel's XeSS files for XeMFG were not installed (Update adds them): ";
    public const string DueMessage = "MFG Unlock is up to date, but Intel's XeSS files for its XeMFG modes are not installed yet; Update adds them.";
    public const string RestoreFailedPrefix = "MFG Unlock was not removed: Intel's XeSS files could not be put back first: ";

    /// <summary>Whether RHI's MFG Unlock record has had its XeSS step (installs from before #279 have not).</summary>
    public bool NeedsFiles(LinuxDeploymentRecord record) => !IntelXessFiles.HasEntries(record.Metadata, Prefix);

    /// <summary>The Intel files in <paramref name="binaryDirectory"/>, for the ⚙'s status line.</summary>
    public IReadOnlyList<MfgUnlockXessFile> State(string binaryDirectory)
        => xess.FrameGenerationFiles.Select(file =>
        {
            var path = Path.Combine(binaryDirectory, file.Name);
            if (!File.Exists(path)) return new MfgUnlockXessFile(file.Name, IntelXessFileState.Missing, null);
            var version = IntelXessFiles.VersionOf(path);
            var accepted = IntelXessFiles.StateOf(file, path) == IntelXessFileState.Accepted
                           || version is not null && AcceptedVersions.TryGetValue(file.Name, out var ok) && ok.Contains(version);
            return new MfgUnlockXessFile(file.Name, accepted ? IntelXessFileState.Accepted : IntelXessFileState.OtherVersion, version);
        }).ToArray();

    /// <summary>
    /// After an install or update: places the files (journaled, MFG Unlock's record is the commit point). Returns the
    /// message tail; a download or placement that fails is named, never turns the add-on's result into a failure.
    /// </summary>
    public async Task<string> PlaceAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        if (records.Find(game, ManagedComponentKind.MfgAdaUnlock) is not { } record || !NeedsFiles(record)) return "";
        IReadOnlyDictionary<IntelXessFile, string> cache;
        try { cache = await xess.EnsureCachedAsync(xess.FrameGenerationFiles, cancellationToken).ConfigureAwait(false); }
        catch (InvalidDataException ex) { return NotPlacedPrefix + ex.Message; }

        var (opened, refusal) = await DeploymentScope.BeginAsync(journal, "MFG Unlock XeSS files: " + game.Name, game, ManagedComponentKind.MfgAdaUnlock, cancellationToken).ConfigureAwait(false);
        if (refusal is not null) return NotPlacedPrefix + refusal;
        await using var scope = opened!;
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        IntelXessPlaced placed;
        try
        {
            var steps = xess.FrameGenerationFiles.Select(file => new IntelXessStep(file, Path.Combine(record.BinaryDirectory, file.Name),
                Path.Combine(record.BinaryDirectory, file.Name + BackupSuffix))).ToArray();
            placed = await IntelXessFiles.PlaceAsync(scope, steps, cache, cancellationToken).ConfigureAwait(false);
            var updated = LinuxDeploymentRecordStore.Clone(record);
            IntelXessFiles.MarkDone(updated.Metadata, Prefix, placed.Entries);
            records.Upsert(scope.Stamp(updated));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (rollback.Committed) return PlacedMessage;
            return NotPlacedPrefix + ex.Message + DeploymentScope.RollbackNote(rollback);
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return PlacedMessage + IntelXessFiles.Notes(placed);
    }

    /// <summary>
    /// Before the add-on's removal: the game's own copies come back and RHI's files go (journaled; the record, without
    /// its file list, is the commit point). Null when it went well (or there was nothing to do), else the refusal: the
    /// add-on is then not removed, so nothing RHI placed is left without a record.
    /// </summary>
    public async Task<(string? Refusal, string Notes)> RestoreAsync(DetectedGame game, CancellationToken cancellationToken = default)
    {
        if (records.Find(game, ManagedComponentKind.MfgAdaUnlock) is not { } record) return (null, "");
        var entries = IntelXessFiles.Load(record.Metadata, Prefix);
        if (entries.Count == 0) return (null, "");
        var (opened, refusal) = await DeploymentScope.BeginAsync(journal, "MFG Unlock XeSS files back: " + game.Name, game, ManagedComponentKind.MfgAdaUnlock, cancellationToken).ConfigureAwait(false);
        if (refusal is not null) return (RestoreFailedPrefix + refusal, "");
        await using var scope = opened!;
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        IntelXessRemoved removed;
        try
        {
            removed = await IntelXessFiles.RemoveAsync(scope, entries, cancellationToken).ConfigureAwait(false);
            var updated = LinuxDeploymentRecordStore.Clone(record);
            IntelXessFiles.MarkDone(updated.Metadata, Prefix, []);
            records.Upsert(scope.Stamp(updated));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (!rollback.Committed) return (RestoreFailedPrefix + ex.Message + DeploymentScope.RollbackNote(rollback), "");
            return (null, "");
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return (null, IntelXessFiles.Notes(removed));
    }
}
