using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace RHI.Desktop.Views.Dialogs;

/// <summary>Edit game's layout (the view model holds the form).</summary>
public sealed partial class EditGameDialogView : UserControl
{
    public EditGameDialogView() => AvaloniaXamlLoader.Load(this);
}
