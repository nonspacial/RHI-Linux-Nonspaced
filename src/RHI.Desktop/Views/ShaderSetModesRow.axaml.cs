using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.Views;

/// <summary>
/// #179: the ReShade section's per-game Shaders / Add-ons choices (one AXAML element in <c>MainWindow.axaml</c>,
/// DataContext <see cref="ReShadeProfileViewModel"/>). Select opens the Shaders/Add-ons dialog for the game's own list;
/// closing the dropdown on Select again opens it again (upstream).
/// </summary>
public sealed partial class ShaderSetModesRow : UserControl
{
    private ReShadeProfileViewModel? _profile;

    public ShaderSetModesRow()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<ComboBox>("ShaderModeBox")!.DropDownClosed += (_, _) => _profile?.ReopenSelectList(ShaderAddonSetPart.Shaders);
        this.FindControl<ComboBox>("AddonModeBox")!.DropDownClosed += (_, _) => _profile?.ReopenSelectList(ShaderAddonSetPart.Addons);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_profile is not null) _profile.ShowSelectList = null;
        _profile = DataContext as ReShadeProfileViewModel;
        if (_profile is { } profile)
            profile.ShowSelectList = part => UiExceptionGuard.RunAsync(() => ShaderSetDialogs.ShowSelectListAsync(this, profile.Shell, part), "ShaderSetSelect");
    }
}
