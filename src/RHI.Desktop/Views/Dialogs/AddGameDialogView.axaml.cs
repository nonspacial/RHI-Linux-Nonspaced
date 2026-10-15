using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace RHI.Desktop.Views.Dialogs;

/// <summary>Add game's layout (the view model holds the form).</summary>
public sealed partial class AddGameDialogView : UserControl
{
    public AddGameDialogView() => AvaloniaXamlLoader.Load(this);
}
