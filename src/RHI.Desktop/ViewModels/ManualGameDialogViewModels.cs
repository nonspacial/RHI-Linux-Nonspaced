using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RHI.Core.Models;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Detection;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// File and folder pickers a dialog asks the window for (title in, local path or null out); the window decides how
/// (portal availability included). Kept out of the view models so tests answer them.
/// </summary>
public sealed record DialogPickers(Func<string, Task<string?>> PickFolder, Func<string, Task<string?>> PickExecutable);

/// <summary>
/// Add game for a chosen executable (epic #89: was built in the window's code-behind): the game root and name are
/// inferred from the executable; prefix and launch command are optional. Closes as soon as the entry is saved; a refusal
/// stays visible in the dialog.
/// </summary>
public sealed partial class AddGameDialogViewModel : ObservableObject
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    private readonly ManualGamesViewModel _manualGames;
    private readonly DialogPickers _pickers;

    internal AddGameDialogViewModel(ManualGamesViewModel manualGames, string executable, DialogPickers pickers)
    {
        _manualGames = manualGames;
        _pickers = pickers;
        Executable = executable;
        InstallPath = ManualGamePathInference.InferInstallRoot(executable);
        name = ManualGamePathInference.InferGameName(executable, InstallPath);
    }

    public string WindowTitle => T("Win.AddGameRHILinux");
    public string Executable { get; }
    public string InstallPath { get; }
    [ObservableProperty] private string? name;
    [ObservableProperty] private string? prefix;
    [ObservableProperty] private string? launchCommand;
    [ObservableProperty] private string? status;
    /// <summary>Add and Cancel are off while the entry is being saved.</summary>
    [ObservableProperty] private bool canAct = true;

    public event Action? CloseRequested;

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();

    // A picker/portal failure is logged and reported under the button's own name, like the window's handlers (UI-05).
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task BrowsePrefix() => UiExceptionGuard.RunAsync(async () =>
    {
        if (await _pickers.PickFolder(T("Win.SelectWineProtonPrefix")) is { } chosen) Prefix = chosen;
    }, "AddGame.BrowsePrefix");

    [RelayCommand]
    private async Task Add()
    {
        CanAct = false;
        try
        {
            var prefix = string.IsNullOrWhiteSpace(Prefix) ? null : Prefix.Trim();
            var entry = new LinuxManualGameEntry
            {
                Name = Name?.Trim() ?? "",
                InstallPath = InstallPath,
                ExecutablePath = Executable,
                PrefixPath = prefix,
                LaunchCommand = LinuxGameEditOverride.NormalizeLaunchCommand(LaunchCommand),
                Runner = prefix is null ? "Manual Windows" : "Manual Wine"
            };
            // Closes as soon as the entry is saved: a refresh that waits for a running operation
            // reports to the main status line, not to a disabled dialog with no message.
            var (added, result) = await _manualGames.TryAddManualGameAsync(entry, saved: () => CloseRequested?.Invoke());
            if (!added) Status = result;
        }
        catch (Exception ex)
        {
            Status = TF("Win.CouldNotAddGame", UiLocalizationService.Present(ex.Message));
        }
        finally
        {
            CanAct = true;
        }
    }
}

/// <summary>
/// Edit game (epic #89: was built in the window's code-behind): name, wiki mod name and bitness (Refs #190), launcher and
/// launch command, and the modification target (executable, game root, binaries folder) with Detect / Browse. Save and
/// Restore automatic detection close once saved; a refusal stays visible in the dialog.
/// </summary>
public sealed partial class EditGameDialogViewModel : ObservableObject
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    private readonly ManualGamesViewModel _manualGames;
    private readonly DialogPickers _pickers;
    private readonly IReadOnlyList<string> _providerValues = OptionLists.LaunchProvider.Values;
    private readonly IReadOnlyList<PeArchitecture?> _bitnessValues = OptionLists.Bitness.Values;

    internal EditGameDialogViewModel(ManualGamesViewModel manualGames, DetectedGame game, DialogPickers pickers)
    {
        _manualGames = manualGames;
        _pickers = pickers;
        WindowTitle = TF("Win.EditGame", game.DisplayName());
        Heading = TF("Win.Edit", game.DisplayName());
        // Name and bitness (Refs #190): plain labels, the explanation in a 2 s tooltip.
        displayName = game.DisplayName();
        DisplayNamePlaceholder = game.Name;
        wikiName = game.UserWikiName;
        WikiNamePlaceholder = game.DetectedWikiName?.Value ?? T("Shell.WikiNamePlaceholder");
        var bitness = _bitnessValues.ToList().IndexOf(game.UserArchitectureOverride);
        bitnessIndex = bitness >= 0 ? bitness : 0;
        LibrarySource = TF("Vm.LibrarySource", game.LibraryProvider ?? game.Source);
        // Stored provider values stay the canonical tokens; only the labels are localised.
        var provider = _providerValues.ToList().FindIndex(value => string.Equals(value, game.LaunchProvider, StringComparison.OrdinalIgnoreCase));
        providerIndex = provider >= 0 ? provider : 0;
        LaunchExecutable = game.LaunchExecutablePath ?? T("Win.ManagedByLauncherWithoutExposedLocal");
        // The command that runs on Play (the user's, else the detected one), editable as in Add game; clearing it goes
        // back to the detected command.
        launchCommand = game.LaunchCommand;
        patchExecutable = game.HasConfirmedPatchTarget ? game.ExecutablePath : "";
        gameRoot = game.HasConfirmedPatchTarget ? game.InstallPath : "";
        binaryDirectory = game.HasConfirmedPatchTarget ? game.BinaryDirectory : "";
    }

    public string WindowTitle { get; }
    public string Heading { get; }
    public string DisplayNamePlaceholder { get; }
    public string WikiNamePlaceholder { get; }
    public IReadOnlyList<string> BitnessItems { get; } = OptionLists.Bitness.Items;
    public IReadOnlyList<string> ProviderItems { get; } = OptionLists.LaunchProvider.Items;
    public string LibrarySource { get; }
    public string LaunchExecutable { get; }

    [ObservableProperty] private string? displayName;
    [ObservableProperty] private string? wikiName;
    [ObservableProperty] private int bitnessIndex;
    [ObservableProperty] private int providerIndex;
    [ObservableProperty] private string? launchCommand;
    [ObservableProperty] private string? patchExecutable;
    [ObservableProperty] private string? gameRoot;
    [ObservableProperty] private string? binaryDirectory;
    [ObservableProperty] private string? status;
    /// <summary>Save, Restore automatic detection and Cancel are off while one of them runs.</summary>
    [ObservableProperty] private bool canAct = true;

    public event Action? CloseRequested;

    /// <summary>The name fields as Save hands them on (Refs #190).</summary>
    internal GameNameEdit Names => new(DisplayName, WikiName, BitnessIndex >= 0 ? _bitnessValues[BitnessIndex] : null);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();

    /// <summary>Game root and binaries folder from an existing target executable.</summary>
    [RelayCommand]
    private void DetectRoot()
    {
        var path = PatchExecutable?.Trim();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        var full = Path.GetFullPath(path);
        GameRoot = ManualGamePathInference.InferInstallRoot(full);
        BinaryDirectory = Path.GetDirectoryName(full) ?? GameRoot;
    }

    [RelayCommand]
    private void DetectBinary()
    {
        var path = PatchExecutable?.Trim();
        if (!string.IsNullOrWhiteSpace(path))
            BinaryDirectory = Path.GetDirectoryName(path) ?? BinaryDirectory;
    }

    // A picker/portal failure is logged and reported under the button's own name, like the window's handlers (UI-05).
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task BrowseExecutable() => UiExceptionGuard.RunAsync(async () =>
    {
        var selected = await _pickers.PickExecutable(T("Win.SelectRealGameExecutable"));
        if (string.IsNullOrWhiteSpace(selected)) return;
        PatchExecutable = selected;
        DetectRoot();
    }, "EditGame.BrowseExecutable");

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task BrowseRoot() => UiExceptionGuard.RunAsync(async () =>
    {
        if (await _pickers.PickFolder(T("Win.SelectRealGameRoot")) is { } chosen) GameRoot = chosen;
    }, "EditGame.BrowseRoot");

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task BrowseBinary() => UiExceptionGuard.RunAsync(async () =>
    {
        if (await _pickers.PickFolder(T("Win.SelectBinariesPatchesDirectory")) is { } chosen) BinaryDirectory = chosen;
    }, "EditGame.BrowseBinary");

    [RelayCommand]
    private async Task Reset()
    {
        CanAct = false;
        try
        {
            // Closes once the reset is saved; a refusal (managed components present) stays visible here.
            var (reset, message) = await _manualGames.ResetSelectedGameEditAsync(saved: () => CloseRequested?.Invoke());
            if (!reset) Status = message;
        }
        catch (Exception ex)
        {
            Status = TF("Win.CouldNotRestoreAutomaticDetection", UiLocalizationService.Present(ex.Message));
        }
        finally
        {
            CanAct = true;
        }
    }

    [RelayCommand]
    private async Task Save()
    {
        CanAct = false;
        try
        {
            var chosenProvider = ProviderIndex >= 0 ? _providerValues[ProviderIndex] : null;
            // Closes as soon as the edit is saved; the outcome goes to the main status line.
            var (saved, message) = await _manualGames.SaveSelectedGameEditAsync(
                PatchExecutable?.Trim() ?? "",
                GameRoot?.Trim() ?? "",
                BinaryDirectory?.Trim() ?? "",
                chosenProvider,
                LaunchCommand,
                saved: () => CloseRequested?.Invoke(),
                names: Names);
            if (!saved) Status = message;
        }
        catch (Exception ex)
        {
            Status = TF("Win.CouldNotSave", UiLocalizationService.Present(ex.Message));
        }
        finally
        {
            CanAct = true;
        }
    }
}
