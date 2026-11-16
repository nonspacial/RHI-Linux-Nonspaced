using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// #271 Settings › Custom OptiScaler builds (upstream 2.9.2 <c>SettingsHandler.OsCustomBuilds</c>, the card at the end of
/// Components): the builds with DLSS NR DLL / Replace / Rename / Remove, the link box with Add link, Add file, Add folder,
/// and drag and drop onto the card. Built in code like the other added Settings rows; every decision is the presenter's.
/// </summary>
internal sealed class OptiScalerCustomBuildsSettingsRows : IOptiScalerCustomBuildPrompts
{
    private readonly OptiScalerCustomBuildsPresenter _presenter;
    private readonly StackPanel _list = new() { Spacing = 8, Name = "OptiScalerCustomBuildsList" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false, Name = "OptiScalerCustomBuildsStatus" };

    public Control Control { get; }

    public OptiScalerCustomBuildsSettingsRows(OptiScalerCustomBuildsPresenter presenter)
    {
        _presenter = presenter;
        presenter.Prompts = this;
        var body = new StackPanel { Spacing = 10, Margin = new Thickness(0, 12, 0, 0) };
        body.Children.Add(new TextBlock { Text = T("Osc.Title"), FontSize = 16, FontWeight = FontWeight.SemiBold });
        body.Children.Add(new TextBlock { Text = T("Osc.Description"), TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        body.Children.Add(new TextBlock { Text = T("Osc.TrustWarning"), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Orange, Name = "OptiScalerCustomBuildsTrust" });
        body.Children.Add(_list);

        var link = new TextBox { PlaceholderText = T("Osc.LinkPlaceholder"), Name = "OptiScalerCustomBuildLink" };
        link.TextChanged += (_, _) => presenter.LinkText = link.Text ?? "";
        var addLink = ParitySettingsTab.Button(T("Osc.AddLink"), T("Osc.AddLinkTip"));
        addLink.Name = "OptiScalerCustomBuildAddLink";
        addLink.Click += (_, _) => Run(presenter.AddLinkAsync());
        link.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Run(presenter.AddLinkAsync());
        };
        var linkRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        linkRow.Children.Add(link);
        linkRow.Children.Add(addLink);
        Grid.SetColumn(addLink, 1);
        body.Children.Add(linkRow);

        var addFile = ParitySettingsTab.Button(T("Osc.AddFile"), T("Osc.AddFileTip"));
        addFile.Click += (_, _) => Run(presenter.AddFileAsync());
        var addFolder = ParitySettingsTab.Button(T("Osc.AddFolder"), T("Osc.AddFolderTip"));
        addFolder.Click += (_, _) => Run(presenter.AddFolderAsync());
        body.Children.Add(ParitySettingsTab.Buttons(addFile, addFolder));
        body.Children.Add(new TextBlock { Text = T("Osc.DropHint"), Opacity = 0.6, FontSize = 11 });
        body.Children.Add(_status);

        var card = new Border { Child = body, Background = Brushes.Transparent, Name = "OptiScalerCustomBuildsCard" };
        DragDrop.SetAllowDrop(card, true);
        card.AddHandler(DragDrop.DragOverEvent, (_, e) =>
            e.DragEffects = e.DataTransfer.Formats.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);
        card.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (e.DataTransfer.TryGetFiles() is not { } files) return;
            var paths = files.Select(file => file.Path.LocalPath).ToArray();
            Run(DropAsync(paths));
        });
        Control = card;

        presenter.RowsChanged += Fill;
        ParitySettingsTab.Bind(card, presenter, () =>
        {
            _status.Text = presenter.Status;
            _status.IsVisible = presenter.Status.Length > 0;
            _status.Foreground = presenter.StatusIsError ? Brushes.IndianRed : null;
            if (link.Text != presenter.LinkText) link.Text = presenter.LinkText;
            addLink.IsEnabled = addFile.IsEnabled = addFolder.IsEnabled = !presenter.IsBusy;
        });
        card.AttachedToVisualTree += (_, _) => Fill();
        card.DetachedFromVisualTree += (_, _) => presenter.RowsChanged -= Fill;
        Fill();
    }

    private async Task DropAsync(IReadOnlyList<string> paths)
    {
        foreach (var path in paths) await _presenter.AddPathAsync(path);
    }

    private void Fill()
    {
        _list.Children.Clear();
        var rows = _presenter.Rows;
        if (rows.Count == 0)
        {
            _list.Children.Add(new TextBlock { Text = T("Osc.None"), Opacity = 0.7 });
            return;
        }
        foreach (var row in rows) _list.Children.Add(Row(row));
    }

    private Control Row(OptiScalerCustomBuildRow row)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = row.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var facts = new TextBlock { Text = row.Facts, FontSize = 11, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(facts, row.Tip);
        text.Children.Add(facts);
        grid.Children.Add(text);

        var nr = new CheckBox { Content = T("Osc.NrDll"), IsChecked = row.NeedsNrDll, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(nr, T("Osc.NrDllTip"));
        ToolTip.SetShowDelay(nr, MiddleEllipsisText.TooltipDelay);
        nr.Click += (_, _) => _presenter.SetNeedsNrDll(row.Build, nr.IsChecked == true);

        var replace = ParitySettingsTab.Button(T("Osp.FreshIniConfirm"), T("Osc.ReplaceTip"));
        var fromLink = new MenuItem { Header = T("Osc.ReplaceFromLink") };
        fromLink.Click += (_, _) => Run(_presenter.ReplaceFromLinkAsync(row.Build));
        var fromFile = new MenuItem { Header = T("Osc.ReplaceFromFile") };
        fromFile.Click += (_, _) => Run(_presenter.ReplaceFromFileAsync(row.Build));
        var fromFolder = new MenuItem { Header = T("Osc.ReplaceFromFolder") };
        fromFolder.Click += (_, _) => Run(_presenter.ReplaceFromFolderAsync(row.Build));
        replace.Flyout = new MenuFlyout { ItemsSource = new[] { fromLink, fromFile, fromFolder } };

        var rename = ParitySettingsTab.Button(T("Parity.ShaderProfileRename"), T("Osc.RenameTip"));
        rename.Click += (_, _) => Run(_presenter.RenameAsync(row.Build));
        var remove = ParitySettingsTab.Button(T("Vm.Remove"), T("Osc.RemoveTip"));
        remove.Click += (_, _) => Run(_presenter.RemoveAsync(row.Build));

        var actions = ParitySettingsTab.Buttons(nr, replace, rename, remove);
        actions.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(actions);
        Grid.SetColumn(actions, 1);
        return grid;
    }

    private static void Run(Task task) => UiExceptionGuard.Forget(task, "optiscaler-custom-builds");

    private Window? Owner => TopLevel.GetTopLevel(Control) as Window;

    public async Task<string?> AskNameAsync(string title, string suggested)
    {
        if (Owner is not { } owner) return suggested;
        var box = new TextBox { Text = suggested, Name = "OptiScalerCustomBuildName" };
        var ok = new Button { Content = T("Win.Save"), Padding = new Thickness(14, 7), IsDefault = true };
        var cancel = new Button { Content = T("Win.Cancel"), Padding = new Thickness(14, 7), IsCancel = true };
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, ItemSpacing = 8, LineSpacing = 8 };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(18) };
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Title = title, Width = 440, MinWidth = 280, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel
        };
        ok.Click += (_, _) => dialog.Close(string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim());
        cancel.Click += (_, _) => dialog.Close(null);
        dialog.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        return await dialog.ShowDialog<string?>(owner);
    }

    public Task<bool> ConfirmAsync(string title, string message, string accept)
        => Owner is { } owner ? ParityDialogs.AskAsync(owner, title, message, accept) : Task.FromResult(false);

    public async Task<string?> PickFileAsync()
    {
        if (Owner is not { } owner) return null;
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("Osc.FilePickerTitle"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(T("Osc.FilePickerFilter")) { Patterns = ["*.zip", "*.7z", "*.rar", "*.dll"] }, FilePickerFileTypes.All]
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<string?> PickFolderAsync()
    {
        if (Owner is not { } owner) return null;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = T("Osc.AddFolder"), AllowMultiple = false });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    private static string T(string key) => UiLocalizationService.Text(key);
}
