using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace RHI.Desktop.Views.Dialogs;

/// <summary>#234: the read-only Check Neural Rendering page's layout (no code: Close is the view model's command).</summary>
public sealed partial class NeuralRenderingCheckDialogView : UserControl
{
    public NeuralRenderingCheckDialogView() => AvaloniaXamlLoader.Load(this);
}
