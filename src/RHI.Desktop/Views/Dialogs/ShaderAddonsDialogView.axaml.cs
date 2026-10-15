using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views.Dialogs;

/// <summary>The Shaders/Add-ons dialog's layout; hosts the parity lane's shader-profile panel (#93) above the packs.</summary>
public sealed partial class ShaderAddonsDialogView : UserControl
{
    private Control _profiles;

    public ShaderAddonsDialogView()
    {
        AvaloniaXamlLoader.Load(this);
        _profiles = this.FindControl<Control>("ShaderProfileSlot")!;
        DataContextChanged += (_, _) => _profiles = DialogSlot.Fill(_profiles,
            DataContext is ShaderAddonsDialogViewModel model ? ShaderProfilePanel.Create(model.Parity, model.TickedShaderIds, model.TickShaders) : null);
    }
}
