using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>#280 (upstream 2.8.9–2.9.3): RTX Encore's per-game recommended DLL name from the RHI manifest.</summary>
public sealed partial class RtxEncoreDeploymentService
{
    /// <summary>
    /// The manifest's <c>rtxEncoreDllNames</c> name for the game (Euro Truck Simulator 2: winmm.dll, which RTX Encore
    /// 1.0.0-rc.3 needs there), when it is one of RTX Encore's DLL names; null otherwise (upstream logs and uses version.dll).
    /// </summary>
    private const string MetaBuild = "RtxEncore.Build";
    private const string AsiBuild = "asi";
    public const string SeveralAsiBuildsMessage = "The RTX Encore zip holds more than one rtx-encore.asi, so RHI did not choose one.";

    /// <summary>Where the game's own file under <paramref name="name"/> is kept: <c>&lt;stem&gt;Hooked.dll</c> for a Bink name (RTX Encore loads it from there), else <c>&lt;name&gt;.original</c>.</summary>
    internal static string KeptPathFor(string target, string name)
        => RtxEncoreNames.IsBink(name)
            ? Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(name) + "Hooked.dll")
            : target + KeptSuffix;

    private static bool IsAsiRecord(LinuxDeploymentRecord record) => RtxEncoreNames.IsAsi(record.Metadata.GetValueOrDefault(MetaName) ?? "");

    /// <summary>An .asi install made before #280 runs the renamed DLL build; Update puts the ASI build in its place.</summary>
    private static bool NeedsAsiBuild(LinuxDeploymentRecord record)
        => IsAsiRecord(record) && !string.Equals(record.Metadata.GetValueOrDefault(MetaBuild), AsiBuild, StringComparison.Ordinal);

    /// <summary>#280: <c>rtx-encore.jsonc</c> beside RHI's RTX Encore (its own folder), or null when it is not installed.</summary>
    public string? ConfigPath(DetectedGame game) => GetRecord(game) is { } record ? Path.Combine(ModDirectory(record), RtxEncoreConfig.FileName) : null;

    public static string? RecommendedName(DetectedGame game)
        => RtxEncoreNames.Canonical(game.RtxEncoreRecommendedDllName) is { } name && !RtxEncoreNames.IsAsi(name) ? name : null;
}

/// <summary>What the RTX Encore ⚙ shows about Intel's XeSS files (#280).</summary>
public sealed record RtxEncoreXessStatus(bool Installed, int ReplacedGameCopies);

/// <summary>
/// #280 (upstream 2.9.3 <c>Rtx40MfgService.EncoreIntel</c>): RTX Encore's Intel XeSS files, placed the way RTX Encore Hub
/// places them, and the move from RHI's older Neural Rendering file (310.8.0) to 310.9.16.
/// <list type="bullet">
/// <item>The game's own copies of <c>libxess.dll</c>, <c>libxess_fg.dll</c> and <c>libxell.dll</c> (Intel's version
/// resource; found under the game folder, Unreal games keep them under Engine/Plugins) are replaced where they are, each
/// kept as <c>&lt;file&gt;.encore-original</c> and put back on removal. Files the game lacks, and Intel's licence as
/// <c>license.txt</c>, go in <c>rtx-encore-runtimes/intel</c> beside RTX Encore. Files another RHI component placed,
/// folders holding <c>OptiScaler.ini</c>, RTX Encore's and Universal MFG Unlocker's own folders and links are skipped.</item>
/// <item>Installs from before get the files, and an RHI copy of the 310.8.0 runtime gets 310.9.16, through Update / Update
/// All (the check plans it), not at start-up.</item>
/// </list>
/// </summary>
public sealed partial class RtxEncoreDeploymentService
{
    public const string RuntimesFolder = "rtx-encore-runtimes/intel";
    public const string XessBackupSuffix = ".encore-original";
    private const string XessPrefix = "RtxEncore";
    private const int ScanMaxDepth = 16;
    private const int ScanMaxFolders = 20000;

    // Fixed messages (PT-BR presentation pairs in UiLocalizationService).
    public const string XessNotInstalledPrefix = " Intel's XeSS files for RTX Encore were not installed (Update adds them): ";
    public const string UpgradeDueMessage = "RTX Encore is up to date, but its Intel XeSS files or its Neural Rendering file 310.9.16 are not installed yet; Update adds them.";
    public const string UpgradedMessage = "RTX Encore: Intel XeSS files and the Neural Rendering file are up to date.";

    /// <summary>Intel's XeSS files (null: none are placed, as in tests that predate #280).</summary>
    public IntelXessFiles? Xess { get; init; }

    /// <summary>The folder RTX Encore's file is in (the game folder, or the ASI loader's plugins folder).</summary>
    private static string ModDirectory(LinuxDeploymentRecord record)
        => ProxyFile(record) is { } proxy ? Path.GetDirectoryName(proxy.Path)! : record.BinaryDirectory;

    private bool NeedsXess(LinuxDeploymentRecord record) => Xess is not null && !IntelXessFiles.HasEntries(record.Metadata, XessPrefix);

    /// <summary>An RHI copy of a Neural Rendering file other than 310.9.16 (RHI installed 310.8.0 before #280).</summary>
    private static bool NeedsNeuralRenderingUpgrade(LinuxDeploymentRecord record)
        => NeuralRenderingRuntimeOwnership.ListsRuntime(record)
           && !string.Equals(record.Metadata.GetValueOrDefault(MetaNrVersion), NeuralRenderingVersion, StringComparison.Ordinal);

    private bool NeedsUpgrade(LinuxDeploymentRecord record) => NeedsXess(record) || NeedsNeuralRenderingUpgrade(record);

    /// <summary>The ⚙'s XeSS line: whether the files are in place, and how many replace the game's own copies.</summary>
    public RtxEncoreXessStatus? XessStatus(DetectedGame game)
    {
        if (GetRecord(game) is not { } record || Xess is null) return null;
        var entries = IntelXessFiles.Load(record.Metadata, XessPrefix);
        var installed = !NeedsXess(record) && entries.Where(e => e.File.Length > 0).All(e => File.Exists(e.Path));
        return new RtxEncoreXessStatus(installed, entries.Count(e => e.Placement == IntelXessPlacement.Replaced));
    }

    private async Task<(IReadOnlyDictionary<IntelXessFile, string>? Cache, string? Failure)> CacheXessAsync(CancellationToken cancellationToken)
    {
        if (Xess is null) return (null, null);
        try { return (await Xess.EnsureCachedAsync([.. Xess.EncoreFiles, Xess.LicenseFile], cancellationToken).ConfigureAwait(false), null); }
        catch (InvalidDataException ex) { return (null, ex.Message); }
    }

    /// <summary>Where each XeSS file goes for <paramref name="game"/> (see the class summary).</summary>
    internal IReadOnlyList<IntelXessStep> XessSteps(DetectedGame game, string modDirectory)
    {
        var xess = Xess!;
        var root = !string.IsNullOrWhiteSpace(game.InstallPath) && Directory.Exists(game.InstallPath) ? game.InstallPath : modDirectory;
        var others = _records.FindAll(game)
            .Where(record => record.Component != ManagedComponentKind.RtxEncore)
            .SelectMany(record => record.Metadata.Where(pair => pair.Key.EndsWith(".IntelXess", StringComparison.OrdinalIgnoreCase))
                .SelectMany(pair => IntelXessFiles.Load(record.Metadata, pair.Key[..^".IntelXess".Length])))
            .Select(entry => Path.GetFullPath(entry.Path))
            .ToHashSet(StringComparer.Ordinal);
        var found = FindGameCopies(root, xess.EncoreFiles.Select(file => file.Name).ToArray())
            .Where(path => !others.Contains(Path.GetFullPath(path)))
            .ToLookup(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
        var privateFolder = Path.Combine(modDirectory, RuntimesFolder);
        var steps = new List<IntelXessStep>();
        foreach (var file in xess.EncoreFiles)
        {
            var copies = found[file.Name].ToArray();
            if (copies.Length > 0) steps.AddRange(copies.Select(path => new IntelXessStep(file, path, path + XessBackupSuffix)));
            else steps.Add(new IntelXessStep(file, Path.Combine(privateFolder, file.Name)));
        }
        steps.Add(new IntelXessStep(xess.LicenseFile, Path.Combine(privateFolder, "license.txt")));
        return steps;
    }

    /// <summary>The game's own Intel XeSS DLLs under <paramref name="root"/> (bounded walk; links and RHI's own folders skipped).</summary>
    internal static IReadOnlyList<string> FindGameCopies(string root, IReadOnlyCollection<string> names)
    {
        var found = new List<string>();
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        var visited = 0;
        while (pending.Count > 0 && visited < ScanMaxFolders)
        {
            var (folder, depth) = pending.Pop();
            visited++;
            try
            {
                if (File.Exists(Path.Combine(folder, "OptiScaler.ini"))) continue;
                foreach (var file in Directory.EnumerateFiles(folder, "lib*.dll"))
                    if (names.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
                        && (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0 && IntelXessFiles.IsIntelFile(file))
                        found.Add(file);
                if (depth >= ScanMaxDepth) continue;
                foreach (var sub in Directory.EnumerateDirectories(folder))
                {
                    var name = Path.GetFileName(sub);
                    if (name.Equals("rtx-encore-runtimes", StringComparison.OrdinalIgnoreCase) || name.Equals(Rtx40MfgStandaloneDeploymentService.XessFolderName, StringComparison.OrdinalIgnoreCase)) continue;
                    if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                    pending.Push((sub, depth + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return found;
    }

    /// <summary>Places the XeSS files inside the install's or upgrade's scope and lists them in <paramref name="metadata"/>.</summary>
    private async Task<IntelXessPlaced> PlaceXessAsync(DeploymentScope scope, DetectedGame game, string modDirectory,
        IReadOnlyDictionary<IntelXessFile, string> cache, IDictionary<string, string> metadata, CancellationToken cancellationToken)
    {
        var placed = await IntelXessFiles.PlaceAsync(scope, XessSteps(game, modDirectory), cache, cancellationToken).ConfigureAwait(false);
        IntelXessFiles.MarkDone(metadata, XessPrefix, placed.Entries);
        return placed;
    }

    /// <summary>
    /// Update of an install whose RTX Encore file is current but that lacks the XeSS files or still has RHI's 310.8.0
    /// Neural Rendering file: one journaled operation with the record as its commit point.
    /// </summary>
    private async Task<ComponentOperationResult> UpgradeAsync(DetectedGame game, LinuxDeploymentRecord record, CancellationToken cancellationToken)
    {
        var nrFile = NeedsNeuralRenderingUpgrade(record)
            ? record.Files.FirstOrDefault(f => Path.GetFileName(f.Path).Equals(NeuralRenderingRuntimeOwnership.DllName, StringComparison.OrdinalIgnoreCase))
            : null;
        if (nrFile is not null && File.Exists(nrFile.Path)
            && !FileHash.HexEquals(await FileHash.Sha256HexAsync(nrFile.Path, cancellationToken).ConfigureAwait(false), nrFile.Sha256!))
            return ComponentOperationResult.Fail(ModifiedPrefix + nrFile.Path);
        string? nrSource = null;
        if (nrFile is not null)
        {
            var (path, refusal) = await EnsureNeuralRenderingCachedAsync(cancellationToken).ConfigureAwait(false);
            if (refusal is not null) return ComponentOperationResult.Fail(refusal);
            nrSource = path;
        }
        var (cache, failure) = NeedsXess(record) ? await CacheXessAsync(cancellationToken).ConfigureAwait(false) : (null, null);
        if (nrSource is null && cache is null)
            return failure is null ? ComponentOperationResult.Ok(UpToDatePrefix + record.Version) : ComponentOperationResult.Fail(XessNotInstalledPrefix.Trim() + " " + failure);

        var (opened, refusal2) = await DeploymentScope.BeginAsync(_journal, "RTX Encore upgrade: " + game.Name, game, ManagedComponentKind.RtxEncore, cancellationToken).ConfigureAwait(false);
        if (refusal2 is not null) return ComponentOperationResult.Fail(refusal2);
        await using var scope = opened!;
        cancellationToken = OperationCancellation.BeginWrites(cancellationToken);
        var notes = "";
        try
        {
            var updated = LinuxDeploymentRecordStore.Clone(record);
            if (nrSource is not null)
            {
                var file = updated.Files.First(f => Path.GetFileName(f.Path).Equals(NeuralRenderingRuntimeOwnership.DllName, StringComparison.OrdinalIgnoreCase));
                file.Sha256 = await scope.PlaceCopyAsync(nrSource, file.Path, cancellationToken).ConfigureAwait(false);
                updated.Metadata[MetaNrVersion] = NeuralRenderingVersion;
                VerifiedDownload.Record(updated.Metadata, file.Sha256, digestVerified: true, NrPart);
            }
            if (cache is not null)
                notes = IntelXessFiles.Notes(await PlaceXessAsync(scope, game, ModDirectory(updated), cache, updated.Metadata, cancellationToken).ConfigureAwait(false));
            _records.Upsert(scope.Stamp(updated));
        }
        catch (Exception ex)
        {
            var rollback = await scope.RollbackAsync().ConfigureAwait(false);
            if (rollback.Committed) return DeploymentScope.Landed("RTX Encore");
            return ComponentOperationResult.Fail(FailedRolledBackPrefix + ex.Message + DeploymentScope.RollbackNote(rollback));
        }
        await scope.CommitAsync().ConfigureAwait(false);
        return ComponentOperationResult.Ok(UpgradedMessage + notes + (failure is null ? "" : XessNotInstalledPrefix + failure));
    }
}
