using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Artwork;
using RHI.Platform.Linux.Paths;

namespace RHI.Desktop.Views;

/// <summary>
/// w14/layouts-b (#305, upstream 2.9.5 beta SettingsHandler.SteamGridDb): Settings › Accounts &amp; Data › SteamGridDB API
/// key — what it is for, Get a free API key (opens steamgriddb.com), the key box (Enter or Save checks and saves it),
/// Remove, and the status line.
/// </summary>
internal static class SteamGridDbKeySection
{
    private static string T(string key) => UiLocalizationService.Text(key);

    internal static Control Create(SteamGridDbKeyPresenter presenter)
    {
        var section = new StackPanel { Spacing = 8, Name = "SteamGridDbKeySection" };
        section.Children.Add(ParitySettingsTab.Note(T("Sgdb.KeyNote")));
        var status = ParitySettingsTab.Status();
        var box = new TextBox
        {
            PasswordChar = '•', PlaceholderText = T("Sgdb.KeyPlaceholder"), MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Stretch,
            Name = "SteamGridDbKeyBox",
        };
        ToolTip.SetTip(box, T("Sgdb.KeyBoxTip"));
        Avalonia.Automation.AutomationProperties.SetName(box, T("Sgdb.KeyTitle"));
        var save = ParitySettingsTab.Button(T("Sgdb.Save"), T("Sgdb.SaveTip"));
        var remove = ParitySettingsTab.Button(T("Sgdb.Remove"), T("Sgdb.RemoveTip"));
        var getKey = ParitySettingsTab.Button(T("Sgdb.GetKey"), T("Sgdb.GetKeyTip"));

        async Task SaveAsync()
        {
            if (await presenter.SaveAsync(box.Text)) box.Text = "";
        }

        void Sync()
        {
            status.Text = presenter.StatusText;
            remove.IsVisible = presenter.HasSavedKey;
            save.IsEnabled = remove.IsEnabled = box.IsEnabled = !presenter.IsBusy;
            save.Content = presenter.IsBusy ? T("Sgdb.CheckingShort") : T("Sgdb.Save");
        }
        ParitySettingsTab.Bind(section, presenter, Sync);
        save.Click += (_, _) => UiExceptionGuard.Forget(SaveAsync(), "SteamGridDbKeySave");
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            UiExceptionGuard.Forget(SaveAsync(), "SteamGridDbKeySave");
        };
        remove.Click += (_, _) => presenter.Clear();
        getKey.Click += (_, _) =>
        {
            if (!LinuxDesktopOpener.TryOpenUri(SteamGridDbService.ApiKeyPage, out var error))
                status.Text = UiLocalizationService.Present(error ?? "");
        };

        var entry = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        entry.Children.Add(box);
        entry.Children.Add(save);
        Grid.SetColumn(save, 1);
        section.Children.Add(entry);
        section.Children.Add(ParitySettingsTab.Buttons(getKey, remove));
        section.Children.Add(status);
        return section;
    }
}
