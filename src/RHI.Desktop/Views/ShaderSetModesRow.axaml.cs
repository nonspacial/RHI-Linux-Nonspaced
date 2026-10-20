using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.Views;

/// <summary>
/// #179: the ReShade section's per-game Shaders / Add-ons choices (one AXAML element in <c>MainWindow.axaml</c>,
/// DataContext <see cref="ReShadeProfileViewModel"/>). Select opens the Shaders/Add-ons dialog for the game's own list;
/// closing the dropdown on Select again without choosing anything else opens it again (upstream).
/// </summary>
public sealed partial class ShaderSetModesRow : UserControl
{
    private ReShadeProfileViewModel? _profile;

    public ShaderSetModesRow()
    {
        AvaloniaXamlLoader.Load(this);
        Wire(this.FindControl<ComboBox>("ShaderModeBox")!, ShaderAddonSetPart.Shaders);
        Wire(this.FindControl<ComboBox>("AddonModeBox")!, ShaderAddonSetPart.Addons);
    }

    // Re-opens the list only when the dropdown closed without a new choice (a choice already did what it does, and a
    // refused one is put back by the view model, which must not open the dialog).
    private void Wire(ComboBox box, ShaderAddonSetPart part)
    {
        bool open = false, chosen = false;
        box.DropDownOpened += (_, _) => (open, chosen) = (true, false);
        box.SelectionChanged += (_, _) => chosen |= open;
        // Posted: a selection the close itself commits is counted first, whichever order the control raises them in.
        box.DropDownClosed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            open = false;
            if (!chosen) _profile?.ReopenSelectList(part);
        }, Avalonia.Threading.DispatcherPriority.Background);
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
