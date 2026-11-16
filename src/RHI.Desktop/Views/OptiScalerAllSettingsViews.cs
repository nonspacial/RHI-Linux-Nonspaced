using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// #272 the OptiScaler ⚙'s All Settings section and its Save ini / Load saved ini rows (upstream 2.9.3). Built in code like
/// the ⚙'s other lane rows; every decision is the presenters'.
/// </summary>
internal static class OptiScalerAllSettingsViews
{
    private static string T(string key) => UiLocalizationService.Text(key);

    public static Control AllSettings(OptiScalerAllSettingsPresenter presenter)
    {
        var panel = new StackPanel { Spacing = 10, Name = "OptiScalerAllSettings" };
        panel.Children.Add(new TextBlock { Text = T("Osa.Title"), FontSize = 14, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = T("Osa.Intro"), TextWrapping = TextWrapping.Wrap, Opacity = 0.72 });
        var toggle = ParitySettingsTab.Button(presenter.ToggleText);
        toggle.Name = "OptiScalerAllSettingsToggle";
        toggle.HorizontalAlignment = HorizontalAlignment.Left;
        toggle.Click += (_, _) => presenter.Toggle();
        panel.Children.Add(toggle);

        var host = new StackPanel { Spacing = 10, IsVisible = false };
        var sections = new ComboBox { ItemsSource = presenter.Sections, MinWidth = 200, Name = "OptiScalerAllSettingsSection" };
        ToolTip.SetTip(sections, T("Osa.SectionTip"));
        sections.SelectionChanged += (_, _) => { if (sections.SelectedItem is string section) presenter.SelectedSection = section; };
        var search = new TextBox { PlaceholderText = T("Osa.Search"), Name = "OptiScalerAllSettingsSearch" };
        DispatcherTimer? debounce = null;
        search.TextChanged += (_, _) =>
        {
            debounce?.Stop();
            debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            debounce.Tick += (_, _) => { debounce?.Stop(); presenter.SearchText = search.Text ?? ""; };
            debounce.Start();
        };
        var filters = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12 };
        filters.Children.Add(sections);
        filters.Children.Add(search);
        Grid.SetColumn(search, 1);
        host.Children.Add(filters);
        var note = new TextBlock { FontSize = 11, Opacity = 0.6 };
        host.Children.Add(note);
        var rows = new StackPanel { Spacing = 8 };
        host.Children.Add(rows);
        var info = new TextBlock { FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap };
        host.Children.Add(info);

        var apply = ParitySettingsTab.Button(presenter.ApplyText, T("Osa.ApplyTip"));
        apply.Name = "OptiScalerAllSettingsApply";
        apply.Click += (_, _) => UiExceptionGuard.Forget(presenter.ApplyAsync(), "optiscaler-all-settings");
        var revert = ParitySettingsTab.Button(T("Osa.Revert"), T("Osa.RevertTip"));
        revert.Click += (_, _) => presenter.Revert();
        var forget = ParitySettingsTab.Button(T("Osa.Forget"), T("Osa.ForgetTip"));
        forget.Click += (_, _) => presenter.ForgetSavedEdits();
        var backups = ParitySettingsTab.Button(T("Osa.Backups"), T("Osa.BackupsTip"));
        backups.Click += (_, _) => presenter.OpenBackups();
        host.Children.Add(ParitySettingsTab.Buttons(apply, revert, forget, backups));
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.78 };
        host.Children.Add(message);
        panel.Children.Add(host);

        void FillRows()
        {
            rows.Children.Clear();
            foreach (var row in presenter.Rows) rows.Children.Add(Row(row));
        }
        void Sync()
        {
            toggle.Content = presenter.ToggleText;
            host.IsVisible = presenter.IsOpen;
            if (!ReferenceEquals(sections.SelectedItem, presenter.SelectedSection)) sections.SelectedItem = presenter.SelectedSection;
            sections.IsEnabled = presenter.CanChooseSection;
            note.Text = presenter.Note ?? "";
            note.IsVisible = presenter.Note is not null;
            info.Text = presenter.InfoText;
            apply.Content = presenter.ApplyText;
            apply.IsEnabled = revert.IsEnabled = presenter.CanApply;
            forget.IsEnabled = presenter.HasSavedEdits;
            message.Text = presenter.Message;
        }
        NotifyCollectionChangedEventHandler changed = (_, _) => FillRows();
        presenter.Rows.CollectionChanged += changed;
        panel.DetachedFromVisualTree += (_, _) => presenter.Rows.CollectionChanged -= changed;
        panel.AttachedToVisualTree += (_, _) => { presenter.Rows.CollectionChanged -= changed; presenter.Rows.CollectionChanged += changed; FillRows(); };
        ParitySettingsTab.Bind(panel, presenter, Sync);
        FillRows();
        Sync();
        return panel;
    }

    private static Control Row(OptiScalerAllSettingRow row)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("280,*"), ColumnSpacing = 12 };
        var label = new TextBlock { Text = row.Label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        ToolTip.SetTip(label, row.Help);
        ToolTip.SetShowDelay(label, MiddleEllipsisText.TooltipDelay);
        grid.Children.Add(label);
        Control editor;
        if (row.IsChoice)
        {
            var combo = new ComboBox { ItemsSource = row.Options, SelectedIndex = row.SelectedIndex, MinWidth = 260 };
            combo.SelectionChanged += (_, _) => row.SelectedIndex = combo.SelectedIndex;
            editor = combo;
        }
        else
        {
            var box = new TextBox { Text = row.Text, PlaceholderText = "auto", MinWidth = 260 };
            box.TextChanged += (_, _) => row.Text = box.Text ?? "";
            editor = box;
        }
        ToolTip.SetTip(editor, row.Help);
        ToolTip.SetShowDelay(editor, MiddleEllipsisText.TooltipDelay);
        grid.Children.Add(editor);
        Grid.SetColumn(editor, 1);
        return grid;
    }

    /// <summary>"Save ini…" and "Load saved ini…" (each opens its own small panel, as upstream's flyouts).</summary>
    public static Control IniLibrary(OptiScalerIniLibraryPresenter presenter)
    {
        var panel = new StackPanel { Spacing = 8, Name = "OptiScalerIniLibrary" };
        var saveToggle = ParitySettingsTab.Button(T("Osi.SaveIni"), T("Osi.SaveIniTip"));
        var loadToggle = ParitySettingsTab.Button(T("Osi.LoadIni"), T("Osi.LoadIniTip"));
        panel.Children.Add(ParitySettingsTab.Buttons(saveToggle, loadToggle));

        var savePanel = new StackPanel { Spacing = 6, IsVisible = false };
        savePanel.Children.Add(new TextBlock { Text = T("Osi.SaveAs") });
        var name = new TextBox { Text = presenter.SaveName, PlaceholderText = T("Osi.NamePlaceholder"), Name = "OptiScalerSaveIniName" };
        name.TextChanged += (_, _) => presenter.SaveName = name.Text ?? "";
        var save = ParitySettingsTab.Button(T("Win.Save"));
        save.Click += (_, _) => presenter.Save();
        var nameRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        nameRow.Children.Add(name);
        nameRow.Children.Add(save);
        Grid.SetColumn(save, 1);
        savePanel.Children.Add(nameRow);
        var saveNote = new TextBlock { FontSize = 11, Opacity = 0.6 };
        savePanel.Children.Add(saveNote);
        panel.Children.Add(savePanel);

        var loadPanel = new StackPanel { Spacing = 6, IsVisible = false };
        loadPanel.Children.Add(new TextBlock { Text = T("Osi.LoadIntro"), TextWrapping = TextWrapping.Wrap, Opacity = 0.72 });
        var list = new StackPanel { Spacing = 6 };
        loadPanel.Children.Add(list);
        var add = ParitySettingsTab.Button(T("Osi.AddFromFile"), T("Osi.AddFromFileTip"));
        add.Click += (_, _) => UiExceptionGuard.Forget(AddFromFileAsync(), "optiscaler-saved-ini");
        var folder = ParitySettingsTab.Button(T("Osi.OpenFolder"), T("Osi.OpenFolderTip"));
        folder.Click += (_, _) => presenter.OpenFolder();
        loadPanel.Children.Add(ParitySettingsTab.Buttons(add, folder));
        panel.Children.Add(loadPanel);
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(message);

        saveToggle.Click += (_, _) => { savePanel.IsVisible = !savePanel.IsVisible; loadPanel.IsVisible = false; };
        loadToggle.Click += (_, _) => { loadPanel.IsVisible = !loadPanel.IsVisible; savePanel.IsVisible = false; };

        async Task AddFromFileAsync()
        {
            if (TopLevel.GetTopLevel(panel) is not { } top) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = T("Osi.AddFromFile"),
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("INI") { Patterns = ["*.ini"] }, FilePickerFileTypes.All]
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) presenter.AddFromFile(path);
        }
        void FillList()
        {
            list.Children.Clear();
            if (presenter.Saved.Count == 0)
            {
                list.Children.Add(new TextBlock { Text = T("Osi.None"), Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
                return;
            }
            foreach (var saved in presenter.Saved.ToArray())
            {
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
                var current = string.Equals(saved, presenter.Current, StringComparison.OrdinalIgnoreCase);
                grid.Children.Add(new TextBlock
                {
                    Text = current ? UiLocalizationService.Format("Osi.InUse", saved) : saved,
                    VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
                });
                var use = ParitySettingsTab.Button(T("Osi.Use"));
                use.Click += (_, _) => UiExceptionGuard.Forget(presenter.UseAsync(saved), "optiscaler-saved-ini");
                var delete = ParitySettingsTab.Button(T("Vm.Remove"));
                var armed = false;
                delete.Click += (_, _) =>
                {
                    if (!armed) { armed = true; delete.Content = T("Osi.ClickAgain"); return; }
                    presenter.Delete(saved);
                };
                var buttons = ParitySettingsTab.Buttons(use, delete);
                grid.Children.Add(buttons);
                Grid.SetColumn(buttons, 1);
                list.Children.Add(grid);
            }
        }
        void Sync()
        {
            saveNote.Text = presenter.SaveNote;
            saveNote.IsVisible = presenter.SaveNote.Length > 0;
            message.Text = presenter.Message;
            message.Foreground = presenter.MessageIsError ? Brushes.IndianRed : null;
            message.IsVisible = presenter.Message.Length > 0;
        }
        NotifyCollectionChangedEventHandler changed = (_, _) => FillList();
        presenter.Saved.CollectionChanged += changed;
        panel.DetachedFromVisualTree += (_, _) => presenter.Saved.CollectionChanged -= changed;
        panel.AttachedToVisualTree += (_, _) => { presenter.Saved.CollectionChanged -= changed; presenter.Saved.CollectionChanged += changed; FillList(); };
        ParitySettingsTab.Bind(panel, presenter, Sync);
        FillList();
        Sync();
        return panel;
    }
}
