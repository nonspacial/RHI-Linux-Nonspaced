using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// Settings → "Removed games" (#293, upstream 2.9.0 Settings › Tools &amp; Maintenance › Troubleshooting › Removed games):
/// every game removed from RHI with Restore. Built in code like the other added tabs; the logic is the view model's.
/// </summary>
internal static class RemovedGamesSettingsTab
{
    public static TabItem Create(MainWindowViewModel vm, Action<string> show)
    {
        var tab = new TabItem { Header = UiLocalizationService.Text("Removed.Tab"), Name = "RemovedGamesTab" };
        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = UiLocalizationService.Text("Removed.Tab"), FontSize = 20, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = UiLocalizationService.Text("Removed.Intro"), TextWrapping = TextWrapping.Wrap });
        var list = new StackPanel { Spacing = 6, Name = "RemovedGamesList" };
        void Fill()
        {
            list.Children.Clear();
            if (vm.RemovedGames.Count == 0)
                list.Children.Add(new TextBlock { Text = UiLocalizationService.Text("Removed.None"), Opacity = 0.7 });
            foreach (var row in vm.RemovedGames.ToArray())
            {
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
                var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = row.Name, FontWeight = FontWeight.SemiBold });
                text.Children.Add(new TextBlock { Text = row.RemovedText, FontSize = 11, Opacity = 0.7 });
                var restore = new Button { Content = UiLocalizationService.Text("Removed.Restore"), Padding = new Thickness(12, 6) };
                restore.Click += (_, _) => UiExceptionGuard.Forget(RestoreAsync(row.Key), "removed-games-restore");
                grid.Children.Add(text);
                grid.Children.Add(restore);
                Grid.SetColumn(restore, 1);
                list.Children.Add(grid);
            }
        }
        async Task RestoreAsync(string key) => show(await vm.RestoreRemovedGameAsync(key));
        NotifyCollectionChangedEventHandler changed = (_, _) => Fill();
        list.AttachedToVisualTree += (_, _) => { vm.RemovedGames.CollectionChanged -= changed; vm.RemovedGames.CollectionChanged += changed; vm.LoadRemovedGames(); Fill(); };
        list.DetachedFromVisualTree += (_, _) => vm.RemovedGames.CollectionChanged -= changed;
        vm.LoadRemovedGames();
        Fill();
        panel.Children.Add(list);
        tab.Content = new ScrollViewer { Content = panel };
        return tab;
    }
}
