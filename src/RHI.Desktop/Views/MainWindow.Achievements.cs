using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Achievements;

namespace RHI.Desktop.Views;

/// <summary>
/// w14/layouts-b (#307, upstream 2.9.2/2.9.3 DetailLayoutHost.Achievements): Spotlight's Steam achievements — the strip under
/// Launch (progress, latest, recent unlocks with their icons) and the side panel's list (unlock dates, rarity, icons,
/// search, Show hidden). Steam's files are read off the UI thread; while Spotlight shows a Steam game they are checked every
/// few seconds and the strip and list refresh when Steam writes a new unlock (upstream's live refresh).
/// </summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan AchievementPoll = TimeSpan.FromSeconds(5);

    private DispatcherTimer? _achTimer;
    private AchievementSet? _achSet;
    private int? _achAppId;
    private DateTime? _achStamp;
    private int _achSeq;
    private Action? _achPanelRefresh;

    /// <summary>Loads (or clears) the selected game's achievements and shows the strip (from ApplySpotlightLayout / selection).</summary>
    private void RefreshAchievements(bool force = false)
    {
        var vm = _spotVm;
        var appId = _spotOn && vm is not null ? vm.Achievements.AppIdFor(vm.SelectedGame) : null;
        if (appId is null)
        {
            _achTimer?.Stop();
            _achAppId = null;
            _achSet = null;
            SetVisible("SpotlightAchievementsStrip", false);
            return;
        }
        _achTimer ??= NewAchievementTimer();
        _achTimer.Start();
        if (!force && appId == _achAppId) return;
        if (appId != _achAppId) { _achSet = null; SetVisible("SpotlightAchievementsStrip", false); }
        _achAppId = appId;
        var service = vm!.Achievements.Service!;
        var seq = ++_achSeq;
        var token = SpotlightLoads();
        _ = Task.Run(async () =>
        {
            var stamp = service.LastChange(appId.Value);
            var set = service.Load(appId.Value);
            Dispatcher.UIThread.Post(() => { if (seq == _achSeq) ShowAchievements(set, stamp); });
            if (set is null || set.HasRarity) return;
            var rarity = await service.GetRarityAsync(appId.Value, token).ConfigureAwait(false);
            if (rarity is null) return;
            var rated = SteamAchievementService.WithRarity(set, rarity);
            Dispatcher.UIThread.Post(() => { if (seq == _achSeq) ShowAchievements(rated, stamp); });
        }, CancellationToken.None);
    }

    private DispatcherTimer NewAchievementTimer()
    {
        var timer = new DispatcherTimer { Interval = AchievementPoll };
        timer.Tick += (_, _) =>
        {
            if (_achAppId is not int appId || _spotVm?.Achievements.Service is not { } service) return;
            _ = Task.Run(() =>
            {
                var stamp = service.LastChange(appId);
                if (stamp != _achStamp) Dispatcher.UIThread.Post(() => { if (_achAppId == appId) RefreshAchievements(force: true); });
            });
        };
        Closed += (_, _) => timer.Stop();
        return timer;
    }

    private void ShowAchievements(AchievementSet? set, DateTime? stamp)
    {
        _achSet = set;
        _achStamp = stamp;
        SetVisible("SpotlightAchievementsStrip", set is not null);
        _achPanelRefresh?.Invoke();
        if (set is null) return;
        var strip = AchievementsPresenter.Strip(set, DateTime.UtcNow);
        if (this.FindControl<TextBlock>("AchProgressText") is { } progress) progress.Text = strip.Progress;
        if (this.FindControl<ProgressBar>("AchProgressBar") is { } bar) bar.Value = strip.Percent;
        if (this.FindControl<TextBlock>("AchLatestText") is { } latest) latest.Text = strip.Latest ?? "";
        if (this.FindControl<StackPanel>("AchRecentIcons") is { } icons)
        {
            icons.Children.Clear();
            foreach (var a in strip.Recent)
            {
                var icon = AchievementIcon(set.AppId, a.Icon, 32);
                ToolTip.SetTip(icon, a.Name);
                icons.Children.Add(icon);
            }
        }
    }

    private Border AchievementIcon(int appId, string? file, double size)
    {
        var image = new Image { Stretch = Stretch.UniformToFill };
        var border = new Border { Classes = { "achIcon" }, Width = size, Height = size, Child = image };
        if (_spotVm?.Achievements.Service is { } service && file is not null)
        {
            var token = SpotlightLoads();
            _ = Task.Run(async () =>
            {
                var path = await service.GetIconAsync(appId, file, token).ConfigureAwait(false);
                var bitmap = path is null ? null : DecodeImage(path, (int)(size * 2));
                if (bitmap is not null) Dispatcher.UIThread.Post(() => image.Source = bitmap);
            }, CancellationToken.None);
        }
        return border;
    }

    private void AchievementsStrip_Click(object? sender, RoutedEventArgs e) => _spotVm?.Layout.OpenPanel(SpotlightPresenter.AchievementsKey);

    /// <summary>The side panel's full list: the strip's summary, search, Show hidden, one row per achievement.</summary>
    private Control BuildAchievementsPanelCore(MainWindowViewModel vm)
    {
        var root = new StackPanel { Name = "AchievementsPanel", Spacing = 12 };
        var summary = PanelText("", "spotStrong");
        var search = new TextBox { Name = "AchievementsSearch", PlaceholderText = T("Ach.Search"), MinWidth = 220 };
        var showHidden = new Avalonia.Controls.Primitives.ToggleButton
        {
            Name = "AchievementsShowHidden", Classes = { "filters" }, Content = T("Ach.ShowHidden"), IsChecked = vm.Achievements.ShowHidden,
        };
        ToolTip.SetTip(showHidden, T("Ach.ShowHiddenTip"));
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Children = { search, showHidden } };
        Grid.SetColumn(showHidden, 1);
        var list = new StackPanel { Name = "AchievementsList", Spacing = 6 };
        root.Children.Add(summary);
        root.Children.Add(bar);
        root.Children.Add(list);

        void Fill()
        {
            list.Children.Clear();
            if (_achSet is not { } set)
            {
                summary.Text = "";
                list.Children.Add(PanelText(T("Ach.None")));
                return;
            }
            var strip = AchievementsPresenter.Strip(set, DateTime.UtcNow);
            summary.Text = strip.Progress;
            var rows = AchievementsPresenter.Rows(set, vm.Achievements.ShowHidden, search.Text, DateTime.UtcNow);
            if (rows.Count == 0) list.Children.Add(PanelText(T("Ach.NoMatch")));
            foreach (var row in rows) list.Children.Add(AchievementRowView(set.AppId, row));
        }
        search.TextChanged += (_, _) => Fill();
        showHidden.IsCheckedChanged += (_, _) =>
        {
            if (vm.Achievements.SetShowHidden(showHidden.IsChecked == true) is { } refused) vm.StatusText = refused;
            Fill();
        };
        _achPanelRefresh = Fill;
        root.DetachedFromVisualTree += (_, _) => { if (_achPanelRefresh == Fill) _achPanelRefresh = null; };
        Fill();
        return root;
    }

    private Border AchievementRowView(int appId, AchievementRow row)
    {
        var icon = row.Masked ? new Border { Classes = { "achIcon" }, Width = 48, Height = 48, Child = new TextBlock { Text = "?", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Classes = { "spotMuted" } } }
            : AchievementIcon(appId, row.Unlocked ? row.Source.Icon : row.Source.IconGray ?? row.Source.Icon, 48);
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = row.Title, Classes = { "glanceTitle" }, TextWrapping = TextWrapping.Wrap });
        if (row.Description.Length > 0) text.Children.Add(new TextBlock { Text = row.Description, Classes = { "statusLine" }, TextWrapping = TextWrapping.Wrap });
        var side = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        if (row.When is { } when) side.Children.Add(new TextBlock { Text = when, Classes = { "statusLine" }, HorizontalAlignment = HorizontalAlignment.Right });
        if (row.Rarity is { } rarity) side.Children.Add(new TextBlock { Text = rarity, Classes = { "achRarity" }, HorizontalAlignment = HorizontalAlignment.Right });
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12, Children = { icon, text, side } };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(side, 2);
        var border = new Border { Classes = { "achRow" }, Child = grid };
        border.Classes.Set("locked", !row.Unlocked);
        return border;
    }
}
