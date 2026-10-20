using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// What the "Install RenoDX add-on" dialog offers for a dropped or watched file (#195): the library games it can go into
/// (sorted by name), the one RHI matched (or -1), and per game the folder and what it replaces.
/// </summary>
public sealed record DroppedRenoDxOffer(string FileName, string SourcePath, IReadOnlyList<DetectedGame> Games, int MatchedIndex,
    bool DeleteSourceAfterInstall)
{
    public string Title => UiLocalizationService.Text("Sm.DropTitle");
    public string Intro => UiLocalizationService.Format("Sm.DropIntro", FileName);
    public string Note => UiLocalizationService.Text("Sm.DropNote");
    public string ChooseTip => UiLocalizationService.Text("Sm.DropChooseTip");
    public IReadOnlyList<string> GameNames => Games.Select(game => game.Name).ToArray();
}

/// <summary>
/// #195 (upstream <c>DragDropHandler.ProcessDroppedAddon</c>, <c>AddonFileWatcher</c>): a RenoDX add-on dropped on RHI, or
/// found by the add-on watch folder, is offered for install into a library game — the game its name matches (the RenoDX
/// catalogue's file for the game, then the game's name in the file name, then upstream's word match), else the selected
/// game, else the user picks — always confirmed first (the dialog names the file and the game). It installs through the
/// RenoDX deployment service as a local package (recorded like a RenoDX install; Update All cannot check it). Other
/// ReShade add-ons keep the Custom Add-ons import.
/// </summary>
public sealed class DroppedRenoDxPresenter(LinuxManagedComponentService components)
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    /// <summary>Whether a dropped or watched file goes this way (a <c>renodx-*.addon64|32</c> game add-on).</summary>
    public static bool Handles(string? path) => RenoDxDeploymentService.IsDroppedRenoDxAddon(path);

    /// <summary>
    /// The dialog's content, or null when no library game can take a RenoDX add-on (native Linux games cannot). With
    /// <paramref name="watchFolder"/> set, a file directly in it is removed after a successful install (upstream's
    /// <c>deleteSourceAfterInstall</c>).
    /// </summary>
    public DroppedRenoDxOffer? OfferFor(MainWindowViewModel vm, string path, string? watchFolder = null)
    {
        var games = vm.AllLibraryGames
            .Where(game => !string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
            .OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (games.Length == 0) return null;
        var fileName = Path.GetFileName(path);
        return new DroppedRenoDxOffer(fileName, path, games, MatchIndex(games, fileName, vm.SelectedGame, components.RenoDxCatalogueFileName),
            watchFolder is not null && InFolder(path, watchFolder));
    }

    /// <summary>The folder the add-on would go into for <paramref name="game"/> (beside ReShade).</summary>
    public static string FolderFor(DetectedGame game)
        => game.BinaryDirectory is { Length: > 0 } binaries ? RenoDxDeploymentService.ResolveAddonDeployPath(binaries) : game.InstallPath;

    /// <summary>The line naming the RenoDX add-on RHI installed for <paramref name="game"/> that this one replaces, else null.</summary>
    public string? ReplacesFor(DetectedGame game)
        => components.GetRenoDxRecord(game)?.Files.FirstOrDefault()?.Path is { } installed ? TF("Sm.DropReplaces", Path.GetFileName(installed)) : null;

    /// <summary>
    /// The matched game's index in <paramref name="games"/>: the catalogue's add-on file for the game, then the game's
    /// whole name inside the file name (upstream <c>FuzzyMatchGameIndex</c>; the longest such name), then upstream's word
    /// match (a game of two or more words with a word longer than three letters in the file name), then the selected game;
    /// -1 when none.
    /// </summary>
    public static int MatchIndex(IReadOnlyList<DetectedGame> games, string fileName, DetectedGame? selected, Func<DetectedGame, string?> catalogueFile)
    {
        for (var i = 0; i < games.Count; i++)
            if (string.Equals(catalogueFile(games[i]), fileName, StringComparison.OrdinalIgnoreCase)) return i;

        var stem = Normalize(Path.GetFileNameWithoutExtension(fileName));
        var best = -1;
        for (var i = 0; i < games.Count; i++)
        {
            var name = Normalize(games[i].Name);
            if (name.Length >= 4 && stem.Contains(name, StringComparison.Ordinal) && (best < 0 || name.Length > Normalize(games[best].Name).Length)) best = i;
        }
        if (best >= 0) return best;

        var lower = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        for (var i = 0; i < games.Count; i++)
        {
            var words = games[i].Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 2 && words.Any(word => word.Length > 3 && lower.Contains(word.ToLowerInvariant(), StringComparison.Ordinal))) return i;
        }

        if (selected is not null)
            for (var i = 0; i < games.Count; i++)
                if (ReferenceEquals(games[i], selected) || string.Equals(games[i].Name, selected.Name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(games[i].InstallPath, selected.InstallPath, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>Letters and digits only, lower case (™, ®, colons, apostrophes, spaces and dashes dropped).</summary>
    internal static string Normalize(string text) => string.Concat(text.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static bool InFolder(string path, string folder)
    {
        try
        {
            return string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Installs the offered file into <paramref name="game"/> through the shell's operation gate; the status line says what
    /// happened. True when it was installed. A watched file directly in the watch folder is then removed (upstream).
    /// </summary>
    public async Task<bool> InstallAsync(MainWindowViewModel vm, DroppedRenoDxOffer offer, DetectedGame game)
    {
        using var op = vm.Operations.TryBegin("renodx-local");
        if (op is null)
        {
            vm.StatusText = T("Vm.AnotherRHIOperationAlreadyRunning");
            return false;
        }
        vm.StatusText = TF("Sm.DropInstalling", offer.FileName, game.Name);
        ComponentOperationResult result;
        try
        {
            result = await components.InstallLocalRenoDxAddonAsync(game, offer.SourcePath, op.Token);
        }
        catch (OperationCanceledException) when (op.CancelRequested)
        {
            vm.StatusText = MainWindowViewModel.OperationCancelledText();
            return false;
        }
        var line = UiLocalizationService.Present(result.Message);
        if (result.Success && offer.DeleteSourceAfterInstall && File.Exists(offer.SourcePath))
            line += FileCleanup.TryDelete(offer.SourcePath) ? " " + T("Sm.DropSourceRemoved") : "";
        vm.StatusText = line;
        if (ReferenceEquals(vm.SelectedGame, game)) await vm.ReprobeSelectedAsync(game);
        return result.Success;
    }
}
