using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// #211: the per-game NVIDIA settings in the DLSS section (one AXAML element, DataContext the
/// <see cref="NvidiaSettingsPresenter"/>), laid out like the section's component rows: the label, a muted line saying
/// what it does, and the control in the version column; below the section's minimum the control moves under its label
/// (<see cref="ResponsiveRow"/>). Rows for a DLSS feature the game does not ship are hidden; the whole block is hidden
/// where the settings cannot apply. Multi frame generation (RTX 50 only) opens a small dialog, as upstream's MFG dialog.
/// Provenance ("set through Proton's NVIDIA layer") is only in the ~2 s tooltips.
/// </summary>
public sealed class NvidiaSettingsBlock : ContentControl
{
    private static string T(string key) => UiLocalizationService.Text(key);

    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#6B7A8E"));
    private static readonly IBrush Title = new SolidColorBrush(Color.Parse("#E8ECF2"));

    private readonly Row _srPreset, _srScale, _rrPreset, _rrScale, _fgPreset, _mfg;
    private readonly ComboBox _srPresetBox = Combo("NvidiaSrPreset");
    private readonly ComboBox _srScaleBox = Combo("NvidiaSrScale");
    private readonly NumericUpDown _srCustom = Number("NvidiaSrCustomScale", 33, 100);
    private readonly ComboBox _rrPresetBox = Combo("NvidiaRrPreset");
    private readonly ComboBox _rrScaleBox = Combo("NvidiaRrScale");
    private readonly NumericUpDown _rrCustom = Number("NvidiaRrCustomScale", 33, 100);
    private readonly ComboBox _fgPresetBox = Combo("NvidiaFgPreset");
    private readonly Button _mfgButton = new() { Name = "NvidiaFrameGeneration", Classes = { "rhi" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly CheckBox _newest = new() { Name = "NvidiaNewestDlss" };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Muted };
    private readonly Button _copy = new() { Name = "NvidiaCopyLaunchOptions", Classes = { "rhi" }, Padding = new Thickness(10, 5), HorizontalAlignment = HorizontalAlignment.Left };
    private NvidiaSettingsPresenter? _vm;
    private bool _syncing;

    public NvidiaSettingsBlock()
    {
        _srPreset = new Row(_srPresetBox);
        _srScale = new Row(Stack(_srScaleBox, _srCustom));
        _rrPreset = new Row(_rrPresetBox);
        _rrScale = new Row(Stack(_rrScaleBox, _rrCustom));
        _fgPreset = new Row(_fgPresetBox);
        _mfg = new Row(_mfgButton);

        Wire(_srPresetBox, item => _vm!.SelectedSuperResolutionPreset = item);
        Wire(_srScaleBox, item => _vm!.SelectedSuperResolutionScale = item);
        Wire(_rrPresetBox, item => _vm!.SelectedRayReconstructionPreset = item);
        Wire(_rrScaleBox, item => _vm!.SelectedRayReconstructionScale = item);
        Wire(_fgPresetBox, item => _vm!.SelectedFrameGenerationPreset = item);
        _srCustom.ValueChanged += (_, e) => { if (!_syncing && _vm is not null) _vm.SuperResolutionCustomScale = e.NewValue; };
        _rrCustom.ValueChanged += (_, e) => { if (!_syncing && _vm is not null) _vm.RayReconstructionCustomScale = e.NewValue; };
        _newest.IsCheckedChanged += (_, _) => { if (!_syncing && _vm is not null) _vm.NewestDlss = _newest.IsChecked == true; };
        _mfgButton.Click += (_, _) => UiExceptionGuard.Forget(OpenFrameGenerationAsync(), nameof(NvidiaSettingsBlock));
        _copy.Click += (_, _) => UiExceptionGuard.Forget(CopyAsync(), nameof(NvidiaSettingsBlock));
        Tip(_newest, "Nv.NewestDlssTip");
        Tip(_mfgButton, "Nv.FrameGenerationTip");

        var panel = new StackPanel { Spacing = 7 };
        panel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.Parse("#1E4858")), Margin = new Thickness(0, 2, 0, 3) });
        foreach (var row in new[] { _srPreset, _srScale, _rrPreset, _rrScale, _fgPreset, _mfg }) panel.Children.Add(row.Panel);
        panel.Children.Add(_newest);
        var footer = new StackPanel { Spacing = 5 };
        footer.Children.Add(_hint);
        footer.Children.Add(_copy);
        panel.Children.Add(footer);
        Content = panel;
        Relabel();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as NvidiaSettingsPresenter;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        Sync();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_vm is null) return;
        _vm.PropertyChanged -= OnVmChanged;
        _vm.PropertyChanged += OnVmChanged;
        Sync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e) => Sync();

    private void Sync()
    {
        _syncing = true;
        try
        {
            var vm = _vm;
            IsVisible = vm?.IsShown == true;
            if (vm is null) return;
            Relabel();
            Set(_srPresetBox, vm.SuperResolutionPresetOptions, vm.SelectedSuperResolutionPreset);
            Set(_srScaleBox, vm.SuperResolutionScaleOptions, vm.SelectedSuperResolutionScale);
            Set(_rrPresetBox, vm.RayReconstructionPresetOptions, vm.SelectedRayReconstructionPreset);
            Set(_rrScaleBox, vm.RayReconstructionScaleOptions, vm.SelectedRayReconstructionScale);
            Set(_fgPresetBox, vm.FrameGenerationPresetOptions, vm.SelectedFrameGenerationPreset);
            _srCustom.IsVisible = vm.ShowSuperResolutionCustomScale;
            _srCustom.Value = vm.SuperResolutionCustomScale;
            _rrCustom.IsVisible = vm.ShowRayReconstructionCustomScale;
            _rrCustom.Value = vm.RayReconstructionCustomScale;
            _srPreset.Panel.IsVisible = _srScale.Panel.IsVisible = vm.ShowSuperResolution;
            _rrPreset.Panel.IsVisible = _rrScale.Panel.IsVisible = vm.ShowRayReconstruction;
            _fgPreset.Panel.IsVisible = vm.ShowFrameGenerationPreset;
            _mfg.Panel.IsVisible = vm.ShowFrameGeneration;
            _mfg.Line.Text = vm.FrameGenerationSummary;
            _newest.IsChecked = vm.NewestDlss;
            foreach (var control in new Control[] { _srPresetBox, _srScaleBox, _srCustom, _rrPresetBox, _rrScaleBox, _rrCustom, _fgPresetBox, _mfgButton, _newest })
                control.IsEnabled = vm.CanEdit;
            _hint.Text = vm.Hint;
            _hint.IsVisible = vm.Hint.Length > 0;
            _copy.IsVisible = vm.ShowLaunchOptions;
        }
        finally { _syncing = false; }
    }

    /// <summary>Labels and tooltips in the current UI language (a language switch raises the presenter's notifications).</summary>
    private void Relabel()
    {
        _srPreset.Set(T("Nv.SrPreset"), T("Nv.SrPresetLine"), "Nv.SrPresetTip");
        _srScale.Set(T("Nv.SrScale"), T("Nv.ScaleLine"), "Nv.ScaleTip");
        _rrPreset.Set(T("Nv.RrPreset"), T("Nv.RrPresetLine"), "Nv.RrPresetTip");
        _rrScale.Set(T("Nv.RrScale"), T("Nv.RrScaleLine"), "Nv.ScaleTip");
        _fgPreset.Set(T("Nv.FgPreset"), T("Nv.FgPresetLine"), "Nv.FgPresetTip");
        _mfg.Set(T("Nv.MultiFrameGeneration"), _vm?.FrameGenerationSummary ?? "", "Nv.FrameGenerationTip");
        _mfgButton.Content = T("Nv.FrameGenerationButton");
        _newest.Content = T("Nv.NewestDlss");
        _copy.Content = T("Nv.CopyLaunchOptions");
        Tip(_srCustom, "Nv.CustomScaleTip");
        Tip(_rrCustom, "Nv.CustomScaleTip");
        Tip(_newest, "Nv.NewestDlssTip");
        Tip(_mfgButton, "Nv.FrameGenerationTip");
        Tip(_srPresetBox, "Nv.SrPresetTip");
        Tip(_srScaleBox, "Nv.ScaleTip");
        Tip(_rrPresetBox, "Nv.RrPresetTip");
        Tip(_rrScaleBox, "Nv.ScaleTip");
        Tip(_fgPresetBox, "Nv.FgPresetTip");
    }

    private async Task OpenFrameGenerationAsync()
    {
        if (_vm is null) return;
        var dialog = NvidiaFrameGenerationDialog.Create(_vm);
        if (TopLevel.GetTopLevel(this) is MainWindow main) await main.ShowNvidiaDialogAsync(dialog);
        else if (TopLevel.GetTopLevel(this) is Window owner) await dialog.ShowDialog(owner);
    }

    private async Task CopyAsync()
    {
        if (_vm is null) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            _vm.ReportCopy(null);
            return;
        }
        await clipboard.SetTextAsync(_vm.LaunchOptions);
        _vm.ReportCopy(_vm.LaunchOptions);
    }

    private void Wire(ComboBox box, Action<OptionItem<uint>> set)
        => box.SelectionChanged += (_, _) =>
        {
            if (_syncing || _vm is null || box.SelectedItem is not OptionItem<uint> item) return;
            set(item);
        };

    private static void Set(ComboBox box, object items, object selected)
    {
        if (!ReferenceEquals(box.ItemsSource, items)) box.ItemsSource = (System.Collections.IEnumerable)items;
        box.SelectedItem = selected;
    }

    internal static ComboBox Combo(string name) => new()
    {
        Name = name,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        MaxDropDownHeight = 300,
        ItemTemplate = new FuncDataTemplate<OptionItem>((item, _) => new TextBlock { Text = item?.Display })
    };

    internal static NumericUpDown Number(string name, int min, int max) => new()
    {
        Name = name,
        Minimum = min,
        Maximum = max,
        Increment = 1,
        FormatString = "0",
        HorizontalAlignment = HorizontalAlignment.Stretch,
        IsVisible = false
    };

    /// <summary>The control's ~2 s tooltip: what it does, then where it comes from (provenance only in tooltips).</summary>
    internal static void Tip(Control control, string key)
    {
        ToolTip.SetTip(control, T(key) + "\n" + T("Nv.TitleTip"));
        ToolTip.SetShowDelay(control, MiddleEllipsisText.TooltipDelay);
    }

    private static StackPanel Stack(params Control[] controls)
    {
        var stack = new StackPanel { Spacing = 4 };
        foreach (var control in controls) stack.Children.Add(control);
        return stack;
    }

    /// <summary>One setting: label | what it does | control, in the DLSS rows' grid (the control wraps under them when narrow).</summary>
    private sealed class Row
    {
        public Row(Control control)
        {
            Label = new TextBlock { Foreground = Title, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            Line = new TextBlock { Foreground = Muted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(Line, 1);
            Grid.SetColumn(control, 2);
            control.VerticalAlignment = VerticalAlignment.Center;
            Panel = new ResponsiveRow { Columns = "170,*,200", ColumnSpacing = 8, LineSpacing = 6, HeadColumns = 2, MinStarWidth = 160 };
            Panel.Children.Add(Label);
            Panel.Children.Add(Line);
            Panel.Children.Add(control);
        }

        public ResponsiveRow Panel { get; }
        public TextBlock Label { get; }
        public TextBlock Line { get; }

        public void Set(string label, string line, string tipKey)
        {
            Label.Text = label;
            Line.Text = line;
            Tip(Label, tipKey);
        }
    }
}

/// <summary>
/// #211: the small multi frame generation dialog (upstream's MFG dialog): Mode (Default / Fixed / Dynamic), the frame
/// count for that mode and, in Dynamic mode, the target frame rate with a Custom 20–1000 box. Every change is saved at
/// once (as upstream); Close only closes. Shown only on an RTX 50 GPU with a frame-generation file.
/// </summary>
public static class NvidiaFrameGenerationDialog
{
    private static string T(string key) => UiLocalizationService.Text(key);

    public static Window Create(NvidiaSettingsPresenter vm)
    {
        var mode = NvidiaSettingsBlock.Combo("NvidiaFgMode");
        var fixedCount = NvidiaSettingsBlock.Combo("NvidiaFixedFrameCount");
        var dynamicCount = NvidiaSettingsBlock.Combo("NvidiaDynamicFrameCount");
        var target = NvidiaSettingsBlock.Combo("NvidiaTargetFrameRate");
        var custom = NvidiaSettingsBlock.Number("NvidiaCustomTargetFrameRate", 20, 1000);
        NvidiaSettingsBlock.Tip(mode, "Nv.FgModeTip");
        NvidiaSettingsBlock.Tip(fixedCount, "Nv.FrameCountTip");
        NvidiaSettingsBlock.Tip(dynamicCount, "Nv.FrameCountTip");
        NvidiaSettingsBlock.Tip(target, "Nv.TargetFrameRateTip");
        NvidiaSettingsBlock.Tip(custom, "Nv.CustomFpsTip");
        mode.ItemsSource = vm.FrameGenerationModeOptions;
        fixedCount.ItemsSource = vm.FixedFrameCountOptions;
        dynamicCount.ItemsSource = vm.DynamicFrameCountOptions;
        target.ItemsSource = vm.TargetFrameRateOptions;

        var countLabel = Label("Nv.FrameCount");
        var targetLabel = Label("Nv.TargetFrameRate");
        var rows = new LabelValuePanel { ColumnSpacing = 16, RowSpacing = 10, StackBelow = 380 };
        void Add(int row, Control label, Control value)
        {
            Grid.SetRow(label, row);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            rows.Children.Add(label);
            rows.Children.Add(value);
        }
        var counts = new Panel();
        counts.Children.Add(fixedCount);
        counts.Children.Add(dynamicCount);
        var targets = new StackPanel { Spacing = 4 };
        targets.Children.Add(target);
        targets.Children.Add(custom);
        Add(0, Label("Nv.FgMode"), mode);
        Add(1, countLabel, counts);
        Add(2, targetLabel, targets);

        var syncing = false;
        void Sync()
        {
            syncing = true;
            try
            {
                mode.SelectedItem = vm.SelectedFrameGenerationMode;
                fixedCount.SelectedItem = vm.SelectedFixedFrameCount;
                dynamicCount.SelectedItem = vm.SelectedDynamicFrameCount;
                target.SelectedItem = vm.SelectedTargetFrameRate;
                custom.Value = vm.CustomTargetFrameRate;
                fixedCount.IsVisible = vm.ShowFixedFrameCount;
                dynamicCount.IsVisible = vm.ShowDynamicFrameCount;
                countLabel.IsVisible = counts.IsVisible = vm.ShowFixedFrameCount || vm.ShowDynamicFrameCount;
                targetLabel.IsVisible = targets.IsVisible = vm.ShowTargetFrameRate;
                custom.IsVisible = vm.ShowCustomTargetFrameRate;
                foreach (var control in new Control[] { mode, fixedCount, dynamicCount, target, custom }) control.IsEnabled = vm.CanEdit;
            }
            finally { syncing = false; }
        }
        mode.SelectionChanged += (_, _) => { if (!syncing && mode.SelectedItem is OptionItem<string> item) vm.SelectedFrameGenerationMode = item; };
        fixedCount.SelectionChanged += (_, _) => { if (!syncing && fixedCount.SelectedItem is OptionItem<uint> item) vm.SelectedFixedFrameCount = item; };
        dynamicCount.SelectionChanged += (_, _) => { if (!syncing && dynamicCount.SelectedItem is OptionItem<uint> item) vm.SelectedDynamicFrameCount = item; };
        target.SelectionChanged += (_, _) => { if (!syncing && target.SelectedItem is OptionItem<uint> item) vm.SelectedTargetFrameRate = item; };
        custom.ValueChanged += (_, e) => { if (!syncing) vm.CustomTargetFrameRate = e.NewValue; };
        PropertyChangedEventHandler changed = (_, _) => Sync();
        vm.PropertyChanged += changed;
        Sync();

        var close = new Button { Classes = { "rhi" }, Content = T("Win.Close"), HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(14, 6) };
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = T("Nv.MultiFrameGeneration"), FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = T("Nv.FgDialogIntro"), TextWrapping = TextWrapping.Wrap, Opacity = 0.76 });
        panel.Children.Add(rows);
        panel.Children.Add(close);

        var window = new Window
        {
            Title = T("Nv.MultiFrameGeneration"),
            Width = 520,
            Height = 340,
            MinWidth = 360,
            MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }
        };
        close.Click += (_, _) => window.Close();
        window.Closed += (_, _) => vm.PropertyChanged -= changed;
        return window;
    }

    private static TextBlock Label(string key) => new() { Text = T(key), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
}
