using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// Linux-native adaptation of upstream RHI's DLSS/Streamline defaults, Quick Apply and
/// batch deployment. Only game-local DLL versions are managed here; NVIDIA driver-profile
/// presets/render scales remain Windows/NVAPI-only and are intentionally out of scope.
/// </summary>
public sealed record LinuxDlssVersionDefaults(
    string SuperResolution = "",
    string RayReconstruction = "",
    string FrameGeneration = "",
    string NeuralRendering = "",
    string Streamline = "")
{
    public LinuxDlssVersionDefaults Normalize() => this with
    {
        SuperResolution = SuperResolution?.Trim() ?? "",
        RayReconstruction = RayReconstruction?.Trim() ?? "",
        FrameGeneration = FrameGeneration?.Trim() ?? "",
        NeuralRendering = NeuralRendering?.Trim() ?? "",
        Streamline = Streamline?.Trim() ?? ""
    };

    public bool HasAny =>
        !string.IsNullOrWhiteSpace(SuperResolution) ||
        !string.IsNullOrWhiteSpace(RayReconstruction) ||
        !string.IsNullOrWhiteSpace(FrameGeneration) ||
        !string.IsNullOrWhiteSpace(NeuralRendering) ||
        !string.IsNullOrWhiteSpace(Streamline);
}

public enum LinuxDlssBatchComponent
{
    SuperResolution,
    RayReconstruction,
    FrameGeneration,
    NeuralRendering,
    Streamline
}

public sealed record LinuxDlssBatchItemResult(
    string GameName,
    LinuxDlssBatchComponent Component,
    bool Success,
    bool Changed,
    bool Deferred,
    bool Skipped,
    string Message);

public sealed record LinuxDlssBatchResult(IReadOnlyList<LinuxDlssBatchItemResult> Items)
{
    public int ChangedCount => Items.Count(item => item.Changed);
    public int FailedCount => Items.Count(item => !item.Success && !item.Deferred && !item.Skipped);
    public int DeferredCount => Items.Count(item => item.Deferred);
    public int SkippedCount => Items.Count(item => item.Skipped);
    public int CurrentCount => Items.Count(item => item.Success && !item.Changed && !item.Deferred && !item.Skipped);

    public string Summary => Items.Count == 0
        ? "No eligible DLSS/Streamline targets were found."
        : $"DLSS/Streamline: {ChangedCount} changed · {CurrentCount} current · {SkippedCount} skipped · {DeferredCount} deferred · {FailedCount} failed.";
}

public sealed record LinuxDlssBatchCandidate(
    DetectedGame Game,
    bool Eligible,
    string Reason,
    DlssDetectionResult Detection);

public sealed class LinuxDlssDefaultsService
{
    private readonly DlssManagerService _dlss;
    private readonly LinuxDeploymentRecordStore _records;
    private readonly IGameRunningProbe _running;

    public LinuxDlssDefaultsService(
        DlssManagerService dlss,
        LinuxDeploymentRecordStore records,
        IGameRunningProbe? runningProbe = null)
    {
        _dlss = dlss;
        _records = records;
        _running = runningProbe ?? new LinuxGameRunningProbe();
    }

    public LinuxDlssBatchCandidate Inspect(
        DetectedGame game,
        IReadOnlyList<string>? indexedFiles = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);

        if (!game.HasConfirmedPatchTarget)
            return new(game, false, "Alvo de modificações não definido — o launcher foi preservado, mas o executável real do jogo ainda precisa ser confirmado em Editar jogo.", new());
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
            return new(game, false, "Native Linux target — Windows DLSS/Streamline DLL swapping does not apply.", new());
        if (game.DlssScanSkippedByManifest)
            return new(game, false, "Upstream manifest suppresses recursive DLSS/Streamline scanning for this title.", new());
        if (string.IsNullOrWhiteSpace(game.InstallPath) || !Directory.Exists(game.InstallPath))
            return new(game, false, "Game install directory is unavailable.", new());
        if (HasOrchestratedStack(game, out var owner))
            return new(game, false, $"{owner} owns the DLSS/Streamline stack; remove it before direct Quick Apply/Batch Deploy.", new());

        var detection = DetectTargets(game, indexedFiles, cancellationToken);
        return detection.HasAny
            ? new(game, true, BuildDetectionSummary(detection), detection)
            : new(game, false, "No existing game-local DLSS/Streamline DLLs were detected; RHI will not invent target paths.", detection);
    }

    /// <summary>
    /// #177: the targets following the newest DLSS / Streamline may swap: the Quick Apply set (<see cref="Inspect"/>:
    /// manifest path overrides, <c>dlssSkipGames</c>, no <c>OptiScaler/</c> folder, no file an NR owner or OptiScaler
    /// records, no ShortFuse / DLSS5 Feeder Full stack) minus every file ANY RHI record of the game lists, and a
    /// Streamline folder holding such a file. An automatic swap never touches a file a component installed.
    /// </summary>
    public LinuxDlssBatchCandidate InspectForFollow(
        DetectedGame game,
        IReadOnlyList<string>? indexedFiles = null,
        CancellationToken cancellationToken = default)
    {
        var candidate = Inspect(game, indexedFiles, cancellationToken);
        if (!candidate.Eligible) return candidate;
        var recorded = _records.FindAll(game)
            .SelectMany(record => record.Files)
            .Select(file => file.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        return candidate with { Detection = DlssManagerService.ExcludeProtected(candidate.Detection, recorded) };
    }

    /// <summary>
    /// The game's direct DLSS/Streamline swap/restore targets: every per-game, Quick Apply,
    /// Batch Deploy and Restore All flow must take its paths from here. Files RHI-managed
    /// OptiScaler owns (its record, incl. the FG stack), the files of whichever record owns
    /// <c>nvngx_dlssnr.dll</c> (<see cref="NeuralRenderingRuntimeOwnership.OwnerRecords"/>, e.g. RTX
    /// Encore's pinned 310.8.0) and the whole <c>OptiScaler/</c> folder are excluded, so a swap or
    /// restore can never break an owner's hash ownership or consume an <c>.original</c> the owner
    /// restores on disable/removal.
    /// </summary>
    public DlssDetectionResult DetectTargets(
        DetectedGame game,
        IReadOnlyList<string>? indexedFiles = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        return _dlss.Detect(game, indexedFiles, RhiOwnedFiles(game), cancellationToken);
    }

    /// <summary>Re-applies the ownership exclusion to a cached detection right before a swap/restore.</summary>
    public DlssDetectionResult ExcludeOwnedTargets(DetectedGame game, DlssDetectionResult detection)
    {
        ArgumentNullException.ThrowIfNull(game);
        return DlssManagerService.ExcludeProtected(detection, RhiOwnedFiles(game));
    }

    /// <summary>
    /// The per-game DLSS panel's swap/restore targets for <paramref name="component"/> (<c>SR</c>, <c>RR</c>, <c>FG</c>,
    /// <c>NR</c>): the panel's cached detection with the ownership exclusion re-applied at action time (the cached probe
    /// may predate an OptiScaler install or FG-stack change). False for an unknown component.
    /// </summary>
    public bool TryPanelTargets(
        DetectedGame? game,
        DlssDetectionResult cached,
        string? component,
        out DlssComponentKind kind,
        out IReadOnlyList<string> paths)
    {
        var targets = PanelTargets(game, cached);
        switch (component?.ToUpperInvariant())
        {
            case "SR": kind = DlssComponentKind.SuperResolution; paths = targets.DlssPaths; return true;
            case "RR": kind = DlssComponentKind.RayReconstruction; paths = targets.DlssdPaths; return true;
            case "FG": kind = DlssComponentKind.FrameGeneration; paths = targets.DlssgPaths; return true;
            case "NR": kind = DlssComponentKind.NeuralRendering; paths = targets.DlssnrPaths; return true;
            default: kind = default; paths = []; return false;
        }
    }

    /// <summary>The per-game panel's Streamline swap/restore folders, with the ownership exclusion re-applied.</summary>
    public IReadOnlyList<string> PanelStreamlineFolders(DetectedGame? game, DlssDetectionResult cached)
        => PanelTargets(game, cached).StreamlineFolders;

    private DlssDetectionResult PanelTargets(DetectedGame? game, DlssDetectionResult cached)
        => game is null ? cached : ExcludeOwnedTargets(game, cached);

    // OptiScaler's files (any channel: its FG stack too) and every file of a record that owns nvngx_dlssnr.dll
    // (NeuralRenderingRuntimeOwnership: OptiScaler DLSS NR/F5, DLSS5 Feeder, ShortFuse, RTX Encore with its file).
    // Excluding the owned path also keeps the owner's <path>.original out of every restore.
    private IReadOnlyList<string> RhiOwnedFiles(DetectedGame game)
    {
        var owners = NeuralRenderingRuntimeOwnership.OwnerRecords(_records, game).ToList();
        if (_records.Find(game, ManagedComponentKind.OptiScaler) is { } optiScaler) owners.Add(optiScaler);
        return owners
            .SelectMany(record => record.Files)
            .Select(file => file.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<LinuxDlssBatchResult> ApplyDefaultsAsync(
        DetectedGame game,
        LinuxDlssVersionDefaults defaults,
        IReadOnlyList<string>? indexedFiles = null,
        CancellationToken cancellationToken = default)
    {
        defaults = defaults.Normalize();
        if (!defaults.HasAny)
            return new LinuxDlssBatchResult([]);
        // Refs #90: refused before anything is read or downloaded; the batch reports it as this game's failure.
        if (XboxWriteGate.Refusal(game) is { } xbox) throw new XboxTitleBusyException(xbox); // w3/scan hook

        var candidate = Inspect(game, indexedFiles, cancellationToken);
        if (!candidate.Eligible)
            return new LinuxDlssBatchResult([
                new LinuxDlssBatchItemResult(game.Name, LinuxDlssBatchComponent.SuperResolution,
                    true, false, false, true, candidate.Reason)
            ]);

        if (_running.IsRunning(game))
            return BuildWholeGameDeferred(game, "Game is running; Quick Apply/Batch Deploy is deferred to protect loaded DLLs.");

        if (_dlss.Manifest is null)
            await _dlss.FetchManifestAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<LinuxDlssBatchItemResult>();
        await ApplyDllAsync(game, LinuxDlssBatchComponent.SuperResolution, DlssComponentKind.SuperResolution,
            candidate.Detection.DlssPaths, defaults.SuperResolution, results, cancellationToken).ConfigureAwait(false);
        await ApplyDllAsync(game, LinuxDlssBatchComponent.RayReconstruction, DlssComponentKind.RayReconstruction,
            candidate.Detection.DlssdPaths, defaults.RayReconstruction, results, cancellationToken).ConfigureAwait(false);
        await ApplyDllAsync(game, LinuxDlssBatchComponent.FrameGeneration, DlssComponentKind.FrameGeneration,
            candidate.Detection.DlssgPaths, defaults.FrameGeneration, results, cancellationToken).ConfigureAwait(false);
        await ApplyDllAsync(game, LinuxDlssBatchComponent.NeuralRendering, DlssComponentKind.NeuralRendering,
            candidate.Detection.DlssnrPaths, defaults.NeuralRendering, results, cancellationToken).ConfigureAwait(false);
        await ApplyStreamlineAsync(game, candidate.Detection.StreamlineFolders, defaults.Streamline, results, cancellationToken).ConfigureAwait(false);
        return new LinuxDlssBatchResult(results);
    }

    public async Task<LinuxDlssBatchResult> RestoreAllAsync(
        DetectedGame game,
        IReadOnlyList<string>? indexedFiles = null,
        CancellationToken cancellationToken = default)
    {
        if (XboxWriteGate.Refusal(game) is { } xbox) throw new XboxTitleBusyException(xbox); // w3/scan hook (Refs #90)
        var candidate = Inspect(game, indexedFiles, cancellationToken);
        if (!candidate.Eligible)
            return new LinuxDlssBatchResult([
                new LinuxDlssBatchItemResult(game.Name, LinuxDlssBatchComponent.SuperResolution,
                    true, false, false, true, candidate.Reason)
            ]);

        if (_running.IsRunning(game))
            return BuildWholeGameDeferred(game, "Game is running; restore is deferred to protect loaded DLLs.");

        var results = new List<LinuxDlssBatchItemResult>();
        await RestoreDllAsync(game, LinuxDlssBatchComponent.SuperResolution, DlssComponentKind.SuperResolution,
            candidate.Detection.DlssPaths, results, cancellationToken).ConfigureAwait(false);
        await RestoreDllAsync(game, LinuxDlssBatchComponent.RayReconstruction, DlssComponentKind.RayReconstruction,
            candidate.Detection.DlssdPaths, results, cancellationToken).ConfigureAwait(false);
        await RestoreDllAsync(game, LinuxDlssBatchComponent.FrameGeneration, DlssComponentKind.FrameGeneration,
            candidate.Detection.DlssgPaths, results, cancellationToken).ConfigureAwait(false);
        await RestoreDllAsync(game, LinuxDlssBatchComponent.NeuralRendering, DlssComponentKind.NeuralRendering,
            candidate.Detection.DlssnrPaths, results, cancellationToken).ConfigureAwait(false);
        await RestoreStreamlineAsync(game, candidate.Detection.StreamlineFolders, results, cancellationToken).ConfigureAwait(false);
        return new LinuxDlssBatchResult(results);
    }

    public IReadOnlyList<LinuxDlssBatchCandidate> InspectBatch(
        IEnumerable<DetectedGame> games,
        Func<DetectedGame, IReadOnlyList<string>?>? indexedFiles = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<LinuxDlssBatchCandidate>();
        foreach (var game in games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(Inspect(game, indexedFiles?.Invoke(game), cancellationToken));
        }
        return results;
    }

    public async Task<LinuxDlssBatchResult> ApplyBatchAsync(
        IReadOnlyList<DetectedGame> games,
        LinuxDlssVersionDefaults defaults,
        Func<DetectedGame, IReadOnlyList<string>?>? indexedFiles = null,
        CancellationToken cancellationToken = default)
    {
        var all = new List<LinuxDlssBatchItemResult>();
        foreach (var game in games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // One game is one unit: Cancel stops the batch between games, and once a game's first DLL is being
            // swapped that game runs to the end.
            using var unit = new OperationCancellation(cancellationToken);
            try
            {
                var result = await ApplyDefaultsAsync(game, defaults, indexedFiles?.Invoke(game), unit.Token).ConfigureAwait(false);
                all.AddRange(result.Items);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                all.Add(new LinuxDlssBatchItemResult(game.Name, LinuxDlssBatchComponent.SuperResolution,
                    false, false, false, false, $"Batch deploy failed: {ex.Message}"));
            }
        }
        return new LinuxDlssBatchResult(all);
    }

    public async Task<LinuxDlssBatchResult> RestoreBatchAsync(
        IReadOnlyList<DetectedGame> games,
        Func<DetectedGame, IReadOnlyList<string>?>? indexedFiles = null,
        CancellationToken cancellationToken = default)
    {
        var all = new List<LinuxDlssBatchItemResult>();
        foreach (var game in games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var unit = new OperationCancellation(cancellationToken);
            try
            {
                var result = await RestoreAllAsync(game, indexedFiles?.Invoke(game), unit.Token).ConfigureAwait(false);
                all.AddRange(result.Items);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                all.Add(new LinuxDlssBatchItemResult(game.Name, LinuxDlssBatchComponent.SuperResolution,
                    false, false, false, false, $"Batch restore failed: {ex.Message}"));
            }
        }
        return new LinuxDlssBatchResult(all);
    }

    private async Task ApplyDllAsync(
        DetectedGame game,
        LinuxDlssBatchComponent batchComponent,
        DlssComponentKind kind,
        IReadOnlyList<string> targets,
        string version,
        List<LinuxDlssBatchItemResult> results,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(version)) return; // upstream "Default" = don't change
        if (targets.Count == 0)
        {
            results.Add(new(game.Name, batchComponent, true, false, false, true,
                $"{Display(batchComponent)} not present; skipped without creating a target."));
            return;
        }

        if (kind == DlssComponentKind.SuperResolution && targets.Any(IsDlss1x))
        {
            results.Add(new(game.Name, batchComponent, true, false, false, true,
                "DLSS Super Resolution 1.x target detected; newer manifest DLLs are not applied over DLSS 1.x."));
            return;
        }

        var swap = await _dlss.SwapAsync(kind, targets, version, cancellationToken).ConfigureAwait(false);
        var changed = swap.Results.Count(result => result.Changed);
        results.Add(new(game.Name, batchComponent, swap.Success, changed > 0, false, false,
            swap.Success
                ? changed > 0
                    ? $"Applied {Display(batchComponent)} {version} to {changed} existing target(s); true originals remain as .original."
                    : $"{Display(batchComponent)} already matches {version}; no backup was created."
                : swap.Error ?? $"Could not apply {Display(batchComponent)} {version}."));
    }

    private async Task ApplyStreamlineAsync(
        DetectedGame game,
        IReadOnlyList<string> folders,
        string version,
        List<LinuxDlssBatchItemResult> results,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(version)) return;
        if (folders.Count == 0)
        {
            results.Add(new(game.Name, LinuxDlssBatchComponent.Streamline, true, false, false, true,
                "Streamline not present; skipped without creating a layout."));
            return;
        }
        var swap = await _dlss.SwapStreamlineAsync(folders, version, cancellationToken).ConfigureAwait(false);
        var changed = swap.Results.Count(result => result.Changed);
        results.Add(new(game.Name, LinuxDlssBatchComponent.Streamline, swap.Success, changed > 0, false, false,
            swap.Success
                ? changed > 0
                    ? $"Applied Streamline {version} to {changed} existing DLL(s); true originals remain as .original."
                    : $"Existing Streamline DLLs already match {version}; nothing changed."
                : swap.Error ?? $"Could not apply Streamline {version}."));
    }

    private async Task RestoreDllAsync(
        DetectedGame game,
        LinuxDlssBatchComponent batchComponent,
        DlssComponentKind kind,
        IReadOnlyList<string> targets,
        List<LinuxDlssBatchItemResult> results,
        CancellationToken cancellationToken)
    {
        var restoreTargets = targets
            .Where(target => File.Exists(target + TransactionalFileDeploymentService.BackupExtension))
            .ToArray();
        if (restoreTargets.Length == 0) return;
        var restore = await _dlss.RestoreAsync(kind, restoreTargets, cancellationToken).ConfigureAwait(false);
        results.Add(new(game.Name, batchComponent, restore.Success,
            restore.Success && restore.Results.Any(result => result.Changed), false, false,
            restore.Success
                ? $"Restored {restore.Results.Count} original {Display(batchComponent)} target(s)."
                : restore.Error ?? $"Could not restore {Display(batchComponent)}."));
    }

    private async Task RestoreStreamlineAsync(
        DetectedGame game,
        IReadOnlyList<string> folders,
        List<LinuxDlssBatchItemResult> results,
        CancellationToken cancellationToken)
    {
        var hasBackup = folders
            .SelectMany(folder => DlssManagerService.KnownStreamlineDlls.Select(name => Path.Combine(folder, name)))
            .Any(target => File.Exists(target + TransactionalFileDeploymentService.BackupExtension));
        if (!hasBackup) return;
        var restore = await _dlss.RestoreStreamlineAsync(folders, cancellationToken).ConfigureAwait(false);
        results.Add(new(game.Name, LinuxDlssBatchComponent.Streamline, restore.Success,
            restore.Success && restore.Results.Any(result => result.Changed), false, false,
            restore.Success
                ? $"Restored {restore.Results.Count} original Streamline DLL(s)."
                : restore.Error ?? "Could not restore Streamline."));
    }

    private bool HasOrchestratedStack(DetectedGame game, out string owner)
    {
        if (_records.Find(game, ManagedComponentKind.ShortFuse) is not null)
        {
            owner = "ShortFuse";
            return true;
        }

        var feeder = _records.Find(game, ManagedComponentKind.Dlss5Feeder);
        if (feeder is not null && feeder.Metadata.TryGetValue("Feeder.FullStack", out var value) && value == "1")
        {
            owner = "DLSS5 Feeder Full";
            return true;
        }

        owner = "";
        return false;
    }

    private static LinuxDlssBatchResult BuildWholeGameDeferred(DetectedGame game, string message)
        => new([
            new LinuxDlssBatchItemResult(game.Name, LinuxDlssBatchComponent.SuperResolution,
                true, false, true, false, message)
        ]);

    private static bool IsDlss1x(string path)
    {
        var version = DlssManagerService.TryGetDllVersion(path);
        return version?.StartsWith("1.", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string BuildDetectionSummary(DlssDetectionResult detection)
        => $"SR {detection.DlssPaths.Count} · RR {detection.DlssdPaths.Count} · FG {detection.DlssgPaths.Count} · NR {detection.DlssnrPaths.Count} · Streamline {detection.StreamlineFolders.Count}";

    private static string Display(LinuxDlssBatchComponent component) => component switch
    {
        LinuxDlssBatchComponent.SuperResolution => "Super Resolution",
        LinuxDlssBatchComponent.RayReconstruction => "Ray Reconstruction",
        LinuxDlssBatchComponent.FrameGeneration => "Frame Generation",
        LinuxDlssBatchComponent.NeuralRendering => "Neural Rendering",
        LinuxDlssBatchComponent.Streamline => "Streamline",
        _ => component.ToString()
    };
}
