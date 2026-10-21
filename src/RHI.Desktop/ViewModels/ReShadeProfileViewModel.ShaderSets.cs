using CommunityToolkit.Mvvm.ComponentModel;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// #179 (upstream 2.8.7 per-game Shaders / Add-ons: Global / Select / Off, and the global sets in Settings): the two
/// per-game choices in the ReShade section and the global shader mode, set and add-ons. Global and Off apply at once;
/// Select opens the Shaders/Add-ons dialog for the game's own list (<see cref="ShowSelectList"/>, set by the view) and the
/// choice becomes Select only when that dialog applies it. A change reaches the game's files only through the existing
/// journaled paths (<see cref="ShaderAddonSetBatch"/>).
/// </summary>
public sealed partial class ReShadeProfileViewModel
{
    public OptionItems<ShaderAddonMode> ShaderModeOptions { get; } = OptionLists.ShaderAddonModes.Options();
    public OptionItems<ShaderAddonMode> AddonModeOptions { get; } = OptionLists.ShaderAddonModes.Options();
    public OptionItems<GlobalShaderMode> GlobalShaderModeOptions { get; } = OptionLists.GlobalShaderModes.Options();

    [ObservableProperty] private OptionItem<ShaderAddonMode>? selectedShaderMode;
    [ObservableProperty] private OptionItem<ShaderAddonMode>? selectedAddonMode;
    [ObservableProperty] private OptionItem<GlobalShaderMode>? selectedGlobalShaderMode;
    /// <summary>The Shaders choice applies: RHI installed ReShade on the selected game (hidden otherwise).</summary>
    [ObservableProperty] private bool showShaderMode;
    /// <summary>The Add-ons choice applies: as <see cref="ShowShaderMode"/>, and ReShade is not the No Addons build.</summary>
    [ObservableProperty] private bool showAddonMode;

    private bool _loadingSetModes;

    /// <summary>Set by the view: opens the Shaders/Add-ons dialog for the game's own list of that part (Select).</summary>
    internal Func<ShaderAddonSetPart, Task>? ShowSelectList { get; set; }

    /// <summary>The running mode change (a test seam; null when none ran).</summary>
    internal Task? ModeChange { get; private set; }

    private GlobalShaderAddonStore? Sets => _components.ShaderSets;

    /// <summary>The global sets are wired (the app; tests without them hide the Settings rows).</summary>
    internal bool HasShaderSets => Sets is not null;

    partial void OnShowShaderModeChanged(bool value) => OnPropertyChanged(nameof(ShowSetModes));
    partial void OnShowAddonModeChanged(bool value) => OnPropertyChanged(nameof(ShowSetModes));

    /// <summary>Either choice is shown.</summary>
    public bool ShowSetModes => ShowShaderMode || ShowAddonMode;

    /// <summary>Shows the selected game's saved choices (or hides them where they cannot apply).</summary>
    internal void LoadSetModes(DetectedGame? game)
    {
        _loadingSetModes = true;
        try
        {
            var choice = game is null ? null : Sets?.ChoiceFor(game);
            var managed = game is not null && choice is not null && _components.HasRhiReShade(game);
            ShowShaderMode = managed;
            ShowAddonMode = managed && _components.EffectiveAddons(game!) is { HeldByNoAddons: false };
            SelectedShaderMode = ShaderModeOptions.For(choice?.ShaderMode ?? ShaderAddonMode.Global);
            SelectedAddonMode = AddonModeOptions.For(choice?.AddonMode ?? ShaderAddonMode.Global);
            SelectedGlobalShaderMode = GlobalShaderModeOptions.For(Sets?.ShaderMode ?? GlobalShaderMode.Managed);
        }
        finally
        {
            _loadingSetModes = false;
        }
    }

    /// <summary>The UI language changed: the choices are relabelled in place.</summary>
    internal void RelabelSetModes()
    {
        ShaderModeOptions.Relabel();
        AddonModeOptions.Relabel();
        GlobalShaderModeOptions.Relabel();
        OnPropertyChanged(nameof(ShaderModeLabel));
        OnPropertyChanged(nameof(AddonModeLabel));
        OnPropertyChanged(nameof(ShaderModeTip));
        OnPropertyChanged(nameof(AddonModeTip));
    }

    public string ShaderModeLabel => T("Win.ShaderPacks");
    public string AddonModeLabel => T("Win.ReShadeAddOns");
    public string ShaderModeTip => T("Sets.ShaderModeTip");
    public string AddonModeTip => T("Sets.AddonModeTip");

    partial void OnSelectedShaderModeChanged(OptionItem<ShaderAddonMode>? value) => OnModeChosen(ShaderAddonSetPart.Shaders, value);

    partial void OnSelectedAddonModeChanged(OptionItem<ShaderAddonMode>? value) => OnModeChosen(ShaderAddonSetPart.Addons, value);

    private void OnModeChosen(ShaderAddonSetPart part, OptionItem<ShaderAddonMode>? value)
    {
        if (_loadingSetModes || value is null || SelectedGame is not { } game || Sets is null) return;
        ModeChange = value.Value == ShaderAddonMode.Select ? OpenSelectListAsync(game, part) : ChangeModeAsync(game, part, value.Value);
    }

    /// <summary>Select again while on Select (the dropdown closed on the same entry): the list opens again (upstream).</summary>
    internal void ReopenSelectList(ShaderAddonSetPart part)
    {
        if (SelectedGame is { } game && Sets?.ChoiceFor(game) is { } choice
            && (part == ShaderAddonSetPart.Shaders ? choice.ShaderMode : choice.AddonMode) == ShaderAddonMode.Select)
            ModeChange = OpenSelectListAsync(game, part);
    }

    private async Task OpenSelectListAsync(DetectedGame game, ShaderAddonSetPart part)
    {
        try
        {
            if (ShowSelectList is { } show) await show(part);
        }
        finally
        {
            // Applied in the dialog: Select is saved; closed without applying: the saved choice comes back.
            if (ReferenceEquals(SelectedGame, game)) LoadSetModes(game);
        }
    }

    private async Task ChangeModeAsync(DetectedGame game, ShaderAddonSetPart part, ShaderAddonMode mode)
    {
        using var op = BeginOperation("shader-sets");
        if (op is null)
        {
            await PutSavedChoiceBackAsync(game);
            return;
        }
        // Switching shaders to Global forgets the game's own pack list (upstream); add-ons keep theirs for a later Select.
        if (TryPersist(() =>
            {
                if (part == ShaderAddonSetPart.Shaders) Sets!.SetShaderMode(game, mode, mode == ShaderAddonMode.Global ? [] : null);
                else Sets!.SetAddonMode(game, mode);
            }, "Sets.WhatChoice", sessionOnly: false) is not null)
        {
            await PutSavedChoiceBackAsync(game);
            return;
        }
        await ApplySetsAsync(op, [game], part, globalOnly: false);
        await ReprobeKeepingStatusAsync(game);
    }

    // A refused choice is put back once the dropdown's own write has finished: a revert raised inside that write does not
    // reach the control (harness: the dropdown kept showing the refused entry).
    private async Task PutSavedChoiceBackAsync(DetectedGame? game)
    {
        await Task.Yield();
        LoadSetModes(game);
    }

    // ── Global sets (Settings → Shaders/Add-ons) ──────────────────────────

    partial void OnSelectedGlobalShaderModeChanged(OptionItem<GlobalShaderMode>? value)
    {
        if (_loadingSetModes || value is null || Sets is not { } sets || sets.ShaderMode == value.Value) return;
        ModeChange = ChangeGlobalShaderModeAsync(value.Value);
    }

    private async Task ChangeGlobalShaderModeAsync(GlobalShaderMode mode)
    {
        using var op = BeginOperation("shader-sets");
        if (op is null || TryPersist(() => Sets!.SetShaderMode(mode), "Sets.WhatChoice", sessionOnly: false) is not null)
        {
            await PutSavedChoiceBackAsync(SelectedGame);
            return;
        }
        await ApplySetsAsync(op, Host.AllGames, ShaderAddonSetPart.Shaders, globalOnly: true);
        if (SelectedGame is { } game) await ReprobeKeepingStatusAsync(game);
    }

    /// <summary>The global dialog's lists as saved now (the packs and add-ons games set to Global get).</summary>
    internal (IReadOnlyList<string> Shaders, IReadOnlyList<string> Addons) GlobalLists()
        => Sets is { } sets ? (sets.GlobalShaderPackIds, sets.GlobalAddonIds) : ([], []);

    /// <summary>
    /// The global dialog's Apply: saves the global set and add-ons and brings every game set to Global up to date (only
    /// the half that changed). Returns the status line.
    /// </summary>
    public async Task<ComponentOperationResult> ApplyGlobalSetsAsync(IReadOnlyList<string> shaderIds, IReadOnlyList<string> addonIds,
        CancellationToken cancellationToken = default)
    {
        if (Sets is not { } sets) return ComponentOperationResult.Fail(UiLocalizationService.Present(LinuxManagedComponentService.ShaderSetsUnreadableMessage));
        using var op = BeginOperation("shader-sets", linkedToken: cancellationToken);
        if (op is null) return ComponentOperationResult.Fail(T("Vm.AnotherRHIOperationAlreadyRunning"));
        var shadersChanged = !Same(sets.GlobalShaderPackIds, shaderIds);
        var addonsChanged = !Same(sets.GlobalAddonIds, addonIds);
        if (TryPersist(() =>
            {
                if (shadersChanged) sets.SetGlobalShaderPackIds(shaderIds);
                if (addonsChanged) sets.SetGlobalAddonIds(addonIds);
            }, "Sets.WhatChoice", sessionOnly: false) is { } refused)
            return ComponentOperationResult.Fail(refused);
        var lines = new List<string>();
        if (shadersChanged) lines.Add(await ApplySetsAsync(op, Host.AllGames, ShaderAddonSetPart.Shaders, globalOnly: true));
        if (addonsChanged) lines.Add(await ApplySetsAsync(op, Host.AllGames, ShaderAddonSetPart.Addons, globalOnly: true));
        if (SelectedGame is { } game) await ReprobeKeepingStatusAsync(game);
        var line = lines.Count == 0 ? SummaryText(ShaderAddonSetPart.Shaders, new(0, 0, 0, 0, 0, 0, false, [], [])) : string.Join(" ", lines);
        return ComponentOperationResult.Ok(ShowStatus(line));
    }

    private static bool Same(IReadOnlyCollection<string> a, IReadOnlyCollection<string> b)
        => a.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b);

    private async Task<string> ApplySetsAsync(OperationScope op, IReadOnlyList<DetectedGame> games, ShaderAddonSetPart part, bool globalOnly)
    {
        StatusText = T("Sets.Applying");
        ShaderAddonSetBatchResult result;
        try
        {
            result = await new ShaderAddonSetBatch(_components, RunningProbe).ApplyAsync(games, part, op.Token, globalOnly);
        }
        catch (Exception ex) when (op.WasCancelled(ex))
        {
            return ShowStatus(OperationCancelledText());
        }
        return ShowStatus(SummaryText(part, result));
    }

    /// <summary>Which games are running (a test seam; the real probe reads /proc).</summary>
    internal IGameRunningProbe? RunningProbe { get; set; }

    /// <summary>One status line from a batch's counts: "Shader packs: 2 game(s) updated; 1 failed, files kept: Game (why)."</summary>
    internal static string SummaryText(ShaderAddonSetPart part, ShaderAddonSetBatchResult result)
    {
        var parts = new List<string>();
        if (result.Changed > 0) parts.Add(TF("Sets.SumChanged", result.Changed));
        if (result.Unchanged > 0) parts.Add(TF("Sets.SumUnchanged", result.Unchanged));
        if (result.Held > 0) parts.Add(TF("Sets.SumHeld", result.Held));
        if (result.Running > 0) parts.Add(TF("Sets.SumRunning", result.Running, string.Join(", ", result.RunningGames)));
        if (result.Failed > 0)
            parts.Add(TF("Sets.SumFailed", result.Failed,
                string.Join("; ", result.Failures.Select(f => $"{f.Game} ({UiLocalizationService.Present(f.Reason).TrimEnd('.')})"))));
        if (result.NotRun > 0) parts.Add(TF("Sets.SumCancelled", result.NotRun));
        if (result.Excluded > 0) parts.Add(TF("Sets.SumExcluded", result.Excluded));
        if (parts.Count == 0) parts.Add(T("Sets.SumNone"));
        return $"{T(part == ShaderAddonSetPart.Shaders ? "Win.ShaderPacks" : "Win.ReShadeAddOns")}: {string.Join("; ", parts)}.";
    }
}
