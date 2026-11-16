using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.Views;

/// <summary>
/// #277: HSF's row in Extras (upstream: Extras › MFG Unlocks): one AXAML element (<c>&lt;views:HsfRow/&gt;</c>), all state
/// from <see cref="HsfPresenter"/>. Install / Check / Update / Remove, the ⚙ and, on RTX 20/30/40, Universal MFG Unlocker.
/// </summary>
public sealed class HsfRow : ContentControl
{
    private static string T(string key) => UiLocalizationService.Text(key);

    private readonly TextBlock _status = new() { Foreground = new SolidColorBrush(Color.Parse("#5ECB7D")), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _warning = new() { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _launchpad = new() { Name = "HsfLaunchpad", Content = "iMMERSE Launchpad", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _install = new() { Name = "HsfInstall", Classes = { "rhi" }, Content = T("Vm.Install"), Padding = new Thickness(12, 6) };
    private readonly Button _check = new() { Name = "HsfCheck", Classes = { "rhi" }, Content = T("Ui.CheckUpdate"), Padding = new Thickness(12, 6) };
    private readonly Button _update = new() { Name = "HsfUpdate", Classes = { "rhi" }, Content = T("Vm.Update"), Padding = new Thickness(12, 6) };
    private readonly Button _gear = new() { Name = "HsfSettings", Classes = { "rhi" }, Content = "⚙", Padding = new Thickness(10, 6) };
    private readonly Button _remove = new() { Name = "HsfRemove", Classes = { "rhi", "remove" }, Content = T("Vm.Remove"), Padding = new Thickness(12, 6) };
    private readonly Button _mfg = new() { Name = "HsfUniversalMfg", Classes = { "rhi" }, Content = T("Hsf.InstallMfg"), Padding = new Thickness(12, 6) };
    private MainWindowViewModel? _vm;

    public HsfRow()
    {
        IsVisible = false;
        var name = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = "HSF", Foreground = new SolidColorBrush(Color.Parse("#E8ECF2")), FontWeight = FontWeight.SemiBold });
        name.Children.Add(new TextBlock { Text = T("Hsf.Subtitle"), Classes = { "muted" }, FontSize = 10 });
        ToolTip.SetTip(name, T("Hsf.Tip"));
        ToolTip.SetShowDelay(name, MiddleEllipsisText.TooltipDelay);
        ToolTip.SetTip(_launchpad, T("Hsf.LaunchpadTip"));
        ToolTip.SetTip(_mfg, T("Hsf.InstallMfgTip"));
        ToolTip.SetTip(_gear, T("Hsf.SettingsTip"));
        var buttons = new WrapPanel { ItemSpacing = 8, LineSpacing = 6 };
        foreach (var control in new Control[] { _launchpad, _install, _check, _update, _gear, _mfg, _remove }) buttons.Children.Add(control);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*,Auto"), ColumnSpacing = 10 };
        grid.Children.Add(name);
        grid.Children.Add(_status);
        Grid.SetColumn(_status, 1);
        grid.Children.Add(buttons);
        Grid.SetColumn(buttons, 2);
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.Parse("#2A3038")), Margin = new Thickness(0, 4) });
        panel.Children.Add(grid);
        panel.Children.Add(_warning);
        Content = panel;

        _install.Click += (_, _) => Run(p => p.InstallAsync(_vm!, _launchpad.IsChecked == true), "HsfInstall");
        _check.Click += (_, _) => Run(p => p.CheckAsync(_vm!), "HsfCheck");
        _update.Click += (_, _) => Run(p => p.UpdateAsync(_vm!), "HsfUpdate");
        _remove.Click += (_, _) => Run(p => p.RemoveAsync(_vm!), "HsfRemove");
        _mfg.Click += (_, _) => Run(p => p.InstallUniversalMfgAsync(_vm!), "HsfUniversalMfg");
        _gear.Click += (_, _) => Run(async p =>
        {
            var moved = await p.PrepareGearAsync(_vm!);
            if (TopLevel.GetTopLevel(this) is Window owner && p.StateFor(_vm!) is { Settings: { } settings })
                await HsfSettingsDialog.Create(_vm!, p, settings, moved).ShowDialog(owner);
        }, "HsfSettings");
    }

    private void Run(Func<HsfPresenter, Task> action, string context)
    {
        if (_vm?.Parity?.Hsf is { } presenter) UiExceptionGuard.Forget(action(presenter), context);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as MainWindowViewModel ?? (DataContext as PanelViewModel)?.Shell;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        Sync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_vm is not null) { _vm.PropertyChanged -= OnVmChanged; _vm.PropertyChanged += OnVmChanged; }
        Sync();
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.SelectedGame) or nameof(MainWindowViewModel.IsBusy)
            or nameof(MainWindowViewModel.IsSelectionProbeBusy) or nameof(MainWindowViewModel.StatusText))
            Sync();
    }

    private void Sync()
    {
        var state = _vm?.Parity?.Hsf?.StateFor(_vm);
        IsVisible = state?.Visible == true;
        if (state is null || !state.Visible) return;
        _status.Text = state.Status;
        _warning.Text = state.Warning ?? "";
        _warning.IsVisible = state.Warning is not null;
        _launchpad.IsVisible = _install.IsVisible = !state.Installed;
        _check.IsVisible = _gear.IsVisible = _remove.IsVisible = state.Installed;
        _update.IsVisible = state.Installed && state.UpdateAvailable;
        _mfg.IsVisible = state.Installed && state.OffersUniversalMfg;
        foreach (var control in new Control[] { _launchpad, _install, _check, _update, _gear, _mfg, _remove }) control.IsEnabled = state.CanAct;
    }
}

/// <summary>#277: HSF's ⚙ — installed version, DirectX 12 mode (HSF.ini's D3D12 route) and iMMERSE Launchpad on/off.</summary>
internal static class HsfSettingsDialog
{
    private static string T(string key) => UiLocalizationService.Text(key);

    private static readonly (HsfRoute Route, string Key)[] Routes =
        [(HsfRoute.None, "Hsf.RouteNone"), (HsfRoute.GameStreamline, "Hsf.RouteGame"), (HsfRoute.BringIn, "Hsf.RouteBringIn")];

    public static Window Create(MainWindowViewModel vm, HsfPresenter presenter, HsfState settings, string? note)
    {
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "HSF", FontSize = 18, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = UiLocalizationService.Format("Hsf.Installed", settings.Version ?? "?"), TextWrapping = TextWrapping.Wrap });
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.82, Text = note ?? "", IsVisible = note is not null };
        void Show() { result.Text = vm.StatusText; result.IsVisible = true; }

        panel.Children.Add(new TextBlock { Text = T("Hsf.RouteLabel"), FontWeight = FontWeight.SemiBold });
        var route = new ComboBox { Name = "HsfRoute", ItemsSource = Routes.Select(r => T(r.Key)).ToArray(), MinWidth = 260,
            SelectedIndex = Array.FindIndex(Routes, r => r.Route == settings.Route) };
        ToolTip.SetTip(route, T("Hsf.RouteTip"));
        if (settings.RouteSwitchedOff) panel.Children.Add(new TextBlock { Text = T("Hsf.RouteOff"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap });
        var launchpad = new CheckBox { Name = "HsfLaunchpadSetting", Content = "iMMERSE Launchpad", IsChecked = settings.LaunchpadOn };
        ToolTip.SetTip(launchpad, T("Hsf.LaunchpadTip"));
        var ready = false;
        route.SelectionChanged += async (_, _) => await UiExceptionGuard.RunAsync(async () =>
        {
            if (!ready || route.SelectedIndex < 0) return;
            await presenter.SetRouteAsync(vm, Routes[route.SelectedIndex].Route);
            Show();
        }, nameof(HsfSettingsDialog));
        launchpad.IsCheckedChanged += async (_, _) => await UiExceptionGuard.RunAsync(async () =>
        {
            if (!ready) return;
            await presenter.SetLaunchpadAsync(vm, launchpad.IsChecked == true);
            Show();
        }, nameof(HsfSettingsDialog));
        ready = true;
        panel.Children.Add(route);
        panel.Children.Add(new TextBlock { Text = T("Hsf.LaunchpadLabel"), FontWeight = FontWeight.SemiBold });
        panel.Children.Add(launchpad);
        panel.Children.Add(new TextBlock { Text = T("Hsf.GearNote"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(result);
        return new Window
        {
            Title = $"HSF — {vm.SelectedGame?.Name}",
            Width = 560, Height = 440, MinWidth = 420, MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = panel }
        };
    }
}
