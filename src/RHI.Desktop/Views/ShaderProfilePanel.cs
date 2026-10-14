using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>
/// The shader-profile panel at the top of the Shaders/Add-ons dialog's shader tab (#93). The dialog adds it with one
/// line, handing over how to read and set its pack ticks; the panel holds no deployment logic (Apply stays the
/// dialog's). Fits 640x480 and portrait: rows wrap, the name box stretches.
/// </summary>
internal static class ShaderProfilePanel
{
    private static string T(string key) => UiLocalizationService.Text(key);

    public static Control Create(ParityFeatures? parity, Func<IReadOnlyCollection<string>> ticked, Action<IReadOnlyCollection<string>> tick)
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 10), IsVisible = parity is not null, Name = "ShaderProfilePanel" };
        if (parity is null) return panel;
        var presenter = parity.ShaderProfiles;

        var profiles = new ComboBox { MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Stretch, Name = "ShaderProfileCombo",
            PlaceholderText = T("Parity.ShaderProfilePick") };
        var load = ParitySettingsTab.Button(T("Parity.ShaderProfileLoad"), T("Parity.ShaderProfileLoadTip"));
        var delete = ParitySettingsTab.Button(T("Parity.ShaderProfileDelete"));
        var name = new TextBox { PlaceholderText = T("Parity.ShaderProfileNamePlaceholder"), MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Stretch };
        var save = ParitySettingsTab.Button(T("Parity.ShaderProfileSave"), T("Parity.ShaderProfileSaveTip"));
        var rename = ParitySettingsTab.Button(T("Parity.ShaderProfileRename"), T("Parity.ShaderProfileRenameTip"));
        var export = ParitySettingsTab.Button(T("Parity.ShaderProfileExport"), T("Parity.ShaderProfileExportTip"));
        var import = ParitySettingsTab.Button(T("Parity.ShaderProfileImport"), T("Parity.ShaderProfileImportTip"));
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8, FontSize = 12 };

        string? Selected() => profiles.SelectedItem as string;

        void Refresh(string? select)
        {
            var names = presenter.Names;
            profiles.ItemsSource = names;
            profiles.SelectedItem = select is not null && names.Contains(select) ? select : null;
            Sync();
        }

        void Sync()
        {
            var has = Selected() is not null;
            load.IsVisible = delete.IsVisible = has;
            rename.IsVisible = has && !string.IsNullOrWhiteSpace(name.Text);
            profiles.IsVisible = presenter.Names.Count > 0;
        }

        profiles.SelectionChanged += (_, _) => Sync();
        name.TextChanged += (_, _) => Sync();
        load.Click += (_, _) =>
        {
            if (Selected() is not { } chosen) return;
            var (selected, line) = presenter.Load(chosen);
            if (selected is not null) tick(selected);
            status.Text = line;
        };
        delete.Click += (_, _) =>
        {
            if (Selected() is not { } chosen) return;
            status.Text = presenter.Delete(chosen);
            Refresh(null);
        };
        save.Click += (_, _) =>
        {
            var (line, saved) = presenter.Save(string.IsNullOrWhiteSpace(name.Text) ? Selected() : name.Text, ticked());
            status.Text = line;
            if (saved is not null) { name.Text = ""; Refresh(saved); }
        };
        rename.Click += (_, _) =>
        {
            if (Selected() is not { } chosen) return;
            var newName = name.Text?.Trim();
            status.Text = presenter.Rename(chosen, newName);
            name.Text = "";
            Refresh(presenter.Names.Contains(newName ?? "") ? newName : chosen);
        };
        export.Click += (_, _) => UiExceptionGuard.Forget(ExportAsync(), "ShaderProfileExport");
        import.Click += (_, _) => UiExceptionGuard.Forget(ImportAsync(), "ShaderProfileImport");

        async Task ExportAsync()
        {
            export.IsEnabled = false;
            status.Text = T("Parity.ShaderProfileExporting");
            try { status.Text = await presenter.ExportAsync(Selected(), ticked()); }
            finally { export.IsEnabled = true; }
        }

        async Task ImportAsync()
        {
            if (TopLevel.GetTopLevel(panel) is not { } top) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = T("Parity.ShaderProfileImportPick"),
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("zip") { Patterns = ["*.zip"] }]
            });
            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
            var (line, imported) = presenter.Import(path);
            status.Text = line;
            if (imported is not null) Refresh(imported);
        }

        var pickRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        pickRow.Children.Add(profiles);
        var pickButtons = ParitySettingsTab.Buttons(load, delete);
        pickRow.Children.Add(pickButtons);
        Grid.SetColumn(pickButtons, 1);
        var nameRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        nameRow.Children.Add(name);
        var nameButtons = ParitySettingsTab.Buttons(save, rename);
        nameRow.Children.Add(nameButtons);
        Grid.SetColumn(nameButtons, 1);

        panel.Children.Add(new TextBlock { Text = T("Parity.ShaderProfilesHeading"), FontWeight = FontWeight.SemiBold });
        panel.Children.Add(ParitySettingsTab.Note(T("Parity.ShaderProfilesNote")));
        panel.Children.Add(pickRow);
        panel.Children.Add(nameRow);
        panel.Children.Add(ParitySettingsTab.Buttons(export, import));
        panel.Children.Add(status);
        Refresh(null);
        return panel;
    }
}
