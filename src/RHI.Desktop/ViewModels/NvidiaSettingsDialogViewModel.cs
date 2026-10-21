using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RHI.Desktop.Localization;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// #211 (owner layout B): the one NVIDIA settings dialog the DLSS section's "NVIDIA settings…" button opens. Every
/// control binds the selected game's <see cref="NvidiaSettingsPresenter"/> (each change is saved at once, as upstream);
/// Close only closes. The layout is <c>Views/Dialogs/NvidiaSettingsDialogView.axaml</c>. Tooltip texts are read once at
/// open, like the template's <c>{l:T}</c> labels; provenance is only in them.
/// </summary>
public sealed partial class NvidiaSettingsDialogViewModel : ObservableObject
{
    /// <summary>The window's width; its height follows the rows shown (capped to the main window, then it scrolls).</summary>
    public const double Width = 560;
    public const double MinWidth = 360;
    public const double MinHeight = 320;

    public NvidiaSettingsDialogViewModel(NvidiaSettingsPresenter settings)
    {
        Settings = settings;
    }

    public NvidiaSettingsPresenter Settings { get; }

    public string WindowTitle => T("Nv.DialogTitle");

    public string SrPresetTip { get; } = Tip("Nv.SrPresetTip");
    public string ScaleTip { get; } = Tip("Nv.ScaleTip");
    public string CustomScaleTip { get; } = Tip("Nv.CustomScaleTip");
    public string RrPresetTip { get; } = Tip("Nv.RrPresetTip");
    public string FgPresetTip { get; } = Tip("Nv.FgPresetTip");
    public string FgModeTip { get; } = Tip("Nv.FgModeTip");
    public string FrameCountTip { get; } = Tip("Nv.FrameCountTip");
    public string TargetFrameRateTip { get; } = Tip("Nv.TargetFrameRateTip");
    public string CustomFpsTip { get; } = Tip("Nv.CustomFpsTip");
    public string NewestDlssTip { get; } = Tip("Nv.NewestDlssTip");

    /// <summary>The footer's line after Copy (empty until then).</summary>
    [ObservableProperty] private string status = "";

    public event Action? CloseRequested;

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    /// <summary>The Copy button's result: <paramref name="copied"/> null = this session has no clipboard.</summary>
    internal void ReportCopy(string? copied)
    {
        Settings.ReportCopy(copied);
        Status = copied is null ? T("Win.ClipboardUnavailableSession") : T("Nv.Copied");
    }

    private static string T(string key) => UiLocalizationService.Text(key);

    /// <summary>What the control does, then where it comes from (provenance only in the ~2 s tooltip).</summary>
    private static string Tip(string key) => T(key) + "\n" + T("Nv.TitleTip");
}
