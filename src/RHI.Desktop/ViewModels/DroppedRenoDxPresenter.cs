using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Ux;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// What the "Install RenoDX add-on" (or "Install add-on") dialog offers for a dropped or watched file (#195): the library
/// games it can go into (sorted by name), the one RHI matched (or -1), and per game the folder and what it replaces.
/// <paramref name="IsRenoDx"/>: a RenoDX game add-on (the game's RenoDX mod), else another add-on dropped on RHI.
/// </summary>
public sealed record DroppedRenoDxOffer(string FileName, string SourcePath, IReadOnlyList<DetectedGame> Games, int MatchedIndex,
    bool DeleteSourceAfterInstall, bool IsRenoDx = true)
{
    public string Title => UiLocalizationService.Text(IsRenoDx ? "Sm.DropTitle" : "Sm.DropAddonTitle");
    public string Intro => UiLocalizationService.Format(IsRenoDx ? "Sm.DropIntro" : "Sm.DropAddonIntro", FileName);
    public string Note => UiLocalizationService.Text(IsRenoDx ? "Sm.DropNote" : "Sm.DropAddonNote");
    public string ChooseTip => UiLocalizationService.Text("Sm.DropChooseTip");
    public IReadOnlyList<string> GameNames => Games.Select(game => game.Name).ToArray();
}

/// <summary>
/// #195 (upstream <c>DragDropHandler.ProcessDroppedAddon</c>, <c>AddonFileWatcher</c>): an add-on dropped on RHI, or a
/// RenoDX add-on found by the add-on watch folder (upstream's sets, <see cref="LinuxDroppedFileClassifier.AddonTarget"/>),
/// is offered for install into a library game — the game its name matches (the RenoDX catalogue's file for the game, then
/// the game's name in the file name, then upstream's word match), else the selected game, else the user picks — always
/// confirmed first (the dialog names the file and the game). A RenoDX add-on installs through the RenoDX deployment
/// service as a local package (recorded like a RenoDX install; Update All cannot check it); another add-on goes into the
/// game's Shaders/Add-ons selection. Files upstream offers no game install for keep the Custom Add-ons import.
/// </summary>
public sealed class DroppedRenoDxPresenter(LinuxManagedComponentService components)
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    /// <summary>The status line when <see cref="OfferFor"/> finds no game: says why for an add-on that needs RHI's ReShade.</summary>
    public static string NoGameLine(string path, bool watched)
        => LinuxDroppedFileClassifier.AddonTarget(path, watched) == DroppedAddonTarget.GameAddonInstall
            ? TF("Sm.DropAddonNoGame", Path.GetFileName(path))
            : TF("Sm.DropNoGame", Path.GetFileName(path));

    /// <summary>Whether a dropped (or, with <paramref name="watched"/>, watched) add-on gets this dialog.</summary>
    public static bool Handles(string? path, bool watched)
        => LinuxDroppedFileClassifier.AddonTarget(path, watched) != DroppedAddonTarget.CustomAddonsImport;

    /// <summary>
    /// The dialog's content, or null when no library game can take the add-on (native Linux games cannot; another add-on
    /// goes only into a game with RHI's ReShade with add-on support — w7 follow-up B10: the others are not listed). With
    /// <paramref name="watchFolder"/> set, a file directly in it is removed after a successful install (upstream's
    /// <c>deleteSourceAfterInstall</c>).
    /// </summary>
    public DroppedRenoDxOffer? OfferFor(MainWindowViewModel vm, string path, string? watchFolder = null)
    {
        var isRenoDx = LinuxDroppedFileClassifier.AddonTarget(path, watched: watchFolder is not null) != DroppedAddonTarget.GameAddonInstall;
        var games = vm.AllLibraryGames
            .Where(game => !string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
            .Where(game => isRenoDx || components.DroppedAddonRefusal(game) is null)
            .OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (games.Length == 0) return null;
        var fileName = Path.GetFileName(path);
        return new DroppedRenoDxOffer(fileName, path, games, MatchIndex(games, fileName, vm.SelectedGame, components.RenoDxCatalogueFileName),
            watchFolder is not null && InFolder(path, watchFolder), isRenoDx);
    }

    /// <summary>The folder the add-on would go into for <paramref name="game"/> (beside ReShade).</summary>
    public static string FolderFor(DetectedGame game)
        => game.BinaryDirectory is { Length: > 0 } binaries ? RenoDxDeploymentService.ResolveAddonDeployPath(binaries) : game.InstallPath;

    /// <summary>The line naming the RenoDX add-on RHI installed for <paramref name="game"/> that this one replaces, else null.</summary>
    public string? ReplacesFor(DetectedGame game)
        => components.GetRenoDxRecord(game)?.Files.FirstOrDefault()?.Path is { } installed ? TF("Sm.DropReplaces", Path.GetFileName(installed)) : null;

    /// <summary>The line for the dialog under the folder: what a RenoDX add-on replaces; nothing for another add-on.</summary>
    public string? ReplacesFor(DroppedRenoDxOffer offer, DetectedGame game) => offer.IsRenoDx ? ReplacesFor(game) : null;

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
    /// happened. True when it was installed. A watched file directly in the watch folder is then removed (upstream), but
    /// only while it is still the file the install copied (a browser may have saved a new download under its name).
    /// </summary>
    public async Task<bool> InstallAsync(MainWindowViewModel vm, DroppedRenoDxOffer offer, DetectedGame game)
    {
        using var op = vm.Operations.TryBegin("renodx-local");
        if (op is null)
        {
            vm.StatusText = T("Vm.AnotherRHIOperationAlreadyRunning");
            return false;
        }
        vm.StatusText = TF(offer.IsRenoDx ? "Sm.DropInstalling" : "Sm.DropAddonInstalling", offer.FileName, game.Name);
        ComponentOperationResult result;
        try
        {
            result = offer.IsRenoDx
                ? await components.InstallLocalRenoDxAddonAsync(game, offer.SourcePath, op.Token)
                : await components.InstallDroppedAddonForGameAsync(game, offer.SourcePath, op.Token);
        }
        catch (OperationCanceledException) when (op.CancelRequested)
        {
            vm.StatusText = MainWindowViewModel.OperationCancelledText();
            return false;
        }
        var line = UiLocalizationService.Present(result.Message);
        if (result.Success && offer.DeleteSourceAfterInstall && File.Exists(offer.SourcePath) && await StillTheInstalledFileAsync(offer.SourcePath, result))
            line += FileCleanup.TryDelete(offer.SourcePath) ? " " + T("Sm.DropSourceRemoved") : "";
        vm.StatusText = line;
        if (ReferenceEquals(vm.SelectedGame, game)) await vm.ReprobeSelectedAsync(game);
        return result.Success;
    }

    /// <summary>Whether the source still has the bytes of the copy the install placed (its first changed file).</summary>
    private static async Task<bool> StillTheInstalledFileAsync(string source, ComponentOperationResult result)
    {
        if (result.ChangedFiles is not [var copy, ..] || !File.Exists(copy)) return false;
        try
        {
            return FileHash.HexEquals(await FileHash.Sha256HexAsync(source), await FileHash.Sha256HexAsync(copy));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
