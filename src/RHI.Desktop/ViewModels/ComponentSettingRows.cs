using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Paths;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// w14/extras-fg: one setting of a frame-generation mod's ⚙ (#279 MFG Unlock, #281 20/30 FG Unlock, #280 RTX Encore): a
/// dropdown, or a number box when <see cref="IsNumber"/>. A change is applied at once through <see cref="Apply"/> (it
/// writes the mod's own file); a refused change goes back to the value before and the dialog shows why.
/// <see cref="EnabledWhen"/> greys a row out while another setting makes it meaningless (upstream's dependencies).
/// </summary>
public sealed partial class ComponentSettingRow : ObservableObject
{
    private bool _reverting;
    private int _applied;
    private string _appliedText = "";

    public ComponentSettingRow(string label, string? tip, IReadOnlyList<string> options, int selected)
    {
        Label = label;
        Tip = tip;
        Options = options;
        _applied = selected;
        selectedIndex = selected;
    }

    /// <summary>A number box (upstream's NumberBox rows); <paramref name="min"/>–<paramref name="max"/> is checked before Apply.</summary>
    public ComponentSettingRow(string label, string? tip, int value, int min, int max)
        : this(label, tip, [], 0)
    {
        IsNumber = true;
        Minimum = min;
        Maximum = max;
        _appliedText = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        numberText = _appliedText;
    }

    public string Label { get; }
    public string? Tip { get; }
    public IReadOnlyList<string> Options { get; }
    public bool IsNumber { get; }
    public bool IsChoice => !IsNumber;
    public int Minimum { get; }
    public int Maximum { get; }

    /// <summary>Writes the value (the option index, or the number); returns whether it was saved and the line to show.</summary>
    public Func<int, (bool Saved, string Message)>? Apply { get; init; }

    /// <summary>Whether the row can be changed now (with <see cref="Available"/>); null = always.</summary>
    public Func<bool>? EnabledWhen { get; init; }

    /// <summary>The dialog-wide switch (the mod is installed, its file can be written).</summary>
    public bool Available { get; init; } = true;

    /// <summary>Called after a saved change (the dialog shows the message and re-evaluates every row's enabled state).</summary>
    internal Action<string>? Changed { get; set; }

    [ObservableProperty] private int selectedIndex;
    [ObservableProperty] private string numberText = "";
    [ObservableProperty] private bool isEnabled = true;

    public void Refresh() => IsEnabled = Available && (EnabledWhen?.Invoke() ?? true);

    /// <summary>Sets the shown value without applying it (another row's change moved it, as upstream's Engine/Max Frames).</summary>
    public void Show(int index)
    {
        _reverting = true;
        try { SelectedIndex = _applied = index; }
        finally { _reverting = false; }
    }

    partial void OnSelectedIndexChanged(int value)
    {
        if (_reverting || IsNumber || value < 0 || value == _applied || Apply is null) return;
        var (saved, message) = Apply(value);
        if (saved) _applied = value;
        else Show(_applied);
        Changed?.Invoke(message);
    }

    partial void OnNumberTextChanged(string value)
    {
        if (_reverting || !IsNumber || Apply is null || value == _appliedText) return;
        if (!int.TryParse(value.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number)
            || number < Minimum || number > Maximum)
        {
            _reverting = true;
            try { NumberText = _appliedText; }
            finally { _reverting = false; }
            Changed?.Invoke(UiLocalizationService.Format("Fg.NumberRange", Label, Minimum, Maximum));
            return;
        }
        var (saved, message) = Apply(number);
        _reverting = true;
        try { NumberText = _appliedText = saved ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : _appliedText; }
        finally { _reverting = false; }
        Changed?.Invoke(message);
    }
}

/// <summary>
/// A section of the ⚙'s settings list (upstream's section titles): one card. #297: <paramref name="Compact"/> lays the rows
/// out two per row with the explanation as the tooltip (upstream <c>AddCompactGrid</c>, e.g. RTX Encore); otherwise each row
/// has its name left, the option right and the explanation under it.
/// </summary>
public sealed record ComponentSettingSection(string Title, IReadOnlyList<ComponentSettingRow> Rows, bool Compact = false)
{
    public bool HasTitle => Title.Length > 0;
    public bool IsListed => !Compact;
}

/// <summary>w14/extras-fg: an "Open &lt;file&gt;" button of a ⚙ (the mod's ini, log or jsonc), disabled while the file is missing.</summary>
public sealed partial class ComponentFileButton : ObservableObject
{
    private readonly Action<string> _show;

    public ComponentFileButton(string label, string path, Action<string> show)
    {
        Label = label;
        Path = path;
        _show = show;
        Refresh();
    }

    public string Label { get; }
    public string Path { get; }

    /// <summary>Test seam: opens the file (desktop default application).</summary>
    internal static Func<string, (bool Opened, string? Error)> Open { get; set; } = path => (LinuxDesktopOpener.TryOpenFile(path, out var error), error);

    [ObservableProperty] private bool exists;

    public void Refresh() => Exists = File.Exists(Path);

    [RelayCommand]
    private void OpenFile()
    {
        Refresh();
        if (!Exists)
        {
            _show(UiLocalizationService.Format("Fg.FileNotFound", System.IO.Path.GetFileName(Path)));
            return;
        }
        var (opened, error) = Open(Path);
        if (!opened) _show(UiLocalizationService.Present(error));
    }
}
