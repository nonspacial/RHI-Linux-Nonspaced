using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views.Dialogs;

/// <summary>#211: the NVIDIA settings dialog's layout; Copy puts the hand-off launch line on the clipboard.</summary>
public sealed partial class NvidiaSettingsDialogView : UserControl
{
    public NvidiaSettingsDialogView() => AvaloniaXamlLoader.Load(this);

    private void Copy_Click(object? sender, RoutedEventArgs e)
        => UiExceptionGuard.Forget(CopyAsync(), nameof(NvidiaSettingsDialogView));

    private async Task CopyAsync()
    {
        if (DataContext is not NvidiaSettingsDialogViewModel model) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            model.ReportCopy(null);
            return;
        }
        var line = model.Settings.LaunchOptions;
        await clipboard.SetTextAsync(line);
        model.ReportCopy(line);
    }
}
