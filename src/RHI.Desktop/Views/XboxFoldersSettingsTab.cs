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
/// Settings → "Xbox / Game Pass folders" (w3/scan #90, owner decision C; hosted at integration): the folders the Xbox
/// detector searches besides the installer app's default state folder and <c>~/Games</c>. Built in code like the other Settings
/// tabs from <see cref="XboxFoldersPresenter"/> only (description with Xodus' link, list, Add folder…, Remove); a refused save shows on the dialog's
/// status line. Each path is one line with a middle ellipsis and the full path in a 2 s tooltip. Hidden when the view
/// model has no folder list (a window built over a bare view model).
/// </summary>
internal static class XboxFoldersSettingsTab
{
    public static TabItem Create(XboxFoldersPresenter? presenter, TextBlock status, Func<string, Task<string?>> pickFolder)
    {
        var tab = new TabItem { Header = XboxFoldersPresenter.Label, IsVisible = presenter is not null, Name = "XboxFoldersTab" };
        if (presenter is null) return tab;
        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = XboxFoldersPresenter.Label, FontSize = 20, FontWeight = FontWeight.SemiBold });
        // Shown on every system, a clean one included: what the tab is for, with Xodus' page one click away.
        panel.Children.Add(new TextBlock { Text = XboxFoldersPresenter.Intro, TextWrapping = TextWrapping.Wrap, Name = "XboxFoldersIntro" });
        var xodus = new Button { Content = XboxFoldersPresenter.XodusLinkText, Padding = new Thickness(12, 6), Name = "XboxFoldersXodusLink" };
        ToolTip.SetTip(xodus, XboxFoldersPresenter.XodusUrl);
        ToolTip.SetShowDelay(xodus, MiddleEllipsisText.TooltipDelay);
        xodus.Click += (_, _) =>
        {
            if (!RHI.Platform.Linux.Paths.LinuxDesktopOpener.TryOpenUri(XboxFoldersPresenter.XodusUrl, out var error))
                status.Text = ErrorLineText.LinkNotOpened(XboxFoldersPresenter.XodusUrl, error);
        };
        panel.Children.Add(xodus);

        var list = new StackPanel { Spacing = 6, Name = "XboxFoldersList" };
        void Fill()
        {
            list.Children.Clear();
            foreach (var folder in presenter.Folders)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
                var path = new MiddleEllipsisText { FullText = folder, WrapNonPath = false, VerticalAlignment = VerticalAlignment.Center };
                ToolTip.SetTip(path, folder);
                ToolTip.SetShowDelay(path, MiddleEllipsisText.TooltipDelay);
                var remove = new Button { Content = XboxFoldersPresenter.RemoveText, Padding = new Thickness(12, 6) };
                remove.Click += (_, _) => { if (presenter.Remove(folder) is { } refused) status.Text = refused; };
                row.Children.Add(path);
                row.Children.Add(remove);
                Grid.SetColumn(remove, 1);
                list.Children.Add(row);
            }
        }
        NotifyCollectionChangedEventHandler changed = (_, _) => Fill();
        list.AttachedToVisualTree += (_, _) => { presenter.Folders.CollectionChanged -= changed; presenter.Folders.CollectionChanged += changed; Fill(); };
        list.DetachedFromVisualTree += (_, _) => presenter.Folders.CollectionChanged -= changed;
        Fill();
        panel.Children.Add(list);

        var add = new Button { Content = XboxFoldersPresenter.AddText, Padding = new Thickness(12, 6), Name = "XboxFoldersAdd" };
        ToolTip.SetTip(add, XboxFoldersPresenter.ToolTip);
        ToolTip.SetShowDelay(add, MiddleEllipsisText.TooltipDelay);
        add.Click += (_, _) => UiExceptionGuard.Forget(AddAsync(), "xbox-folders-add");
        panel.Children.Add(add);
        tab.Content = new ScrollViewer { Content = panel };
        return tab;

        async Task AddAsync()
        {
            var chosen = await pickFolder(XboxFoldersPresenter.AddText);
            if (string.IsNullOrWhiteSpace(chosen)) return;
            if (presenter.Add(chosen) is { } refused) status.Text = refused; // already localised (TryPersist)
        }
    }
}
