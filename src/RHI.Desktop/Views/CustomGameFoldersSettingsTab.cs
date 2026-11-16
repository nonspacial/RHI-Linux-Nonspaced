using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Desktop.Views.Dialogs;

namespace RHI.Desktop.Views;

/// <summary>
/// Settings → "Custom game folders" (#293, upstream 2.9.0 Settings › Tools &amp; Maintenance › Custom game folders): the
/// folders with Remove, then Add folder, Scan now, Review N new games (while games wait) and Forget ignored games (N).
/// Built in code like the other added tabs, beside Removed games until phase 2 restyles Settings; the logic is the view
/// model's. Scan now opens the review straight away when it finds games, as upstream.
/// </summary>
internal static class CustomGameFoldersSettingsTab
{
    public static TabItem Create(MainWindowViewModel vm, Action<string> show, Func<string, Task<string?>> pickFolder)
    {
        static string T(string key) => UiLocalizationService.Text(key);
        var tab = new TabItem { Header = T("CustomFolders.Tab"), Name = "CustomGameFoldersTab" };
        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = T("CustomFolders.Tab"), FontSize = 20, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = T("CustomFolders.Intro"), TextWrapping = TextWrapping.Wrap });
        var list = new StackPanel { Spacing = 6, Name = "CustomGameFoldersList" };
        panel.Children.Add(list);

        Button MakeButton(string text, string tip, string name)
        {
            var button = new Button { Content = text, Padding = new Thickness(12, 6), Name = name };
            ToolTip.SetTip(button, tip);
            ToolTip.SetShowDelay(button, MiddleEllipsisText.TooltipDelay);
            return button;
        }
        var add = MakeButton(T("CustomFolders.Add"), T("CustomFolders.AddTip"), "CustomGameFoldersAdd");
        var scan = MakeButton(T("CustomFolders.ScanNow"), T("CustomFolders.ScanNowTip"), "CustomGameFoldersScan");
        var review = MakeButton("", T("CustomFolders.ReviewTip"), "CustomGameFoldersReview");
        var forget = MakeButton("", T("CustomFolders.ForgetTip"), "CustomGameFoldersForget");
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
        foreach (var button in new[] { add, scan, review, forget }) buttons.Children.Add(button);
        panel.Children.Add(buttons);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8, IsVisible = false, Name = "CustomGameFoldersStatus" };
        panel.Children.Add(status);

        void Say(string text)
        {
            status.Text = text;
            status.IsVisible = !string.IsNullOrEmpty(text);
            show(text);
        }

        void Fill()
        {
            list.Children.Clear();
            if (vm.CustomGameFolders.Count == 0)
                list.Children.Add(new TextBlock { Text = T("CustomFolders.None"), Opacity = 0.7 });
            foreach (var folder in vm.CustomGameFolders.ToArray())
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
                var path = new MiddleEllipsisText { FullText = folder, WrapNonPath = false, VerticalAlignment = VerticalAlignment.Center };
                ToolTip.SetTip(path, folder);
                ToolTip.SetShowDelay(path, MiddleEllipsisText.TooltipDelay);
                var remove = MakeButton(T("Vm.Remove"), T("CustomFolders.RemoveTip"), "CustomGameFolderRemove");
                remove.Click += (_, _) => Say(vm.RemoveCustomGameFolder(folder));
                row.Children.Add(path);
                row.Children.Add(remove);
                Grid.SetColumn(remove, 1);
                list.Children.Add(row);
            }
            var pending = vm.PendingCustomGames.Count;
            review.Content = pending == 1 ? T("CustomFolders.ReviewOne") : UiLocalizationService.Format("CustomFolders.ReviewMany", pending);
            review.IsVisible = pending > 0;
            forget.Content = UiLocalizationService.Format("CustomFolders.Forget", vm.IgnoredCustomGameCount);
            forget.IsVisible = vm.IgnoredCustomGameCount > 0;
            scan.IsEnabled = vm.CustomGameFolders.Count > 0;
        }

        async Task ReviewAsync()
        {
            if (vm.CreateCustomGamesReview(pickFolder) is not { } model || TopLevel.GetTopLevel(tab) is not Window owner) return;
            await CustomGamesReviewDialogView.CreateWindow(model).ShowDialog(owner);
            Fill();
        }

        async Task ScanAsync(Func<Task<string>> run)
        {
            scan.IsEnabled = false;
            scan.Content = T("CustomFolders.Scanning");
            string line;
            try { line = await run(); }
            finally { scan.Content = T("CustomFolders.ScanNow"); Fill(); }
            Say(line);
            // The user asked for this scan, so the review opens straight away.
            if (vm.PendingCustomGames.Count > 0) await ReviewAsync();
        }

        add.Click += (_, _) => UiExceptionGuard.Forget(AddAsync(), "custom-folders-add");
        scan.Click += (_, _) => UiExceptionGuard.Forget(ScanAsync(vm.ScanCustomFoldersNowAsync), "custom-folders-scan-now");
        review.Click += (_, _) => UiExceptionGuard.Forget(ReviewAsync(), "custom-folders-review");
        forget.Click += (_, _) => Say(vm.ForgetIgnoredCustomGames());

        Action changed = () => Fill();
        NotifyCollectionChangedEventHandler folders = (_, _) => Fill();
        list.AttachedToVisualTree += (_, _) =>
        {
            vm.CustomGameFoldersChanged -= changed;
            vm.CustomGameFoldersChanged += changed;
            vm.CustomGameFolders.CollectionChanged -= folders;
            vm.CustomGameFolders.CollectionChanged += folders;
            vm.LoadCustomGameFolders();
            Fill();
        };
        list.DetachedFromVisualTree += (_, _) =>
        {
            vm.CustomGameFoldersChanged -= changed;
            vm.CustomGameFolders.CollectionChanged -= folders;
        };
        vm.LoadCustomGameFolders();
        Fill();
        tab.Content = new ScrollViewer { Content = panel };
        return tab;

        async Task AddAsync()
        {
            var chosen = await pickFolder(T("CustomFolders.Add"));
            if (string.IsNullOrWhiteSpace(chosen)) return;
            if (vm.AddCustomGameFolder(chosen) is { } refused)
            {
                Say(refused);
                return;
            }
            Say(UiLocalizationService.Format("CustomFolders.Added", RHI.Platform.Linux.Detection.CustomFolders.CustomFolderScanService.NormalizePath(chosen)));
            await ScanAsync(vm.ScanCustomFoldersNowAsync);
        }
    }
}
