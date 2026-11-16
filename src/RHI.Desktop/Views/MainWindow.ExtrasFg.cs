using Avalonia.Controls;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views;

/// <summary>w14/extras-fg: the window's side of the FG unlocks — the "Don't show this again" notices (#285, #278) and the MFG Unlock ⚙ (#279).</summary>
public partial class MainWindow
{
    // w14/extras-fg hook (#285): the window answers the shell's "Don't show this again" notices.
    private void AttachDismissibleNotices()
        => DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm) vm.ConfirmDismissibleAsync = ShowDismissibleNoticeAsync;
        };

    private async Task<DismissibleNoticeAnswer> ShowDismissibleNoticeAsync(DismissibleNotice notice)
    {
        var parts = DismissibleNoticeView.Build(notice, OpenProjectLink);
        var dialog = new Window
        {
            Title = notice.Title,
            Width = 600,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = parts.Root
        };
        parts.Cancel.Click += (_, _) => dialog.Close(false);
        parts.Accept.Click += (_, _) => dialog.Close(true);
        var accepted = await ShowFittedDialogAsync<bool>(dialog);
        return new DismissibleNoticeAnswer(accepted, parts.DontShow.IsChecked == true);
    }

    // w14/extras-fg hook (#279): MFG Unlock's ⚙.
    private void MfgUnlockSettings_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => RunUiSafe(async () =>
    {
        if (sender is not Button { DataContext: ComponentRowViewModel row } || !row.HasMfgUnlockSettings) return;
        if (DataContext is not MainWindowViewModel vm || vm.SelectedGame is null) return;
        await ShowComponentDialogAsync(new ProxyComponentDialogViewModel(vm, ProxyComponentDialogKind.MfgUnlock));
    });
}
