using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Hdr;

namespace RHI.Desktop.ViewModels;

/// <summary>One eligible game in the DLSS / Streamline batch dialog: ticked or not.</summary>
public sealed partial class DlssBatchGameRow(LinuxDlssBatchCandidate candidate) : ObservableObject
{
    public LinuxDlssBatchCandidate Candidate { get; } = candidate;
    public string Name => Candidate.Game.Name;
    public string Reason { get; } = UiLocalizationService.Present(candidate.Reason);
    [ObservableProperty] private bool isChecked;
}

/// <summary>
/// One version dropdown of a dialog ("Do not change", the manifest's versions, and the saved value kept even when the
/// manifest is offline). <see cref="Value"/> is "" for "Do not change".
/// </summary>
public sealed partial class VersionChoice : ObservableObject
{
    public VersionChoice(string label, IEnumerable<string> versions, string current)
    {
        Label = label;
        var items = new List<string> { OptionLists.DoNotChangeDisplay() };
        foreach (var version in versions.Where(version => !string.IsNullOrWhiteSpace(version)).Distinct(StringComparer.OrdinalIgnoreCase))
            items.Add(version);
        if (!string.IsNullOrWhiteSpace(current) && !items.Contains(current, StringComparer.OrdinalIgnoreCase))
            items.Add(current); // keep the saved default visible/offline instead of silently resetting it
        Items = items;
        selected = string.IsNullOrWhiteSpace(current)
            ? items[0]
            : items.First(item => string.Equals(item, current, StringComparison.OrdinalIgnoreCase));
    }

    public string Label { get; }
    public IReadOnlyList<string> Items { get; }

    private string selected;

    /// <summary>The shown entry; a null written back by the dropdown (its items reset) is not a choice and is ignored.</summary>
    public string? Selected
    {
        get => selected;
        set { if (value is not null) SetProperty(ref selected, value); }
    }

    /// <summary>The chosen version, or "" for "Do not change" (either language: the dialog can outlive a switch).</summary>
    public string Value => OptionLists.IsDoNotChange(selected) ? "" : selected;

    /// <summary>Back to "Do not change".</summary>
    public void Clear() => Selected = OptionLists.DoNotChangeDisplay();
}

/// <summary>
/// #215: one DLSS preset dropdown (Settings' default presets, the DLSS batch): the manifest's preset list with the same
/// labels as the NVIDIA settings (Default first), the saved value kept when the manifest no longer lists it, and — in
/// the batch — "Do not change" first. <see cref="Value"/> is null for "Do not change".
/// </summary>
public sealed partial class PresetChoice : ObservableObject
{
    private readonly OptionItem<uint?> _cleared;

    public PresetChoice(string label, string tip, IReadOnlyList<DlssPreset> presets, uint current, bool doNotChange)
    {
        Label = label;
        Tip = tip;
        var list = OptionLists.DlssPresetChoices(presets, current, doNotChange);
        Items = list.Options();
        _cleared = Items.For(list.Fallback);
        selected = doNotChange && current == DlssPresetCatalogue.Default ? _cleared : Items.For(current);
    }

    public string Label { get; }
    public string Tip { get; }
    public OptionItems<uint?> Items { get; }

    private OptionItem<uint?> selected;

    /// <summary>The shown entry; a null written back by the dropdown (its items reset) is not a choice and is ignored.</summary>
    public OptionItem<uint?>? Selected
    {
        get => selected;
        set { if (value is not null) SetProperty(ref selected, value); }
    }

    /// <summary>The chosen preset value; null = "Do not change".</summary>
    public uint? Value => selected.Value;

    /// <summary>Back to Default (Settings) or "Do not change" (batch).</summary>
    public void Clear() => Selected = _cleared;
}

/// <summary>
/// The DLSS / Streamline batch dialog (epic #89: was built in the window's code-behind). Lists the eligible games, the
/// five version dropdowns filled from Settings' defaults, and runs Apply / Restore selected through the DLSS panel (busy
/// gate). While a batch runs, Close becomes Cancel (the main window's overlay is behind this modal dialog, Refs #77)
/// and the window cannot be closed.
/// </summary>
public sealed partial class DlssBatchDialogViewModel : ObservableObject
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    private readonly Func<IReadOnlyList<DetectedGame>, LinuxDlssVersionDefaults, NvidiaPresetChoice, Task<DlssBatchOutcome>> _apply;
    private readonly Func<IReadOnlyList<DetectedGame>, Task<DlssBatchOutcome>> _restore;
    private readonly Action _cancel;

    /// <summary>The eligible candidates, by name; null when none is eligible (the caller says so instead of opening).</summary>
    public static IReadOnlyList<LinuxDlssBatchCandidate> Eligible(IEnumerable<LinuxDlssBatchCandidate> candidates)
        => candidates.Where(candidate => candidate.Eligible)
            .OrderBy(candidate => candidate.Game.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The dialog over the DLSS panel: its version lists, Settings' defaults, its batch operations.</summary>
    internal static DlssBatchDialogViewModel For(DlssPanelViewModel dlss, OperationCoordinator operations, IReadOnlyList<LinuxDlssBatchCandidate> eligible)
        => new(eligible,
            [dlss.DlssSrVersions, dlss.DlssRrVersions, dlss.DlssFgVersions, dlss.DlssNrVersions, dlss.StreamlineVersions],
            dlss.DlssVersionDefaults,
            (games, chosen, presets) => dlss.ApplyDlssBatchAsync(games, chosen, presets),
            games => dlss.RestoreDlssBatchAsync(games),
            operations.Cancel,
            dlss.DlssPresetDefaults);

    /// <param name="versions">SR, RR, FG, NR and Streamline versions, in that order.</param>
    /// <param name="presetDefaults">#215: Settings' default presets the preset dropdowns start from (none = "Do not change").</param>
    /// <param name="presetLists">#215: the preset lists (default: the manifest's, <see cref="DlssPresetCatalogue.Current"/>).</param>
    internal DlssBatchDialogViewModel(
        IReadOnlyList<LinuxDlssBatchCandidate> eligible,
        IReadOnlyList<IEnumerable<string>> versions,
        LinuxDlssVersionDefaults defaults,
        Func<IReadOnlyList<DetectedGame>, LinuxDlssVersionDefaults, NvidiaPresetChoice, Task<DlssBatchOutcome>> apply,
        Func<IReadOnlyList<DetectedGame>, Task<DlssBatchOutcome>> restore,
        Action cancel,
        NvidiaPresetChoice? presetDefaults = null,
        DlssPresetLists? presetLists = null)
    {
        _apply = apply;
        _restore = restore;
        _cancel = cancel;
        Games = new(eligible.Select(candidate => new DlssBatchGameRow(candidate)));
        EligibleHeading = TF("Win.EligibleGames", Games.Count);
        SuperResolution = new(T("Win.SuperResolution"), versions[0], defaults.SuperResolution);
        RayReconstruction = new(T("Win.RayReconstruction"), versions[1], defaults.RayReconstruction);
        FrameGeneration = new(T("Win.FrameGeneration"), versions[2], defaults.FrameGeneration);
        NeuralRendering = new(T("Win.NeuralRendering"), versions[3], defaults.NeuralRendering);
        Streamline = new("Streamline", versions[4], defaults.Streamline);
        // #215 (upstream MassDlssDeployDialog): the three presets under the versions, filled from Settings' defaults.
        var lists = presetLists ?? DlssPresetCatalogue.Current;
        var chosen = presetDefaults ?? NvidiaPresetChoice.None;
        var tip = T("Dp.BatchPresetsTip");
        SuperResolutionPreset = new(T("Nv.SrPreset"), tip, lists.SuperResolution, chosen.SuperResolution ?? DlssPresetCatalogue.Default, doNotChange: true);
        RayReconstructionPreset = new(T("Nv.RrPreset"), tip, lists.RayReconstruction, chosen.RayReconstruction ?? DlssPresetCatalogue.Default, doNotChange: true);
        FrameGenerationPreset = new(T("Nv.FgPreset"), tip, lists.FrameGeneration, chosen.FrameGeneration ?? DlssPresetCatalogue.Default, doNotChange: true);
        closeLabel = T("Win.Close");
    }

    public string WindowTitle => T("Win.DLSSStreamlineBatchOperation");
    public ObservableCollection<DlssBatchGameRow> Games { get; }
    public string EligibleHeading { get; }
    public VersionChoice SuperResolution { get; }
    public VersionChoice RayReconstruction { get; }
    public VersionChoice FrameGeneration { get; }
    public VersionChoice NeuralRendering { get; }
    public VersionChoice Streamline { get; }
    public IReadOnlyList<VersionChoice> Versions => [SuperResolution, RayReconstruction, FrameGeneration, NeuralRendering, Streamline];
    public PresetChoice SuperResolutionPreset { get; }
    public PresetChoice RayReconstructionPreset { get; }
    public PresetChoice FrameGenerationPreset { get; }
    public IReadOnlyList<PresetChoice> Presets => [SuperResolutionPreset, RayReconstructionPreset, FrameGenerationPreset];

    [ObservableProperty] private string status = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanRun))] private bool isRunning;
    [ObservableProperty] private string closeLabel;
    [ObservableProperty] private bool canClose = true;

    /// <summary>Apply and Restore selected are off while a batch runs.</summary>
    public bool CanRun => !IsRunning;

    /// <summary>Asks the window to close (Close pressed while nothing runs).</summary>
    public event Action? CloseRequested;

    [RelayCommand]
    private void SelectAll() { foreach (var row in Games) row.IsChecked = true; }

    [RelayCommand]
    private void ClearAll() { foreach (var row in Games) row.IsChecked = false; }

    [RelayCommand]
    private Task Apply() => RunAsync(restoreMode: false);

    [RelayCommand]
    private Task Restore() => RunAsync(restoreMode: true);

    /// <summary>Close, or — while a batch runs — Cancel (pressed once: it greys out until the batch has stopped).</summary>
    [RelayCommand]
    private void Close()
    {
        if (!IsRunning) { CloseRequested?.Invoke(); return; }
        CanClose = false;
        _cancel();
    }

    private IReadOnlyList<DetectedGame> SelectedGames() => Games.Where(row => row.IsChecked).Select(row => row.Candidate.Game).ToArray();

    internal async Task RunAsync(bool restoreMode)
    {
        var selected = SelectedGames();
        if (selected.Count == 0)
        {
            Status = T("Win.SelectAtLeastOneGame");
            return;
        }

        var chosen = new LinuxDlssVersionDefaults(
            SuperResolution.Value, RayReconstruction.Value, FrameGeneration.Value, NeuralRendering.Value, Streamline.Value);
        var presets = new NvidiaPresetChoice(SuperResolutionPreset.Value, RayReconstructionPreset.Value, FrameGenerationPreset.Value);
        if (!restoreMode && !chosen.HasAny && !presets.HasAny)
        {
            Status = T("Win.SelectAtLeastOneVersionUsing");
            return;
        }

        IsRunning = true;
        CloseLabel = T("Win.Cancel");
        try
        {
            Status = restoreMode ? T("Win.RestoringSelectedGames") : T("Win.ApplyingSelectedGames");
            var outcome = restoreMode ? await _restore(selected) : await _apply(selected, chosen, presets);
            Status = outcome.Line;
        }
        catch (OperationRefusedException refused)
        {
            Status = refused.Message;
        }
        catch (OperationCanceledException cancelled)
        {
            // #215: the batch's own line names the games whose presets were already set or reset.
            Status = cancelled is DlssBatchCancelledException batch ? batch.Line : T("Vm.OperationCancelled");
        }
        catch (Exception ex)
        {
            Status = TF("Win.BatchOperationFailed", UiLocalizationService.Present(ex.Message));
        }
        finally
        {
            IsRunning = false;
            CloseLabel = T("Win.Close");
            CanClose = true;
        }
    }
}
