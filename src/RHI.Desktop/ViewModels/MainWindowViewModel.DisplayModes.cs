namespace RHI.Desktop.ViewModels;

/// <summary>w14/display-modes (#311): the shell's display modes (switch, Settings card, Custom mode, Keep).</summary>
public partial class MainWindowViewModel
{
    private DisplayModesPresenter? _displayModes;

    /// <summary>#311: Display modes (Monitor / TV / …). Built at start so installs and updates keep a game's mode values.</summary>
    public DisplayModesPresenter DisplayModes => _displayModes ??= new DisplayModesPresenter(this, _managedComponents, _deploymentRecords, _settingsStore.ConfigRoot);
}
