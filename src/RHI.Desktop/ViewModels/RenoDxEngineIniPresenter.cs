using CommunityToolkit.Mvvm.ComponentModel;
using RHI.Core.Models;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// w8/renodx (#217): the RenoDX ⚙'s Engine.ini rows for the selected game: "Fix colour grading updates (Unreal)"
/// (RenoDX's <c>r.LUT.UpdateEveryFrame=1</c>). Shown only where the setting applies (an Unreal game whose RenoDX RHI
/// installed, no custom Engine.ini file, not excluded by the manifest, an Engine.ini location found). Each change is one
/// journaled RenoDX operation behind the busy gate; the line it shows says what changed.
/// </summary>
public sealed class RenoDxEngineIniPresenter(MainWindowViewModel vm, LinuxManagedComponentService managed)
{
    private static string T(string key) => UiLocalizationService.Text(key);

    /// <summary>The rows for the selected game (none where nothing applies). <paramref name="show"/> receives each result line.</summary>
    public IReadOnlyList<RenoDxEngineIniRow> RowsFor(Action<string> show)
    {
        if (vm.SelectedGame is not { HasConfirmedPatchTarget: true } game || managed.RenoDx is not { } renodx) return [];
        var rows = new List<RenoDxEngineIniRow>();
        if (renodx.OffersEngineIniLut(game))
            rows.Add(new RenoDxEngineIniRow("RenoDxEngineIniLut", T("Rdx.LutLabel"),
                UiLocalizationService.Format("Rdx.LutTip", RenoDxDeploymentService.EngineIniLutLines),
                renodx.EngineIniLutIsOn(game),
                on => SetAsync(game, on, renodx.SetEngineIniLutAsync, () => renodx.EngineIniLutIsOn(game), "Rdx.LutOn", "Rdx.LutOff", "Rdx.LutOffKept"),
                show));
        return rows;
    }

    private async Task<(bool Applied, bool IsOn, string Line)> SetAsync(DetectedGame game, bool on,
        Func<DetectedGame, bool, CancellationToken, Task<ComponentOperationResult>> set, Func<bool> isOn, string onKey, string offKey, string keptKey)
    {
        if (managed.IniWriteRefusal(game) is { } refused) return (false, isOn(), UiLocalizationService.Present(refused));
        using var op = vm.Operations.TryBegin("renodx-engine-ini", cancellable: false);
        if (op is null) return (false, isOn(), T("Vm.AnotherRHIOperationAlreadyRunning"));
        var result = await set(game, on, op.Token);
        var now = isOn();
        if (!result.Success) return (false, now, UiLocalizationService.Present(result.Message));
        // A line the game's Engine.ini had before RHI is never removed: say so when it still reads as on.
        return (true, now, T(on ? onKey : now ? keptKey : offKey));
    }
}

/// <summary>
/// One Engine.ini On/Off row of the RenoDX ⚙: label, ~2 s tooltip (the INI key lives there), the Off/On dropdown. A choice
/// runs at once; afterwards the dropdown shows what is in effect (a refused or failed change puts it back).
/// </summary>
public sealed partial class RenoDxEngineIniRow : ObservableObject
{
    private readonly Func<bool, Task<(bool Applied, bool IsOn, string Line)>> _set;
    private readonly Action<string> _show;
    private bool _reverting;

    internal RenoDxEngineIniRow(string name, string label, string tooltip, bool isOn,
        Func<bool, Task<(bool Applied, bool IsOn, string Line)>> set, Action<string> show)
    {
        Name = name;
        Label = label;
        Tooltip = tooltip;
        _set = set;
        _show = show;
        Options = OptionLists.OffOn.Items;
        selectedIndex = isOn ? 1 : 0;
    }

    /// <summary>The dropdown's control name (harness checks find the row by it).</summary>
    public string Name { get; }
    public string Label { get; }
    public string Tooltip { get; }
    public IReadOnlyList<string> Options { get; }
    [ObservableProperty] private int selectedIndex;
    [ObservableProperty] private bool isIdle = true;

    /// <summary>The last change's task (tests await it).</summary>
    internal Task Pending { get; private set; } = Task.CompletedTask;

    partial void OnSelectedIndexChanged(int value)
    {
        if (_reverting || value is < 0 or > 1) return;
        Pending = ApplyAsync(value == 1);
        UiExceptionGuard.Forget(Pending, nameof(RenoDxEngineIniRow));
    }

    private async Task ApplyAsync(bool on)
    {
        IsIdle = false;
        try
        {
            var (_, isOn, line) = await _set(on);
            _show(line);
            // Show what is in effect now (refused, failed, or a line the user had before RHI that stays).
            await Task.Yield(); // outside the dropdown's own write, so the control takes the value back
            _reverting = true;
            try { SelectedIndex = isOn ? 1 : 0; }
            finally { _reverting = false; }
        }
        finally
        {
            IsIdle = true;
        }
    }
}
