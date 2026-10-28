using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Hdr;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// The DLSS / Streamline section of the detail pane (epic #89 unit 4): the selected game's DLSS and Streamline DLLs,
/// the manifest version lists, swap/restore per component, the Settings defaults (quick apply, restore all) and the
/// library-wide DLSS batch. It reads the selected game's published analysis (<see cref="GameAnalysis.Dlss"/> and the
/// backup counts in its facts) and never touches the disk from a getter.
/// </summary>
public sealed partial class DlssPanelViewModel : PanelViewModel
{
    private readonly DlssManagerService _dlss;
    private readonly LinuxDlssDefaultsService _dlssDefaults;

    internal DlssPanelViewModel(IPanelHost host, DlssManagerService dlss, LinuxDlssDefaultsService dlssDefaults) : base(host)
    {
        _dlss = dlss;
        _dlssDefaults = dlssDefaults;
    }

    /// <summary>#211: the per-game NVIDIA settings shown in this section (owned and saved by the runtime panel's launch profile).</summary>
    public NvidiaSettingsPresenter Nvidia => Shell.Runtime.Nvidia;

    /// <summary>The selected game's DLSS detection (from its published analysis; empty while none).</summary>
    internal DlssDetectionResult Detection
    {
        get => _dlssDetection;
        set => _dlssDetection = value;
    }

    // ShortFuse owns the DLSS stack while installed (Extras shows it; this panel only reads the same fact).
    private bool ShortFuseInstalled => SelectedGame is not null && Facts?.ShortFuseManaged == true;

    /// <summary>Fills the version lists from the DLSS manifest and selects each newest version.</summary>
    internal void ApplyManifest(DlssManifestData manifest)
    {
        FillVersions(DlssSrVersions, manifest.Dlss.Select(x => x.Version));
        FillVersions(DlssRrVersions, manifest.Dlssd.Select(x => x.Version));
        FillVersions(DlssFgVersions, manifest.Dlssg.Select(x => x.Version));
        // #229: NR versions made for another card generation are hidden (swap row, DLSS batch, Settings → DLSS defaults).
        FillVersions(DlssNrVersions, NeuralRenderingGpuPolicy.ForCard(manifest.Dlssnr.Select(x => x.Version), _dlss.CardGeneration()));
        FillVersions(StreamlineVersions, manifest.Streamline.Select(x => x.Version));
        SelectedDlssSrVersion = DlssSrVersions.FirstOrDefault();
        SelectedDlssRrVersion = DlssRrVersions.FirstOrDefault();
        SelectedDlssFgVersion = DlssFgVersions.FirstOrDefault();
        SelectedDlssNrVersion = DlssNrVersions.FirstOrDefault();
        SelectedStreamlineVersion = StreamlineVersions.FirstOrDefault();
    }

    internal void NotifyStackManaged() => OnPropertyChanged(nameof(OrchestratedDlssStackManaged));

    /// <summary>Raises every derived property of the section (selection, analysis or busy state changed).</summary>
    internal void NotifyPanel()
    {
        OnPropertyChanged(nameof(Dlss5FeederFullManaged));
        OnPropertyChanged(nameof(OrchestratedDlssStackManaged));
        OnPropertyChanged(nameof(CanSwapDlssSr));
        OnPropertyChanged(nameof(CanSwapDlssRr));
        OnPropertyChanged(nameof(CanSwapDlssFg));
        OnPropertyChanged(nameof(CanSwapDlssNr));
        OnPropertyChanged(nameof(CanSwapStreamline));
        OnPropertyChanged(nameof(DlssSrDetected));
        OnPropertyChanged(nameof(DlssRrDetected));
        OnPropertyChanged(nameof(DlssFgDetected));
        OnPropertyChanged(nameof(DlssNrDetected));
        OnPropertyChanged(nameof(StreamlineDetected));
        OnPropertyChanged(nameof(DlssSrRestoreAvailable));
        OnPropertyChanged(nameof(DlssRrRestoreAvailable));
        OnPropertyChanged(nameof(DlssFgRestoreAvailable));
        OnPropertyChanged(nameof(DlssNrRestoreAvailable));
        OnPropertyChanged(nameof(StreamlineRestoreAvailable));
        OnPropertyChanged(nameof(DlssSrDeploymentState));
        OnPropertyChanged(nameof(DlssRrDeploymentState));
        OnPropertyChanged(nameof(DlssFgDeploymentState));
        OnPropertyChanged(nameof(DlssNrDeploymentState));
        OnPropertyChanged(nameof(StreamlineDeploymentState));
        OnPropertyChanged(nameof(DlssSrPath));
        OnPropertyChanged(nameof(DlssRrPath));
        OnPropertyChanged(nameof(DlssFgPath));
        OnPropertyChanged(nameof(DlssNrPath));
        OnPropertyChanged(nameof(StreamlinePath));
        OnPropertyChanged(nameof(DlssStatusSummary));
        NotifyDlssDefaults();
    }

    public ObservableCollection<string> DlssSrVersions { get; } = [];

    public ObservableCollection<string> DlssRrVersions { get; } = [];

    public ObservableCollection<string> DlssFgVersions { get; } = [];

    public ObservableCollection<string> DlssNrVersions { get; } = [];

    public ObservableCollection<string> StreamlineVersions { get; } = [];

    [ObservableProperty] private string? selectedDlssSrVersion;

    [ObservableProperty] private string? selectedDlssRrVersion;

    [ObservableProperty] private string? selectedDlssFgVersion;

    [ObservableProperty] private string? selectedDlssNrVersion;

    [ObservableProperty] private string? selectedStreamlineVersion;

    private DlssDetectionResult _dlssDetection = new();

    public bool OrchestratedDlssStackManaged => Dlss5FeederFullManaged || ShortFuseInstalled;

    public bool Dlss5FeederFullManaged => SelectedGame is not null && Facts?.Dlss5FeederFullManaged == true;

    public bool CanSwapDlssSr => HasConfirmedPatchTarget && DlssSrDetected && !OrchestratedDlssStackManaged;

    public bool CanSwapDlssRr => HasConfirmedPatchTarget && DlssRrDetected && !OrchestratedDlssStackManaged;

    public bool CanSwapDlssFg => HasConfirmedPatchTarget && DlssFgDetected && !OrchestratedDlssStackManaged;

    public bool CanSwapDlssNr => HasConfirmedPatchTarget && DlssNrDetected && !OrchestratedDlssStackManaged;

    public bool CanSwapStreamline => HasConfirmedPatchTarget && StreamlineDetected && !OrchestratedDlssStackManaged;

    public bool DlssSrDetected => _dlssDetection.DlssPaths.Count > 0;

    public bool DlssRrDetected => _dlssDetection.DlssdPaths.Count > 0;

    public bool DlssFgDetected => _dlssDetection.DlssgPaths.Count > 0;

    public bool DlssNrDetected => _dlssDetection.DlssnrPaths.Count > 0;

    public bool StreamlineDetected => _dlssDetection.StreamlineFolders.Count > 0;

    // A restore button is meaningful only after RHI actually created an .original backup.
    // This also doubles as a persistent visual signal that survives switching games/restarting RHI.
    public bool DlssSrRestoreAvailable => HasConfirmedPatchTarget && !OrchestratedDlssStackManaged && Facts?.DlssSrBackups > 0;

    public bool DlssRrRestoreAvailable => HasConfirmedPatchTarget && !OrchestratedDlssStackManaged && Facts?.DlssRrBackups > 0;

    public bool DlssFgRestoreAvailable => HasConfirmedPatchTarget && !OrchestratedDlssStackManaged && Facts?.DlssFgBackups > 0;

    public bool DlssNrRestoreAvailable => HasConfirmedPatchTarget && !OrchestratedDlssStackManaged && Facts?.DlssNrBackups > 0;

    public bool StreamlineRestoreAvailable => HasConfirmedPatchTarget && !OrchestratedDlssStackManaged && Facts?.StreamlineBackups > 0;

    public string DlssSrDeploymentState => ShortFuseInstalled && DlssSrDetected
        ? T("Vm.ShortFuseManaged")
        : Dlss5FeederFullManaged && DlssSrDetected ? T("Vm.FeederFullPrivate") : DlssDeploymentState(_dlssDetection.DlssPaths, Facts?.DlssSrBackups ?? 0);

    public string DlssRrDeploymentState => ShortFuseInstalled && DlssRrDetected ? T("Vm.ShortFuseManaged") : DlssDeploymentState(_dlssDetection.DlssdPaths, Facts?.DlssRrBackups ?? 0);

    public string DlssFgDeploymentState => ShortFuseInstalled && DlssFgDetected ? T("Vm.ShortFuseManaged") : DlssDeploymentState(_dlssDetection.DlssgPaths, Facts?.DlssFgBackups ?? 0);

    public string DlssNrDeploymentState => ShortFuseInstalled && DlssNrDetected
        ? T("Vm.ShortFuseManaged")
        : Dlss5FeederFullManaged && DlssNrDetected ? T("Vm.FeederFullPrivate") : DlssDeploymentState(_dlssDetection.DlssnrPaths, Facts?.DlssNrBackups ?? 0);

    public string StreamlineDeploymentState
    {
        get
        {
            if (_dlssDetection.StreamlineFolders.Count == 0) return T("Vm.Missing");
            if (ShortFuseInstalled) return T("Vm.ShortFuseManaged");
            var backups = Facts?.StreamlineBackups ?? 0;
            return backups > 0 ? TF("Vm.RHIAppliedDLLS", backups) : T("Vm.GameDLLs");
        }
    }

    public string DlssSrPath => FirstOrNotFound(_dlssDetection.DlssPaths, "nvngx_dlss.dll");

    public string DlssRrPath => FirstOrNotFound(_dlssDetection.DlssdPaths, "nvngx_dlssd.dll");

    public string DlssFgPath => FirstOrNotFound(_dlssDetection.DlssgPaths, "nvngx_dlssg.dll");

    public string DlssNrPath => FirstOrNotFound(_dlssDetection.DlssnrPaths, "nvngx_dlssnr.dll");

    public string StreamlinePath => _dlssDetection.StreamlineFolders.FirstOrDefault()
        ?? T("Vm.StreamlineNotDetectedUpdateUnavailable");

    public string DlssStatusSummary => IsSelectionProbeBusy
        ? T("Vm.AnalyzingGameFilesBackground")
        : ShortFuseInstalled
            ? T("Vm.ManagedShortFuseDLSSStreamlineStackTransactional")
        : Dlss5FeederFullManaged
            ? T("Vm.ManagedDLSS5FeederFullPrivateSuper")
            : _dlssDetection.HasAny
                ? TF("Vm.DetectedSuperResolutionRayReconstructionFrame", _dlssDetection.DlssPaths.Count, _dlssDetection.DlssdPaths.Count, _dlssDetection.DlssgPaths.Count, _dlssDetection.DlssnrPaths.Count, _dlssDetection.StreamlineFolders.Count)
                : T("Vm.NoLocalDLSSStreamlineDLLDetected");

    public LinuxDlssVersionDefaults DlssVersionDefaults => new(
        Host.Settings.DefaultDlssVersion,
        Host.Settings.DefaultDlssdVersion,
        Host.Settings.DefaultDlssgVersion,
        Host.Settings.DefaultDlssnrVersion,
        Host.Settings.DefaultStreamlineVersion);

    public bool HasDlssVersionDefaults => DlssVersionDefaults.HasAny;

    /// <summary>#215: the Settings default presets Quick Apply copies (a Default default copies nothing).</summary>
    public NvidiaPresetChoice DlssPresetDefaults => NvidiaPresetChoice.FromDefaults(
        Host.Settings.DefaultSrPreset, Host.Settings.DefaultRrPreset, Host.Settings.DefaultFgPreset);

    /// <summary>A version or (#215, upstream <c>hasDefaults</c>) a preset default is set: Quick Apply has something to do.</summary>
    public bool HasDlssDefaults => HasDlssVersionDefaults || DlssPresetDefaults.HasAny;

    public string DlssDefaultsSummary
    {
        get
        {
            static string Show(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
            var d = DlssVersionDefaults;
            var versions = TF("Vm.DefaultsSRRRFGNRSL", Show(d.SuperResolution), Show(d.RayReconstruction), Show(d.FrameGeneration), Show(d.NeuralRendering), Show(d.Streamline));
            var presets = DlssPresetDefaults;
            if (!presets.HasAny) return versions;
            // #215 (upstream's Settings summary names the preset defaults too).
            return versions + "\n" + TF("Dp.PresetSummary", ShowPreset(NvidiaPresetSlot.SuperResolution, presets.SuperResolution),
                ShowPreset(NvidiaPresetSlot.RayReconstruction, presets.RayReconstruction), ShowPreset(NvidiaPresetSlot.FrameGeneration, presets.FrameGeneration));
        }
    }

    public bool CanQuickApplyDlssDefaults =>
        SelectedGame is not null && HasConfirmedPatchTarget && !IsBusy && !IsSelectionProbeBusy && HasDlssDefaults &&
        _dlssDetection.HasAny && !OrchestratedDlssStackManaged &&
        !string.Equals(SelectedGame.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase);

    /// <summary>RHI has swapped DLSS/Streamline files of the selected game that Restore all can put back.</summary>
    private bool DlssFilesRestorable =>
        SelectedGame is not null && HasConfirmedPatchTarget && !OrchestratedDlssStackManaged &&
        (DlssSrRestoreAvailable || DlssRrRestoreAvailable || DlssFgRestoreAvailable || DlssNrRestoreAvailable || StreamlineRestoreAvailable);

    /// <summary>Restore all: RHI's swapped files, and (#211) the game's NVIDIA settings back to Default.</summary>
    public bool CanRestoreAllDlss =>
        SelectedGame is not null && !IsBusy && !IsSelectionProbeBusy && (DlssFilesRestorable || Nvidia.HasChoices);

    [RelayCommand(CanExecute = nameof(CanQuickApplyDlssDefaults))]
    private async Task QuickApplyDlssDefaultsAsync()
    {
        if (SelectedGame is null || !CanQuickApplyDlssDefaults) return;
        var game = SelectedGame;
        using var op = BeginOperation("dlss-defaults");
        if (op is null) return;
        try
        {
            StatusText = TF("Vm.QuickApplyApplyingDLSSStreamlineDefaults", game.Name);
            var presets = DlssPresetDefaults;
            var detection = _dlssDetection;
            var lines = new List<string>();
            LinuxDlssBatchResult? result = null;
            if (HasDlssVersionDefaults)
            {
                IReadOnlyList<string>? files = Host.Session.CachedFiles(game)
                    ?? await Task.Run(() => GameFileIndexer.Build(game.InstallPath, 8, op.Token), op.Token);
                result = await _dlssDefaults.ApplyDefaultsAsync(game, DlssVersionDefaults, files, op.Token);
                lines.Add(UiLocalizationService.Present(result.Summary));
                lines.Add(FailedGamesLine(result)); // #249
                lines.Add(NrNotForThisCardLine(result.Items, DlssVersionDefaults.NeuralRendering)); // #229
            }
            // #215 (upstream's Apply defaults): after the DLL swaps, the preset defaults go into the game's own NVIDIA
            // settings, only where its NVIDIA settings… dialog shows the row.
            if (presets.HasAny)
                lines.Add(await CopyPresetsAsync(game, detection, presets, op.Token));
            StatusText = string.Join(" ", lines.Where(line => line.Length > 0));
            if (result?.ChangedCount > 0)
                await ReprobeGameAsync(game);
        }
        catch (Exception ex) when (op.WasCancelled(ex))
        {
            StatusText = OperationCancelledText();
        }
        catch (Exception ex)
        {
            StatusText = TF("Vm.QuickApplyFailed", UiLocalizationService.Present(ex.Message));
        }
        finally
        {
            NotifyDlssDefaults();
        }
    }

    /// <summary>
    /// #249 (upstream 2.8.8 <c>d61a696</c>, "Failed: N game(s) • game: reason"): the games a DLSS/Streamline result
    /// counts as failed, each named once with its reasons; "" when none.
    /// </summary>
    internal static string FailedGamesLine(LinuxDlssBatchResult result)
    {
        var games = result.FailedItems
            .GroupBy(item => item.GameName, StringComparer.Ordinal)
            .Select(game => game.Key + ": " + string.Join("; ", game
                .Select(item => UiLocalizationService.Present(item.Message).TrimEnd('.'))
                .Distinct(StringComparer.Ordinal)))
            .ToArray();
        return games.Length == 0 ? "" : TF("Dp.BatchFailedGames", games.Length, string.Join(" • ", games)) + ".";
    }

    /// <summary>
    /// #229: the games whose Neural Rendering step was skipped because the chosen DLL is made for another card
    /// generation (their other DLSS steps ran); "" when none.
    /// </summary>
    internal static string NrNotForThisCardLine(IEnumerable<LinuxDlssBatchItemResult> items, string version)
    {
        var games = items.Where(item => item.NotForThisCard).Select(item => item.GameName).Distinct(StringComparer.Ordinal).ToArray();
        return games.Length == 0 ? "" : TF("Ng.NrSkippedNotForCard", version, string.Join(", ", games));
    }

    [RelayCommand(CanExecute = nameof(CanRestoreAllDlss))]
    private async Task RestoreAllDlssAsync()
    {
        if (SelectedGame is null || !CanRestoreAllDlss) return;
        var game = SelectedGame;
        var files = DlssFilesRestorable;
        using var op = BeginOperation("dlss-restore");
        if (op is null) return;
        // #211: the NVIDIA settings go back to Default first (no files involved; kept even if a file restore fails).
        var nvidiaLine = Shell.Runtime.ResetNvidiaSettings(game) ? TF("Nv.RestoredToDefault", game.Name) : null;
        if (!files)
        {
            if (nvidiaLine is not null) StatusText = nvidiaLine;
            NotifyDlssDefaults();
            return;
        }
        try
        {
            StatusText = TF("Vm.RestoringAllRHIDLSSStreamlineOriginals", game.Name);
            IReadOnlyList<string>? indexed = Host.Session.CachedFiles(game)
                ?? await Task.Run(() => GameFileIndexer.Build(game.InstallPath, 8, op.Token), op.Token);
            var result = await _dlssDefaults.RestoreAllAsync(game, indexed, op.Token);
            StatusText = Join(UiLocalizationService.Present(result.Summary), FailedGamesLine(result), nvidiaLine ?? ""); // #249
            if (result.ChangedCount > 0)
                await ReprobeGameAsync(game);
        }
        catch (Exception ex) when (op.WasCancelled(ex))
        {
            StatusText = OperationCancelledText();
        }
        catch (Exception ex)
        {
            StatusText = TF("Vm.RestoreAllFailed", UiLocalizationService.Present(ex.Message));
        }
        finally
        {
            NotifyDlssDefaults();
        }
    }

    public async Task<IReadOnlyList<LinuxDlssBatchCandidate>?> GetDlssBatchCandidatesAsync(CancellationToken cancellationToken = default)
    {
        // Indexing every eligible game can take a while: it holds the gate (the button cannot start it twice) and the
        // busy overlay's Cancel stops it (Refs #77). Null when refused or cancelled; the status line says which.
        using var op = BeginOperation("dlss-batch-scan", linkedToken: cancellationToken);
        if (op is null) return null;
        try
        {
            var games = Host.AllGames.ToArray();
            var indexes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var game in games)
            {
                op.Token.ThrowIfCancellationRequested();
                if (!game.HasConfirmedPatchTarget ||
                    string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase) ||
                    game.DlssScanSkippedByManifest || string.IsNullOrWhiteSpace(game.InstallPath) || !Directory.Exists(game.InstallPath))
                    continue;
                var key = Key(game);
                if (Host.Session.CachedFiles(game) is { } cached)
                    indexes[key] = cached;
                else
                    indexes[key] = await Task.Run(() => GameFileIndexer.Build(game.InstallPath, 8, op.Token), op.Token);
            }

            var candidates = await Task.Run(() => _dlssDefaults.InspectBatch(
                games,
                game => indexes.TryGetValue(Key(game), out var files) ? files : null,
                op.Token), op.Token);
            return candidates;
        }
        catch (Exception ex) when (op.WasCancelled(ex))
        {
            StatusText = OperationCancelledText();
            return null;
        }
    }

    /// <param name="presets">#215: the batch's preset dropdowns (null or none chosen = presets untouched).</param>
    public async Task<DlssBatchOutcome> ApplyDlssBatchAsync(
        IReadOnlyList<DetectedGame> games,
        LinuxDlssVersionDefaults defaults,
        NvidiaPresetChoice? presets = null,
        CancellationToken cancellationToken = default)
    {
        if (games.Count == 0) return new(new LinuxDlssBatchResult([]), "");
        using var op = BeginOperation("dlss-batch", linkedToken: cancellationToken);
        if (op is null) throw new OperationRefusedException(T("Vm.AnotherRHIOperationAlreadyRunning"));
        var tally = new BatchPresetTally();
        try
        {
            StatusText = TF("Vm.DLSSStreamlineBatchPreparingGameS", games.Count);
            var indexes = await PrepareDlssBatchIndexesAsync(games, op.Token);
            // #215, upstream's order (MassDlssDeployDialog): game by game, that game's DLL swaps, then its presets. Cancel
            // stops between games, so a game not reached yet keeps its presets, and one whose swaps ran gets its presets too.
            var items = new List<LinuxDlssBatchItemResult>();
            foreach (var game in games)
            {
                op.Token.ThrowIfCancellationRequested();
                if (defaults.HasAny)
                    items.AddRange((await _dlssDefaults.ApplyBatchAsync([game], defaults, Files(indexes), op.Token)).Items);
                if (presets is { HasAny: true })
                    await CopyBatchPresetsAsync(game, indexes, presets, tally);
            }
            var result = defaults.HasAny ? new LinuxDlssBatchResult(items) : null;
            await InvalidateAllAndReprobeSelectedAsync(); // Refs #72
            var line = Join(result is null ? "" : Join(UiLocalizationService.Present(result.Summary), FailedGamesLine(result), // #249
                    NrNotForThisCardLine(result.Items, defaults.NeuralRendering)),
                presets is { HasAny: true } ? tally.Line() : ""); // #229: the NR step skipped for a DLL made for another card
            StatusText = line;
            return new(result ?? new LinuxDlssBatchResult([]), line);
        }
        catch (Exception ex) when (op.WasCancelled(ex))
        {
            await InvalidateAllAndReprobeSelectedAsync(); // games finished before the cancel were changed
            var line = Join(OperationCancelledText(), tally.Reached.Count > 0 ? TF("Dp.BatchPresetsBeforeCancel", string.Join(", ", tally.Reached)) : "");
            StatusText = line;
            throw new DlssBatchCancelledException(line, ex);
        }
        catch (Exception ex) when (ex is not OperationRefusedException && tally.Reached.Count > 0)
        {
            // w10 follow-up D14: a failure partway through still names the games that already got their presets.
            await InvalidateAllAndReprobeSelectedAsync();
            throw new DlssBatchFailedException(TF("Dp.BatchPresetsBeforeFailure", string.Join(", ", tally.Reached)), ex);
        }
        finally
        {
            NotifyDlssDefaults();
        }
    }

    public async Task<DlssBatchOutcome> RestoreDlssBatchAsync(
        IReadOnlyList<DetectedGame> games,
        CancellationToken cancellationToken = default)
    {
        if (games.Count == 0) return new(new LinuxDlssBatchResult([]), "");
        using var op = BeginOperation("dlss-batch", linkedToken: cancellationToken);
        if (op is null) throw new OperationRefusedException(T("Vm.AnotherRHIOperationAlreadyRunning"));
        var reset = new List<string>();
        string? refused = null;
        try
        {
            StatusText = TF("Vm.DLSSStreamlineBatchRestorePreparingGame", games.Count);
            var indexes = await PrepareDlssBatchIndexesAsync(games, op.Token);
            // #215 (upstream's mass restore, game by game): that game's DLLs back, then its DLSS presets back to Default
            // and the newest-DLSS driver override off; its render scale and frame-generation settings stay.
            var items = new List<LinuxDlssBatchItemResult>();
            foreach (var game in games)
            {
                op.Token.ThrowIfCancellationRequested();
                items.AddRange((await _dlssDefaults.RestoreBatchAsync([game], Files(indexes), op.Token)).Items);
                BeforeBatchPresetsForTesting?.Invoke(game);
                var write = Shell.Runtime.WriteNvidiaSettings([game], (_, saved) =>
                {
                    if (saved is null) return null;
                    var next = saved.Copy();
                    next.SuperResolutionPreset = next.RayReconstructionPreset = next.FrameGenerationPreset = DlssPresetCatalogue.Default;
                    next.NewestDlss = false;
                    return next;
                });
                if (write.Changed > 0) reset.Add(game.Name);
                refused ??= write.Refused;
            }
            var result = new LinuxDlssBatchResult(items);
            await InvalidateAllAndReprobeSelectedAsync(); // Refs #72
            var line = Join(UiLocalizationService.Present(result.Summary), FailedGamesLine(result), // #249
                reset.Count > 0 ? TF("Dp.BatchPresetsReset", reset.Count) : "", refused ?? "");
            StatusText = line;
            return new(result, line);
        }
        catch (Exception ex) when (op.WasCancelled(ex))
        {
            await InvalidateAllAndReprobeSelectedAsync(); // games finished before the cancel were changed
            var line = Join(OperationCancelledText(), reset.Count > 0 ? TF("Dp.BatchPresetsResetBeforeCancel", string.Join(", ", reset)) : "");
            StatusText = line;
            throw new DlssBatchCancelledException(line, ex);
        }
        catch (Exception ex) when (ex is not OperationRefusedException && reset.Count > 0)
        {
            await InvalidateAllAndReprobeSelectedAsync(); // w10 follow-up D14
            throw new DlssBatchFailedException(TF("Dp.BatchPresetsResetBeforeFailure", string.Join(", ", reset)), ex);
        }
        finally
        {
            NotifyDlssDefaults();
        }
    }

    private Func<DetectedGame, IReadOnlyList<string>?> Files(IReadOnlyDictionary<string, IReadOnlyList<string>> indexes)
        => game => indexes.TryGetValue(Key(game), out var files) ? files : null;

    // ---- #215: default presets into a game's own NVIDIA settings ---------------------------------------------------

    /// <summary>
    /// Quick Apply: copies <paramref name="presets"/> into <paramref name="game"/>'s NVIDIA settings where its dialog
    /// shows the row (<see cref="LinuxDlssDefaultsService.NvidiaPresetRowsFor(DetectedGame, DlssDetectionResult, Platform.Linux.Hdr.LinuxHdrHostSnapshot)"/>);
    /// the line says what was set and what was not, and why.
    /// </summary>
    private async Task<string> CopyPresetsAsync(DetectedGame game, DlssDetectionResult detection, NvidiaPresetChoice presets, CancellationToken token)
    {
        var host = Shell.Runtime.NvidiaHost;
        var rows = await Task.Run(() => LinuxDlssDefaultsService.NvidiaPresetRowsFor(game, detection, host), token);
        NvidiaPresetCopyResult? copy = null;
        var write = Shell.Runtime.WriteNvidiaSettings([game], (_, saved) => (copy = NvidiaPresetCopy.Apply(saved, presets, rows)).Settings);
        // Library writes wait (Repair after move): nothing was copied, the line says why.
        if (copy is null) return write.Refused ?? "";
        return Join(PresetCopyLine(game, copy, presets), write.Refused ?? "");
    }

    /// <summary>
    /// The batch: the same rule for one ticked game, right after its DLL swaps; <paramref name="tally"/> counts the games
    /// set and skipped (by reason) for the line. Runs to the end once called (the game's swaps already ran), so a
    /// Cancel pressed meanwhile still leaves the game with both. A game without a confirmed patch target has no index
    /// (<see cref="PrepareDlssBatchIndexesAsync"/> skips it), so its files are unknown and no row is offered.
    /// </summary>
    private async Task CopyBatchPresetsAsync(DetectedGame game, IReadOnlyDictionary<string, IReadOnlyList<string>> indexes,
        NvidiaPresetChoice presets, BatchPresetTally tally)
    {
        BeforeBatchPresetsForTesting?.Invoke(game);
        var host = Shell.Runtime.NvidiaHost;
        var rows = await Task.Run(() =>
        {
            var detection = indexes.TryGetValue(Key(game), out var files)
                ? _dlssDefaults.DetectTargets(game, files)
                : new DlssDetectionResult();
            return LinuxDlssDefaultsService.NvidiaPresetRowsFor(game, detection, host);
        });
        NvidiaPresetCopyResult? copy = null;
        var write = Shell.Runtime.WriteNvidiaSettings([game], (_, saved) => (copy = NvidiaPresetCopy.Apply(saved, presets, rows)).Settings);
        tally.Add(game, copy, write);
    }

    /// <summary>Test seam: runs as a batch game's presets are about to be set (Apply) or reset (Restore selected).</summary>
    internal Action<DetectedGame>? BeforeBatchPresetsForTesting { get; set; }

    /// <summary>#215: what the batch's preset copies did, per game: set, skipped by reason, the games reached.</summary>
    private sealed class BatchPresetTally
    {
        private int _copied, _set, _notShown, _dlss1x;
        private string? _refused;

        /// <summary>The games whose NVIDIA settings now hold a copied preset, in batch order (the Cancel line names them).</summary>
        public List<string> Reached { get; } = [];

        public void Add(DetectedGame game, NvidiaPresetCopyResult? copy, NvidiaSettingsWrite write)
        {
            _refused ??= write.Refused;
            // Library writes wait (Repair after move): nothing was copied.
            if (copy is null) return;
            _copied++;
            if (copy.AnySet) _set++;
            if (copy.Skipped.Any(s => s.Why == NvidiaPresetRowState.NotShown)) _notShown++;
            if (copy.Skipped.Any(s => s.Why == NvidiaPresetRowState.Dlss1x)) _dlss1x++;
            if (copy.AnySet) Reached.Add(game.Name);
        }

        /// <summary>The batch line: games set, skipped because the row is not offered, skipped over DLSS 1.x; or why nothing was copied.</summary>
        public string Line()
            => _copied == 0
                ? _refused ?? ""
                : Join(TF("Dp.BatchPresets", _set),
                    _notShown > 0 ? TF("Dp.BatchPresetsNotShown", _notShown) : "",
                    _dlss1x > 0 ? TF("Dp.BatchPresetsDlss1x", _dlss1x) : "",
                    _refused ?? "");
    }

    private static string PresetCopyLine(DetectedGame game, NvidiaPresetCopyResult copy, NvidiaPresetChoice presets)
    {
        var parts = new List<string>();
        if (copy.AnySet)
            parts.Add(TF("Dp.PresetsSet", game.Name, string.Join(", ", copy.Set.Select(slot => $"{PresetLabel(slot)}: {ShowPreset(slot, presets[slot])}"))));
        var notShown = copy.Skipped.Where(s => s.Why == NvidiaPresetRowState.NotShown).Select(s => PresetLabel(s.Slot)).ToArray();
        if (notShown.Length > 0) parts.Add(TF("Dp.PresetsNotShown", string.Join(", ", notShown)));
        var legacy = copy.Skipped.Where(s => s.Why == NvidiaPresetRowState.Dlss1x).Select(s => PresetLabel(s.Slot)).ToArray();
        if (legacy.Length > 0) parts.Add(TF("Dp.PresetsDlss1x", string.Join(", ", legacy)));
        return string.Join(" ", parts);
    }

    /// <summary>The row's label, as the NVIDIA settings… dialog names it.</summary>
    private static string PresetLabel(NvidiaPresetSlot slot) => T(slot switch
    {
        NvidiaPresetSlot.SuperResolution => "Nv.SrPreset",
        NvidiaPresetSlot.RayReconstruction => "Nv.RrPreset",
        _ => "Nv.FgPreset"
    });

    /// <summary>A preset's label in the current manifest lists ("—" for none chosen).</summary>
    private static string ShowPreset(NvidiaPresetSlot slot, uint? value)
    {
        if (value is not { } preset) return "—";
        var lists = DlssPresetCatalogue.Current;
        var list = slot switch
        {
            NvidiaPresetSlot.SuperResolution => lists.SuperResolution,
            NvidiaPresetSlot.RayReconstruction => lists.RayReconstruction,
            _ => lists.FrameGeneration
        };
        return OptionLists.DlssPresetsWithSaved(list, preset).Display(preset);
    }

    private static string Join(params string[] lines) => string.Join(" ", lines.Where(line => !string.IsNullOrWhiteSpace(line)));

    private async Task<Dictionary<string, IReadOnlyList<string>>> PrepareDlssBatchIndexesAsync(
        IReadOnlyList<DetectedGame> games,
        CancellationToken cancellationToken)
    {
        var indexes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var game in games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!game.HasConfirmedPatchTarget) continue;
            var key = Key(game);
            if (Host.Session.CachedFiles(game) is { } cached)
            {
                indexes[key] = cached;
                continue;
            }
            if (string.IsNullOrWhiteSpace(game.InstallPath) || !Directory.Exists(game.InstallPath))
            {
                indexes[key] = Array.Empty<string>();
                continue;
            }
            indexes[key] = await Task.Run(() => GameFileIndexer.Build(game.InstallPath, 8, cancellationToken), cancellationToken);
        }
        return indexes;
    }

    [RelayCommand]
    private async Task SwapDlssComponentAsync(string? component)
    {
        if (SelectedGame is not { } game || !TryResolveComponent(component, out var kind, out var version, out var paths)) return;
        if (!SelectedGame.HasConfirmedPatchTarget)
        {
            StatusText = T("Vm.SetTargetExecutableEditGameBefore4");
            return;
        }
        if (OrchestratedDlssStackManaged)
        {
            StatusText = ShortFuseInstalled
                ? T("Vm.DirectDLSSSwappingBlockedWhileShortFuse")
                : T("Vm.DirectDLSSSwappingBlockedWhileRHI");
            return;
        }
        if (string.IsNullOrWhiteSpace(version))
        {
            StatusText = T("Vm.ChooseDLSSVersionFirst");
            return;
        }
        if (paths.Count == 0)
        {
            StatusText = TF("Vm.NoExistingTargetDLLWasFound", DlssComponentDisplayName(kind));
            return;
        }

        using var op = BeginOperation("dlss-swap");
        if (op is null) return;
        try
        {
            var displayName = DlssComponentDisplayName(kind);
            StatusText = TF("Vm.DownloadingApplying", displayName, version);
            var result = await _dlss.SwapAsync(kind, paths, version, op.Token);
            if (result.Success)
            {
                var changed = result.Results.Count(x => x.Changed);
                StatusText = changed == 0
                    ? TF("Vm.AlreadyMatchesGameFileNothingChanged", displayName, version)
                    : TF("Vm.AppliedFileSOriginalPreservedAs", displayName, version, changed);
                await ReprobeKeepingStatusAsync(game); // the .original backups are in the file index (Refs #83)
            }
            else
            {
                StatusText = op.CancelRequested ? OperationCancelledText() : TF("Vm.FailedSwap", displayName, UiLocalizationService.Present(result.Error));
            }
        }
        catch (Exception ex) when (op.WasCancelled(ex))
        {
            StatusText = OperationCancelledText();
        }
        catch (Exception ex)
        {
            StatusText = TF("Vm.FailedSwap", DlssComponentDisplayName(kind), UiLocalizationService.Present(ex.Message));
        }
    }

    [RelayCommand]
    private async Task RestoreDlssComponentAsync(string? component)
    {
        if (SelectedGame is not { } game || !TryResolveComponent(component, out var kind, out _, out var paths)) return;
        if (!SelectedGame.HasConfirmedPatchTarget)
        {
            StatusText = T("Vm.SetTargetExecutableEditGameBefore5");
            return;
        }
        if (OrchestratedDlssStackManaged)
        {
            StatusText = ShortFuseInstalled
                ? T("Vm.DirectDLSSRestoreBlockedWhileShortFuse")
                : T("Vm.DirectDLSSRestoreBlockedWhileRHI");
            return;
        }
        if (paths.Count == 0)
        {
            StatusText = TF("Vm.NoExistingTargetDLLWasFound2", DlssComponentDisplayName(kind));
            return;
        }

        using var op = BeginOperation("dlss-swap");
        if (op is null) return;
        try
        {
            var displayName = DlssComponentDisplayName(kind);
            var result = await _dlss.RestoreAsync(kind, paths, op.Token);
            StatusText = result.Success
                ? TF("Vm.OriginalDLLRestored", displayName)
                : op.CancelRequested ? OperationCancelledText() : TF("Vm.RestoreUnavailable", displayName, UiLocalizationService.Present(result.Error));
            if (result.Success) await ReprobeKeepingStatusAsync(game); // Refs #83
        }
        catch (Exception ex) when (op.WasCancelled(ex))
        {
            StatusText = OperationCancelledText();
        }
        catch (Exception ex)
        {
            StatusText = TF("Vm.RestoreFailed", DlssComponentDisplayName(kind), UiLocalizationService.Present(ex.Message));
        }
    }

    [RelayCommand]
    private async Task SwapStreamlineAsync()
    {
        if (SelectedGame is not { } game || IsBusy) return;
        if (OrchestratedDlssStackManaged)
        {
            StatusText = ShortFuseInstalled
                ? T("Vm.DirectStreamlineSwappingBlockedWhileShortFuse")
                : T("Vm.DirectStreamlineSwappingBlockedWhileDLSS5");
            return;
        }
        if (string.IsNullOrWhiteSpace(SelectedStreamlineVersion))
        {
            StatusText = T("Vm.ChooseStreamlineVersionFirst");
            return;
        }
        var streamlineFolders = _dlssDefaults.PanelStreamlineFolders(SelectedGame, _dlssDetection);
        if (streamlineFolders.Count == 0)
        {
            StatusText = T("Vm.StreamlineNoLocalStreamlineFolderWas");
            return;
        }

        using var op = BeginOperation("streamline");
        if (op is null) return;
        try
        {
            StatusText = TF("Vm.DownloadingApplyingStreamline", SelectedStreamlineVersion);
            var result = await _dlss.SwapStreamlineAsync(streamlineFolders, SelectedStreamlineVersion, op.Token);
            if (result.Success)
            {
                var changed = result.Results.Count(x => x.Changed);
                StatusText = changed == 0
                    ? TF("Vm.StreamlineAlreadyMatchesAllDetectedDLLs", SelectedStreamlineVersion)
                    : TF("Vm.StreamlineAppliedExistingDLLSOriginals", SelectedStreamlineVersion, changed);
                await ReprobeKeepingStatusAsync(game); // Refs #83
            }
            else
            {
                StatusText = op.CancelRequested ? OperationCancelledText() : TF("Vm.FailedSwapStreamline", UiLocalizationService.Present(result.Error));
            }
        }
        catch (Exception ex) when (op.WasCancelled(ex))
        {
            StatusText = OperationCancelledText();
        }
        catch (Exception ex)
        {
            StatusText = TF("Vm.FailedSwapStreamline", UiLocalizationService.Present(ex.Message));
        }
    }

    [RelayCommand]
    private async Task RestoreStreamlineAsync()
    {
        if (SelectedGame is not { } game || IsBusy) return;
        if (OrchestratedDlssStackManaged)
        {
            StatusText = ShortFuseInstalled
                ? T("Vm.DirectStreamlineRestoreBlockedWhileShortFuse")
                : T("Vm.DirectStreamlineRestoreBlockedWhileDLSS5");
            return;
        }
        var streamlineFolders = _dlssDefaults.PanelStreamlineFolders(SelectedGame, _dlssDetection);
        if (streamlineFolders.Count == 0)
        {
            StatusText = T("Vm.StreamlineNoLocalStreamlineFolderWas2");
            return;
        }

        using var op = BeginOperation("streamline");
        if (op is null) return;
        try
        {
            var result = await _dlss.RestoreStreamlineAsync(streamlineFolders, op.Token);
            StatusText = result.Success
                ? TF("Vm.StreamlineOriginalDLLSRestored", result.Results.Count)
                : op.CancelRequested ? OperationCancelledText() : TF("Vm.StreamlineRestoreUnavailable", UiLocalizationService.Present(result.Error));
            if (result.Success) await ReprobeKeepingStatusAsync(game); // Refs #83
        }
        catch (Exception ex) when (op.WasCancelled(ex))
        {
            StatusText = OperationCancelledText();
        }
        catch (Exception ex)
        {
            StatusText = TF("Vm.StreamlineRestoreFailed", UiLocalizationService.Present(ex.Message));
        }
    }

    // Swap/restore targets come from LinuxDlssDefaultsService (ownership exclusion re-applied at action time).
    private bool TryResolveComponent(string? component, out DlssComponentKind kind, out string? version, out IReadOnlyList<string> paths)
    {
        if (!_dlssDefaults.TryPanelTargets(SelectedGame, _dlssDetection, component, out kind, out paths))
        {
            version = null;
            return false;
        }
        version = kind switch
        {
            DlssComponentKind.SuperResolution => SelectedDlssSrVersion,
            DlssComponentKind.RayReconstruction => SelectedDlssRrVersion,
            DlssComponentKind.FrameGeneration => SelectedDlssFgVersion,
            _ => SelectedDlssNrVersion
        };
        return true;
    }

    private static string DlssComponentDisplayName(DlssComponentKind kind) => kind switch
    {
        DlssComponentKind.SuperResolution => UiLocalizationService.Text("Ui.SuperResolution"),
        DlssComponentKind.RayReconstruction => UiLocalizationService.Text("Ui.RayReconstruction"),
        DlssComponentKind.FrameGeneration => UiLocalizationService.Text("Ui.FrameGeneration"),
        DlssComponentKind.NeuralRendering => UiLocalizationService.Text("Ui.NeuralRendering"),
        _ => "DLSS"
    };

    internal void NotifyDlssDefaults()
    {
        OnPropertyChanged(nameof(DlssVersionDefaults));
        OnPropertyChanged(nameof(HasDlssVersionDefaults));
        OnPropertyChanged(nameof(DlssPresetDefaults));
        OnPropertyChanged(nameof(HasDlssDefaults));
        OnPropertyChanged(nameof(DlssDefaultsSummary));
        OnPropertyChanged(nameof(CanQuickApplyDlssDefaults));
        OnPropertyChanged(nameof(CanRestoreAllDlss));
        QuickApplyDlssDefaultsCommand.NotifyCanExecuteChanged();
        RestoreAllDlssCommand.NotifyCanExecuteChanged();
    }

    private static string DlssDeploymentState(IReadOnlyList<string> targets, int backups)
    {
        if (targets.Count == 0) return T("Vm.Missing");
        return backups > 0
            ? TF("Vm.RHIApplied", backups, targets.Count)
            : targets.Count == 1 ? T("Vm.GameDLL") : TF("Vm.GameDLLs2", targets.Count);
    }

    internal static void FillVersions(ObservableCollection<string> target, IEnumerable<string> versions)
    {
        target.Clear();
        foreach (var version in versions) target.Add(version);
    }

    private static string FirstOrNotFound(IReadOnlyList<string> paths, string dll)
        => paths.FirstOrDefault() ?? TF("Vm.MissingDirectSwapBlocked", dll);
}

/// <summary>#215: a DLSS batch's result and the line the dialog shows (the DLL summary and, if any, the presets').</summary>
public sealed record DlssBatchOutcome(LinuxDlssBatchResult Result, string Line);

/// <summary>
/// #215: a DLSS batch stopped by Cancel; <see cref="Line"/> is the line the dialog shows ("Operation cancelled." and the
/// games whose presets were already set or reset).
/// </summary>
public sealed class DlssBatchCancelledException(string line, Exception inner)
    : OperationCanceledException(line, inner, (inner as OperationCanceledException)?.CancellationToken ?? default)
{
    public string Line { get; } = line;
}

/// <summary>
/// w10 follow-up D14: a DLSS batch that failed partway through (not by Cancel) after some games' presets were set or
/// reset; the message is the failure's own, <see cref="Note"/> names those games (the dialog adds it to its line).
/// </summary>
public sealed class DlssBatchFailedException(string note, Exception inner) : Exception(inner.Message, inner)
{
    public string Note { get; } = note;
}
