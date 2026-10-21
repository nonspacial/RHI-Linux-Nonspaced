using System.ComponentModel;
using Avalonia.Data.Converters;
using CommunityToolkit.Mvvm.ComponentModel;
using RHI.Desktop.Diagnostics;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.ViewModels;

/// <summary>A line of the OptiScaler.ini part of the ⚙ dialog: a group divider, a group heading or a setting.</summary>
public abstract class OptiScalerIniItem : ObservableObject;

/// <summary>The thin line before every group but the first.</summary>
public sealed class OptiScalerIniDivider : OptiScalerIniItem;

/// <summary>A group heading (e.g. "Upscaling").</summary>
public sealed class OptiScalerIniGroup(string text) : OptiScalerIniItem
{
    public string Text { get; } = text;
}

/// <summary>
/// One OptiScaler.ini setting. A choice is written at once (it may download, under the busy gate); the dropdown is off
/// while it is written and goes back to the value in the file when the write fails.
/// </summary>
public sealed partial class OptiScalerIniSetting : OptiScalerIniItem
{
    private readonly OptiScalerSettingsDialogViewModel _dialog;
    private readonly OptiScalerAdvancedSetting _setting;
    private readonly OptiScalerAdvancedOption[] _options;
    private int _accepted;
    private bool _reverting;

    internal OptiScalerIniSetting(OptiScalerSettingsDialogViewModel dialog, OptiScalerAdvancedSetting setting)
    {
        _dialog = dialog;
        _setting = setting;
        _options = setting.Options.ToArray();
        Options = _options.Select(option => UiLocalizationService.Present(option.Name)).ToArray();
        var index = Array.FindIndex(_options, option => option.Value.Equals(setting.CurrentValue, StringComparison.OrdinalIgnoreCase));
        _accepted = selectedIndex = index >= 0 ? index : 0;
        Label = UiLocalizationService.Present(setting.Label);
        Help = string.IsNullOrWhiteSpace(setting.Help) ? null : UiLocalizationService.Present(setting.Help);
    }

    public string Id => _setting.Id;
    public string Label { get; }
    public string? Help { get; }
    public IReadOnlyList<string> Options { get; }
    [ObservableProperty] private int selectedIndex;
    [ObservableProperty] private bool isEnabled = true;

    /// <summary>Completes when the last choice has been written (tests).</summary>
    internal Task Applied { get; private set; } = Task.CompletedTask;

    partial void OnSelectedIndexChanged(int value)
    {
        if (_reverting || value < 0 || value >= _options.Length) return;
        Applied = ApplyAsync(value);
        UiExceptionGuard.Forget(Applied, "OptiScalerSettings_Click");
    }

    private async Task ApplyAsync(int requested)
    {
        IsEnabled = false;
        ComponentOperationResult result;
        string shown;
        _dialog.BeginApplying();
        try
        {
            result = await _dialog.SetSetting(_setting.Id, _options[requested].Value);
            shown = UiLocalizationService.Present(result.Message);
        }
        catch (Exception ex)
        {
            // Report returns an already-localised sentence; presenting it again could swap it for the generic text
            // because it embeds the (English) .NET message (review nit 6).
            result = ComponentOperationResult.Fail(UiExceptionGuard.Report(ex, "optiscaler-advanced-setting"));
            shown = result.Message;
        }
        finally
        {
            IsEnabled = true;
            _dialog.EndApplying();
        }
        _dialog.Message = shown;
        if (result.Success)
        {
            _accepted = requested;
            return;
        }
        _reverting = true;
        try { SelectedIndex = _accepted; }
        finally { _reverting = false; }
    }
}

/// <summary>
/// The OptiScaler ⚙ dialog (epic #89: was built in the window's code-behind): channel (locked while installed, each
/// channel explained in a 2 s tooltip, #174), nightly build (#156), DLL name (#172), and the OptiScaler.ini settings RHI
/// manages, written at once. The main window's overlay Cancel is behind this modal dialog: a setting change that is
/// still downloading or waiting can be stopped with the dialog's own Cancel (hidden otherwise, and once it starts
/// changing files). The #187 presets row is the shell-launch lane's control, hosted by the view: it reports to
/// <see cref="Message"/> and asks for the dialog to reopen after an apply (<see cref="ReopenRequested"/>).
/// </summary>
public sealed partial class OptiScalerSettingsDialogViewModel : ObservableObject, IDisposable
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    private readonly MainWindowViewModel _shell;
    private bool _applying;

    /// <summary>The channel's plain-words line, as the tooltip of each channel in the list (#174).</summary>
    public static readonly IValueConverter ChannelTip = new FuncValueConverter<object?, string?>(item
        => item is OptionItem<OptiScalerVariant> option ? RuntimePanelViewModel.OptiScalerChannelText(option.Value) : null);

    internal OptiScalerSettingsDialogViewModel(MainWindowViewModel shell, OptiScalerAdvancedSnapshot snapshot,
        Func<string, string, Task<ComponentOperationResult>>? setSetting = null)
    {
        _shell = shell;
        Snapshot = snapshot;
        SetSetting = setSetting ?? ((id, value) => shell.SetOptiScalerAdvancedSettingAsync(id, value));
        Operations = shell.Operations;
        Operations.PropertyChanged += OnOperationsChanged;
        Runtime = shell.Runtime;
        WindowTitle = TF("Win.OptiScalerAdvancedTitle", shell.SelectedGame?.Name ?? T("Win.Game"));
        CanChooseVariant = !snapshot.ManagedByRhi;
        selectedVariant = Runtime.SelectedOptiScalerVariant;
        VariantNote = snapshot.ManagedByRhi
            ? T("Win.InstalledVariantSelectorStaysLockedUntil")
            : T("Win.SelectedVariantSavedPerGameWill");
        NightlyBuild = Runtime.OptiScalerNightlyBuild;
        if (shell.OptiScalerDllName() is { } dllName)
        {
            HasDllName = true;
            DllOptions = dllName.Options;
            _acceptedDll = selectedDll = dllName.Selected;
            CanChooseDll = dllName.CanChoose;
            dllHint = dllName.Hint;
            _installedDll = snapshot.ManagedByRhi;
        }
        // #166 (w6/fixes): the warning is shown once — above the settings, or as the one line in their place.
        var (noticeWarning, noticeMessage) = OptiScalerSettingsNotice.For(snapshot);
        Warning = noticeWarning;
        IniPath = string.IsNullOrWhiteSpace(snapshot.IniPath) ? null : snapshot.IniPath;
        HasSettings = noticeMessage is null;
        if (!HasSettings)
            message = noticeMessage;
        else
        {
            var items = new List<OptiScalerIniItem>();
            string? lastGroup = null;
            foreach (var setting in snapshot.Settings)
            {
                if (!string.Equals(lastGroup, setting.Group, StringComparison.Ordinal))
                {
                    if (lastGroup is not null) items.Add(new OptiScalerIniDivider());
                    items.Add(new OptiScalerIniGroup(UiLocalizationService.Present(setting.Group)));
                    lastGroup = setting.Group;
                }
                items.Add(new OptiScalerIniSetting(this, setting));
            }
            IniItems = items;
            // #187: the four global presets (upstream shows them for the Nightly, DLSS NR and F5 channels).
            if (OptiScalerPresetsPresenter.AppliesTo(snapshot)) Presets = shell.OptiScalerPresets;
        }
    }

    public string WindowTitle { get; }
    public OptiScalerAdvancedSnapshot Snapshot { get; }
    public OperationCoordinator Operations { get; }
    public RuntimePanelViewModel Runtime { get; }
    internal Func<string, string, Task<ComponentOperationResult>> SetSetting { get; }

    // Channel (#174): every channel named in the label, each explained in plain words.
    public bool CanChooseVariant { get; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ChannelText))] private OptionItem<OptiScalerVariant>? selectedVariant;
    public string ChannelText => RuntimePanelViewModel.OptiScalerChannelText(SelectedVariant?.Value ?? OptiScalerVariant.Stable);
    public string VariantNote { get; }

    partial void OnSelectedVariantChanged(OptionItem<OptiScalerVariant>? value)
    {
        if (value is not null) Runtime.SelectedOptiScalerVariant = value;
    }

    /// <summary>The nightly-build row's presenter (#156; the row is hosted by the view).</summary>
    public OptiScalerNightlyBuildPresenter NightlyBuild { get; }

    // DLL name (#172): Automatic or a free name; locked while installed; the hint says which name the install takes.
    private DllNameOption? _acceptedDll;
    private bool _revertingDll;
    public bool HasDllName { get; }
    public IReadOnlyList<DllNameOption> DllOptions { get; } = [];
    public bool CanChooseDll { get; }
    [ObservableProperty] private DllNameOption? selectedDll;
    [ObservableProperty] private string? dllHint;

    /// <summary>The latest in-place rename the dropdown started (#196; test seam).</summary>
    internal Task DllChanged { get; private set; } = Task.CompletedTask;

    partial void OnSelectedDllChanged(DllNameOption? value)
    {
        if (_revertingDll || value is null || ReferenceEquals(value, _acceptedDll)) return;
        // #196: an installed OptiScaler is renamed in place; a refused or failed rename shows the name it keeps again.
        if (_installedDll)
        {
            DllChanged = RenameDllAsync(value);
            RHI.Desktop.Diagnostics.UiExceptionGuard.Forget(DllChanged, "OptiScaler DLL rename");
            return;
        }
        if (_shell.SaveOptiScalerDllName(value.Value))
        {
            _acceptedDll = value;
            if (_shell.OptiScalerDllName() is { } refreshed) DllHint = refreshed.Hint;
            return;
        }
        _revertingDll = true;
        try { SelectedDll = _acceptedDll; }
        finally { _revertingDll = false; }
    }

    private bool _installedDll;

    private async Task RenameDllAsync(DllNameOption value)
    {
        if (await _shell.RenameOptiScalerDllNameAsync(value.Value).ConfigureAwait(true))
        {
            _acceptedDll = value;
            if (_shell.OptiScalerDllName() is { } refreshed) DllHint = refreshed.Hint;
            return;
        }
        _revertingDll = true;
        try { SelectedDll = _acceptedDll; }
        finally { _revertingDll = false; }
    }

    // OptiScaler.ini
    public string? Warning { get; }
    public bool HasWarning => Warning is not null;
    public bool HasSettings { get; }
    public IReadOnlyList<OptiScalerIniItem> IniItems { get; } = [];
    public string? IniPath { get; }
    public bool HasIniPath => IniPath is not null;

    /// <summary>The #187 presets (hosted by the view), or null when the channel has none.</summary>
    public OptiScalerPresetsPresenter? Presets { get; }

    /// <summary>Why there is nothing to change, or the last change's result.</summary>
    [ObservableProperty] private string? message;

    /// <summary>The dialog's own Cancel: while a setting change can still stop (downloading or waiting).</summary>
    public bool ShowCancel => _applying && Operations.CanCancel;

    internal void BeginApplying() { _applying = true; OnPropertyChanged(nameof(ShowCancel)); }

    internal void EndApplying() { _applying = false; OnPropertyChanged(nameof(ShowCancel)); }

    /// <summary>A preset was applied: the dialog reopens so every dropdown shows the applied values.</summary>
    public event Action? ReopenRequested;

    internal void RequestReopen() => ReopenRequested?.Invoke();

    private void OnOperationsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OperationCoordinator.CanCancel)) OnPropertyChanged(nameof(ShowCancel));
    }

    public void Dispose() => Operations.PropertyChanged -= OnOperationsChanged;
}
