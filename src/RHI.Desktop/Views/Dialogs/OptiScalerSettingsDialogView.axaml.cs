using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using RHI.Desktop.ViewModels;

namespace RHI.Desktop.Views.Dialogs;

/// <summary>The OptiScaler ⚙ dialog's layout; hosts the nightly-build row (#156) and the presets row (#187).</summary>
public sealed partial class OptiScalerSettingsDialogView : UserControl
{
    private Control _nightly;
    private Control _presets;
    private Control _allSettings;
    private Control _iniLibrary;

    public OptiScalerSettingsDialogView()
    {
        AvaloniaXamlLoader.Load(this);
        _nightly = this.FindControl<Control>("NightlyBuildSlot")!;
        _presets = this.FindControl<Control>("PresetsSlot")!;
        _allSettings = this.FindControl<Control>("AllSettingsSlot")!;
        _iniLibrary = this.FindControl<Control>("IniLibrarySlot")!;
        DataContextChanged += (_, _) =>
        {
            var model = DataContext as OptiScalerSettingsDialogViewModel;
            _nightly = DialogSlot.Fill(_nightly, model is null ? null : new OptiScalerNightlyBuildRow(model.NightlyBuild));
            _presets = DialogSlot.Fill(_presets, model?.Presets is { } slots
                ? OptiScalerPresetsRow.Create(slots, model.Snapshot, line => model.Message = line, model.RequestReopen)
                : null);
            // w14/optiscaler-2 (#272)
            _allSettings = DialogSlot.Fill(_allSettings, model?.AllSettings is { } all ? OptiScalerAllSettingsViews.AllSettings(all) : null);
            _iniLibrary = DialogSlot.Fill(_iniLibrary, model?.IniLibrary is { } library ? OptiScalerAllSettingsViews.IniLibrary(library) : null);
        };
    }
}
