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
    /// <summary>#216: the archive the add-on is in (<see cref="SourcePath"/> is then the archive), else null.</summary>
    public DroppedArchive? Archive { get; init; }
    /// <summary>#216: the add-on inside <see cref="Archive"/>; <see cref="FileName"/> is its name.</summary>
    public DroppedArchiveAddon? ArchiveAddon { get; init; }
    public bool IsLuma => ArchiveAddon?.Kind == DroppedArchiveAddonKind.Luma;

    public string Title => UiLocalizationService.Text(IsLuma ? "Arc.LumaTitle" : IsRenoDx ? "Sm.DropTitle" : "Sm.DropAddonTitle");
    public string Intro => Archive is { } archive
        ? UiLocalizationService.Format(IsLuma ? "Arc.LumaIntro" : IsRenoDx ? "Arc.DropIntro" : "Arc.DropAddonIntro", FileName, archive.ArchiveName)
        : UiLocalizationService.Format(IsRenoDx ? "Sm.DropIntro" : "Sm.DropAddonIntro", FileName);
    public string Note => UiLocalizationService.Text(IsLuma ? "Arc.LumaNote" : IsRenoDx ? "Sm.DropNote" : "Sm.DropAddonNote");
    public string ChooseTip => UiLocalizationService.Text("Sm.DropChooseTip");
    public IReadOnlyList<string> GameNames => Games.Select(game => game.Name).ToArray();
}

/// <summary>
/// #216: what the drop dialog offers for a dropped or watched archive: one entry per add-on RHI can install from it (an
/// "Add-on" dropdown above the game list when there are two or more). Owns the archive's private folder: disposing it
/// (or an install from it) removes the folder.
/// </summary>
public sealed class DroppedArchiveOffer(DroppedArchive archive, IReadOnlyList<DroppedRenoDxOffer> addOns) : IDisposable
{
    public DroppedArchive Archive { get; } = archive;
    public IReadOnlyList<DroppedRenoDxOffer> AddOns { get; } = addOns;
    /// <summary>
    /// The "Add-on" dropdown's entries: each add-on's file name, or its path inside the archive when another add-on of the
    /// archive has the same file name (w10 follow-up D5), so two entries never read the same.
    /// </summary>
    public IReadOnlyList<string> AddOnNames => AddOns.Select(offer =>
        offer.ArchiveAddon is { } addon && AddOns.Count(other => string.Equals(other.FileName, offer.FileName, StringComparison.OrdinalIgnoreCase)) > 1
            ? addon.Member.Replace('\\', '/')
            : offer.FileName).ToArray();
    public string AddOnTip => UiLocalizationService.Text("Arc.AddonChoiceTip");
    public void Dispose() => Archive.Dispose();
}

/// <summary>What the shell does with a file the add-on watch folder raised (<see cref="DroppedRenoDxPresenter.RouteFor"/>).</summary>
public enum WatchedFileRoute
{
    /// <summary>A Luma add-on file: the Luma package prompt for the selected game.</summary>
    LumaAddonPrompt,
    /// <summary>A RenoDX game add-on: the drop dialog (#195).</summary>
    DropDialog,
    /// <summary>Another add-on: the Custom Add-ons import.</summary>
    CustomAddonsImport,
    /// <summary>#216: a <c>.zip</c>/<c>.7z</c>/<c>.rar</c> named for RenoDX or Luma: read, checked and its add-on offered.</summary>
    ArchiveOffer,
}

/// <summary>#216: an archive offer, or the status line saying why there is none (nothing was installed or left behind).</summary>
public sealed record DroppedArchiveOfferResult(DroppedArchiveOffer? Offer, string? Line);

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

    /// <summary>The status line when <see cref="OfferForAsync"/> finds no game: says why for an add-on that needs RHI's ReShade.</summary>
    public static string NoGameLine(string path, bool watched)
        => LinuxDroppedFileClassifier.AddonTarget(path, watched) == DroppedAddonTarget.GameAddonInstall
            ? TF("Sm.DropAddonNoGame", Path.GetFileName(path))
            : TF("Sm.DropNoGame", Path.GetFileName(path));

    /// <summary>
    /// The route of a watched file. Every watched archive — a Luma <c>.zip</c> included (#216 decision 4: one dialog that
    /// names the game, instead of the old prompt for the selected game) — goes to the archive offer.
    /// </summary>
    public static WatchedFileRoute RouteFor(LinuxWatchedFileCandidate candidate)
        => candidate.Kind != LinuxWatchedFileKind.Addon ? WatchedFileRoute.ArchiveOffer
            : LinuxDroppedFileClassifier.IsLikelyLumaAddon(candidate.Path) ? WatchedFileRoute.LumaAddonPrompt
            : Handles(candidate.Path, watched: true) ? WatchedFileRoute.DropDialog
            : WatchedFileRoute.CustomAddonsImport;

    /// <summary>
    /// Whether a dropped file goes to the archive offer (<see cref="OfferForArchiveAsync"/>): every dropped archive, a Luma
    /// <c>.zip</c> included — one dialog for every dropped or watched archive, as <see cref="RouteFor"/> does for the
    /// watch folder.
    /// </summary>
    public static bool OffersDroppedArchive(LinuxDroppedFileKind kind)
        => kind is LinuxDroppedFileKind.ModArchive or LinuxDroppedFileKind.LumaArchive;

    /// <summary>Whether a dropped (or, with <paramref name="watched"/>, watched) add-on gets this dialog.</summary>
    public static bool Handles(string? path, bool watched)
        => LinuxDroppedFileClassifier.AddonTarget(path, watched) != DroppedAddonTarget.CustomAddonsImport;

    /// <summary>
    /// The dialog's content, or null when no library game can take the add-on (native Linux games cannot; another add-on
    /// goes only into a game with RHI's ReShade with add-on support — w7 follow-up B10: the others are not listed). With
    /// <paramref name="watchFolder"/> set, a file directly in it is removed after a successful install (upstream's
    /// <c>deleteSourceAfterInstall</c>).
    /// <para>W7 final review 7: the ReShade check reads every game's folder and DLL signatures, so it runs off the UI
    /// thread; the library and the selected game are read before, the offer is built after, on the caller's thread.</para>
    /// </summary>
    public async Task<DroppedRenoDxOffer?> OfferForAsync(MainWindowViewModel vm, string path, string? watchFolder = null)
    {
        var isRenoDx = LinuxDroppedFileClassifier.AddonTarget(path, watched: watchFolder is not null) != DroppedAddonTarget.GameAddonInstall;
        var library = vm.AllLibraryGames
            .Where(game => !string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var selected = vm.SelectedGame;
        var refusal = Refusal;
        var games = isRenoDx ? library : await Task.Run(() => library.Where(game => refusal(game) is null).ToArray());
        games = games.OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (games.Length == 0) return null;
        var fileName = Path.GetFileName(path);
        return new DroppedRenoDxOffer(fileName, path, games, MatchIndex(games, fileName, selected, components.RenoDxCatalogueFileName),
            watchFolder is not null && InFolder(path, watchFolder), isRenoDx);
    }

    /// <summary>Why a dropped non-RenoDX add-on cannot go into a game, or null (tests watch the thread it runs on).</summary>
    internal Func<DetectedGame, string?> Refusal { get; set; } = components.DroppedAddonRefusal;

    /// <summary>
    /// #216 (upstream <c>ProcessDroppedArchive</c>): a dropped (or, with <paramref name="watchFolder"/>, watched) archive is
    /// copied, listed and checked off the UI thread (<see cref="LinuxManagedComponentService.InspectDroppedArchiveAsync"/>);
    /// every RenoDX or Luma add-on inside that a library game can take is offered, each with its own games (as the same
    /// file dropped alone). No offer → the status line says why (refused, no add-on, no
    /// game) and the archive's folder is already gone.
    /// </summary>
    public async Task<DroppedArchiveOfferResult> OfferForArchiveAsync(MainWindowViewModel vm, string path, string? watchFolder = null)
    {
        var name = Path.GetFileName(path);
        var inspection = await Task.Run(() => components.InspectDroppedArchiveAsync(path));
        if (inspection.Archive is not { } archive)
            return new(null, TF("Arc.Refused", name, UiLocalizationService.Present(inspection.Refusal)));
        try
        {
            if (archive.Addons.Count == 0)
            {
                archive.Dispose();
                return new(null, TF(watchFolder is null ? "Win.ArchiveDetectedRHILinuxDoesNot" : "Win.WatcherDetectedGenericArchivesNeverInstalled", name));
            }
            var library = vm.AllLibraryGames
                .Where(game => !string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
                .OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var selected = vm.SelectedGame;
            var refusal = Refusal;
            var withReShade = archive.Addons.Any(addon => addon.Kind == DroppedArchiveAddonKind.GameAddon)
                ? await Task.Run(() => library.Where(game => refusal(game) is null).ToArray())
                : [];
            var deleteSource = watchFolder is not null && InFolder(path, watchFolder);
            var offers = new List<DroppedRenoDxOffer>();
            foreach (var addon in archive.Addons)
            {
                var games = addon.Kind == DroppedArchiveAddonKind.GameAddon ? withReShade : library;
                if (games.Length == 0) continue;
                // The add-on's own name first (the RenoDX catalogue's file), then the archive's (upstream matches Luma by it).
                var match = MatchIndex(games, addon.FileName, null, components.RenoDxCatalogueFileName);
                if (match < 0) match = MatchIndex(games, name, selected, components.RenoDxCatalogueFileName);
                offers.Add(new DroppedRenoDxOffer(addon.FileName, path, games, match, deleteSource, addon.Kind != DroppedArchiveAddonKind.GameAddon)
                    { Archive = archive, ArchiveAddon = addon });
            }
            if (offers.Count == 0)
            {
                archive.Dispose();
                var first = archive.Addons[0];
                return new(null, TF(first.Kind == DroppedArchiveAddonKind.GameAddon ? "Sm.DropAddonNoGame" : "Sm.DropNoGame", first.FileName));
            }
            return new(new DroppedArchiveOffer(archive, offers), null);
        }
        catch
        {
            archive.Dispose();
            throw;
        }
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
            offer.Archive?.Dispose();
            vm.StatusText = T("Vm.AnotherRHIOperationAlreadyRunning");
            return false;
        }
        vm.StatusText = offer.IsLuma ? TF("Vm.InstallingLocalLumaPackage", game.Name)
            : TF(offer.IsRenoDx ? "Sm.DropInstalling" : "Sm.DropAddonInstalling", offer.FileName, game.Name);
        ComponentOperationResult result;
        try
        {
            if (offer is { Archive: { } archive, ArchiveAddon: { } addon })
            {
                // #216: the archive's Luma package goes through the same install warnings as a Luma package dropped alone.
                if (offer.IsLuma && !await ((IPanelHost)vm).AcceptManifestInstallWarningsAsync(game, ManagedComponentKind.LumaFramework, "Luma",
                        installsTarget: true, vm.Session.CachedFiles(game)))
                    return false;
                result = await components.InstallArchivedAddonAsync(game, archive, addon, op.Token);
            }
            else
            {
                result = offer.IsRenoDx
                    ? await components.InstallLocalRenoDxAddonAsync(game, offer.SourcePath, op.Token)
                    : await components.InstallDroppedAddonForGameAsync(game, offer.SourcePath, op.Token);
            }
        }
        catch (OperationCanceledException) when (op.CancelRequested)
        {
            vm.StatusText = MainWindowViewModel.OperationCancelledText();
            return false;
        }
        finally
        {
            offer.Archive?.Dispose(); // #216: RHI's copy and anything taken out of it, whatever happened
        }
        var line = UiLocalizationService.Present(result.Message);
        if (result.Success && offer.DeleteSourceAfterInstall && File.Exists(offer.SourcePath))
        {
            if (offer.Archive is { } read)
                // #216: a watched archive goes only while it still holds the bytes RHI read (a browser may save a new one).
                line += await read.SourceStillMatchesAsync() ? FileCleanup.TryDelete(offer.SourcePath) ? " " + T("Sm.DropSourceRemoved") : ""
                    : " " + T("Arc.SourceKept");
            else if (await StillTheInstalledFileAsync(offer.SourcePath, result))
                line += FileCleanup.TryDelete(offer.SourcePath) ? " " + T("Sm.DropSourceRemoved") : "";
        }
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
