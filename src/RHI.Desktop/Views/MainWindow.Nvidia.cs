using Avalonia.Controls;

namespace RHI.Desktop.Views;

public sealed partial class MainWindow
{
    /// <summary>w7/nvapi-launch hook (#211): the NVIDIA settings' frame-generation dialog, fitted to this window like every dialog.</summary>
    internal Task ShowNvidiaDialogAsync(Window dialog) => ShowFittedDialogAsync(dialog);
}
