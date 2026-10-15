using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace RHI.Desktop.Views.Dialogs;

/// <summary>A shader pack's Files dialog layout (the view model holds the ticks).</summary>
public sealed partial class ShaderPackFilesDialogView : UserControl
{
    public ShaderPackFilesDialogView() => AvaloniaXamlLoader.Load(this);
}
