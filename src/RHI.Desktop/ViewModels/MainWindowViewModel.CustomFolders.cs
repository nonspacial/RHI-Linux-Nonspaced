using System.Collections.ObjectModel;
using RHI.Core.Models;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Detection.CustomFolders;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

// #293 (upstream 2.9.0 Settings › Tools & Maintenance › Custom game folders): folders RHI scans for games from outside the
// supported stores, at start, on Refresh and with Scan now. Found games are never added on their own: the status line
// shows "N new games found in your custom folders · Review", and the review adds the ones the user picks as manual games
// (Linux: with a Wine/Proton prefix and launch command, as Add game). The lists live in the library file.
public partial class MainWindowViewModel
{
    private IReadOnlyList<CustomGameCandidate> _pendingCustomGames = [];
    private bool _customGamesNoticeDismissed;
    private int _customFolderScanRunning;

    /// <summary>Settings › Custom game folders, as saved.</summary>
    public ObservableCollection<string> CustomGameFolders { get; } = [];

    /// <summary>New games the last scan found that the user has not added or ignored.</summary>
    public IReadOnlyList<CustomGameCandidate> PendingCustomGames => _pendingCustomGames;

    /// <summary>Raised when <see cref="PendingCustomGames"/> or the folder lists change (Settings refreshes its card).</summary>
    public event Action? CustomGameFoldersChanged;

    /// <summary>Exact game title lookup for found folders (PCGamingWiki's page titles; set by the composition root).</summary>
    public Func<string, string?>? CustomFolderTitleLookup { get; set; }

    /// <summary>Test seam: the PE probe the scan uses (default: read the exe).</summary>
    internal Func<string, ExeProbe>? CustomFolderProbe { get; set; }

    public int IgnoredCustomGameCount => _libraryState.IgnoredCustomGameFolders?.Count ?? 0;

    public bool ShowCustomGamesNotice => _pendingCustomGames.Count > 0 && !_customGamesNoticeDismissed;

    public string CustomGamesNoticeText => _pendingCustomGames.Count == 1
        ? T("CustomFolders.NoticeOne")
        : TF("CustomFolders.NoticeMany", _pendingCustomGames.Count);

    public string CustomGamesNoticeTip => T("CustomFolders.NoticeTip");

    /// <summary>Fills <see cref="CustomGameFolders"/> from the library file (Settings calls it when the tab opens).</summary>
    public void LoadCustomGameFolders()
    {
        CustomGameFolders.Clear();
        foreach (var folder in _libraryState.CustomGameFolders ?? []) CustomGameFolders.Add(folder);
        OnPropertyChanged(nameof(IgnoredCustomGameCount));
    }

    /// <summary>Adds a folder to the list; the localised refusal, or null when it is saved.</summary>
    public string? AddCustomGameFolder(string path)
    {
        if (LibraryWritesPaused()) return StatusText;
        AdoptRestoredLibraryFile();
        if (CustomFolderScanService.ValidateFolder(path, _libraryState.CustomGameFolders ?? []) is { } refusal) return RefusalText(refusal);
        return SaveCustomFolderState(state => (state.CustomGameFolders ??= []).Add(CustomFolderScanService.NormalizePath(path)));
    }

    /// <summary>Add folder: saves it, then scans at once. The line to show (refusal, or the scan's outcome).</summary>
    public async Task<string> AddCustomGameFolderAndScanAsync(string path)
    {
        if (AddCustomGameFolder(path) is { } refused) return refused;
        return await ScanCustomFoldersNowAsync();
    }

    /// <summary>Takes a folder off the list; games already added from it stay. The line to show.</summary>
    public string RemoveCustomGameFolder(string path)
    {
        if (LibraryWritesPaused()) return StatusText;
        var normalized = CustomFolderScanService.NormalizePath(path);
        if (SaveCustomFolderState(state => state.CustomGameFolders?.RemoveAll(folder => string.Equals(folder, normalized, StringComparison.Ordinal))) is { } notSaved)
            return notSaved;
        SetPendingCustomGames(_pendingCustomGames.Where(c => !string.Equals(c.CustomFolder, normalized, StringComparison.Ordinal)).ToArray());
        return TF("CustomFolders.Removed", normalized);
    }

    /// <summary>"Forget ignored games": ignored and removed custom-folder games are offered again by the next scan.</summary>
    public string ForgetIgnoredCustomGames()
    {
        if (LibraryWritesPaused()) return StatusText;
        return SaveCustomFolderState(state => state.IgnoredCustomGameFolders = null) ?? T("CustomFolders.Forgotten");
    }

    /// <summary>Scan now (Settings): scans and says what it found. Settings opens the review when there is something.</summary>
    public async Task<string> ScanCustomFoldersNowAsync()
    {
        if ((_libraryState.CustomGameFolders?.Count ?? 0) == 0) return T("CustomFolders.AddFirst");
        var found = await ScanCustomFoldersAsync();
        return found is null ? T("CustomFolders.ScanRunning")
            : found.Count == 0 ? T("CustomFolders.NoneFound")
            : found.Count == 1 ? T("CustomFolders.FoundOne")
            : TF("CustomFolders.FoundMany", found.Count);
    }

    /// <summary>
    /// Scans the custom folders now and replaces the pending games; null when a scan is already running. The library
    /// snapshot is taken here (UI thread), the folders are read off it.
    /// </summary>
    public async Task<IReadOnlyList<CustomGameCandidate>?> ScanCustomFoldersAsync()
    {
        if (Interlocked.Exchange(ref _customFolderScanRunning, 1) == 1) return null;
        try
        {
            var request = BuildCustomFolderScanRequest();
            if (request.Folders.Count == 0)
            {
                SetPendingCustomGames([]);
                return [];
            }
            IReadOnlyList<CustomGameCandidate> found;
            using (BeginActivity("Sb.ActCustomFolders")) // #298
                found = await CustomFolderScanService.ScanAsync(request, CancellationToken.None);
            // A game added (or removed from RHI) while the scan ran is not offered.
            var known = BuildCustomFolderScanRequest();
            found = found.Where(candidate => !IsKnownCustomGame(candidate, known)).ToArray();
            SetPendingCustomGames(found);
            return found;
        }
        finally
        {
            Interlocked.Exchange(ref _customFolderScanRunning, 0);
        }
    }

    // Start and Refresh: a background scan after the library scan, never in the way of the user.
    private void StartCustomFolderScan()
    {
        if ((_libraryState.CustomGameFolders?.Count ?? 0) == 0) return;
        UiExceptionGuard.Forget(ScanCustomFoldersAsync(), "custom-folders-scan");
    }

    private CustomFolderScanRequest BuildCustomFolderScanRequest()
    {
        var paths = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var game in _library.AllGames)
        {
            if (!string.IsNullOrWhiteSpace(game.InstallPath)) paths.Add(game.InstallPath);
            if (!string.IsNullOrWhiteSpace(game.LibraryIdentityPath)) paths.Add(game.LibraryIdentityPath);
            names.Add(CustomGameExeScorer.Norm(game.Name));
            names.Add(CustomGameExeScorer.Norm(game.DisplayName()));
        }
        foreach (var manual in _libraryState.ManualGames)
        {
            paths.Add(manual.InstallPath);
            names.Add(CustomGameExeScorer.Norm(manual.Name));
        }
        // Games taken out with "Remove from RHI" are not offered again.
        foreach (var removed in _libraryState.RemovedGames ?? []) names.Add(CustomGameExeScorer.Norm(removed.Name));
        names.Remove("");
        var lookup = CustomFolderTitleLookup;
        return new CustomFolderScanRequest
        {
            Folders = [.. _libraryState.CustomGameFolders ?? []],
            IgnoredFolders = [.. _libraryState.IgnoredCustomGameFolders ?? []],
            LibraryPaths = paths,
            KnownNames = names,
            CanonicalTitle = lookup is null ? null : name =>
            {
                try { return lookup(name); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
            },
            Probe = CustomFolderProbe
        };
    }

    private static bool IsKnownCustomGame(CustomGameCandidate candidate, CustomFolderScanRequest known)
        => known.IgnoredFolders.Any(folder => RHI.Core.Services.PathIdentity.AreSame(folder, candidate.FolderPath))
           || known.LibraryPaths.Any(path => RHI.Core.Services.PathIdentity.IsSameOrInside(path, candidate.FolderPath)
                                             || RHI.Core.Services.PathIdentity.IsSameOrInside(candidate.FolderPath, path))
           || known.KnownNames.Contains(CustomGameExeScorer.Norm(candidate.SuggestedName));

    private void SetPendingCustomGames(IReadOnlyList<CustomGameCandidate> list)
    {
        _pendingCustomGames = list;
        _customGamesNoticeDismissed = false; // a new scan result shows the link again after "Not now"
        NotifyCustomGames();
    }

    private void NotifyCustomGames()
    {
        OnPropertyChanged(nameof(PendingCustomGames));
        OnPropertyChanged(nameof(ShowCustomGamesNotice));
        OnPropertyChanged(nameof(CustomGamesNoticeText));
        OnPropertyChanged(nameof(IgnoredCustomGameCount));
        CustomGameFoldersChanged?.Invoke();
    }

    /// <summary>Takes games off the pending list (added, ignored or gone).</summary>
    internal void DismissCustomGames(IEnumerable<CustomGameCandidate> done)
    {
        var drop = done.Select(c => c.FolderPath).ToHashSet(StringComparer.Ordinal);
        _pendingCustomGames = _pendingCustomGames.Where(c => !drop.Contains(c.FolderPath)).ToArray();
        NotifyCustomGames();
    }

    /// <summary>"Not now": hides the status line's link until the next scan finds something.</summary>
    internal void DismissCustomGamesNotice()
    {
        _customGamesNoticeDismissed = true;
        NotifyCustomGames();
    }

    /// <summary>"Ignore selected": the games' folders are not offered again. The localised refusal, or null when saved.</summary>
    internal string? IgnoreCustomGames(IReadOnlyCollection<CustomGameCandidate> candidates)
    {
        if (candidates.Count == 0) return null;
        if (LibraryWritesPaused()) return StatusText;
        if (SaveCustomFolderState(state =>
            {
                state.IgnoredCustomGameFolders ??= [];
                state.IgnoredCustomGameFolders.AddRange(candidates.Select(c => c.FolderPath));
            }) is { } notSaved)
            return notSaved;
        DismissCustomGames(candidates);
        return null;
    }

    /// <summary>The review of the pending games (null when there is none); <paramref name="pickFolder"/> answers Browse prefix.</summary>
    public CustomGamesReviewViewModel? CreateCustomGamesReview(Func<string, Task<string?>> pickFolder)
        => _pendingCustomGames.Count == 0 ? null : new CustomGamesReviewViewModel(this, _pendingCustomGames, pickFolder);

    // Mutates a copy of the library state and adopts it once saved; the localised refusal, or null.
    private string? SaveCustomFolderState(Action<LinuxGameLibraryState> change)
    {
        var state = WorkingLibraryCopy();
        change(state);
        if (TryPersist(() => _libraryStore.Save(state), "Ui.WhatGameLibrary", sessionOnly: false) is { } notSaved) return notSaved;
        _libraryState = state;
        LoadCustomGameFolders();
        NotifyCustomGames();
        return null;
    }

    private static string RefusalText(CustomFolderRefusal refusal) => refusal.Problem switch
    {
        CustomFolderProblem.NoFolder => T("CustomFolders.NoFolder"),
        CustomFolderProblem.Missing => TF("CustomFolders.Missing", refusal.Other),
        CustomFolderProblem.WholeDrive => T("CustomFolders.WholeDrive"),
        CustomFolderProblem.HomeFolder => T("CustomFolders.HomeFolder"),
        CustomFolderProblem.WinePrefix => T("CustomFolders.WinePrefix"),
        CustomFolderProblem.SteamLibrary => T("CustomFolders.SteamLibrary"),
        CustomFolderProblem.AlreadyListed => T("CustomFolders.AlreadyListed"),
        CustomFolderProblem.InsideListed => TF("CustomFolders.InsideListed", refusal.Other),
        CustomFolderProblem.ContainsListed => TF("CustomFolders.ContainsListed", refusal.Other),
        _ => T("CustomFolders.NoFolder")
    };

    /// <summary>A game with this (normalised) name is already listed.</summary>
    internal bool IsListedName(string name)
    {
        var norm = CustomGameExeScorer.Norm(name);
        return norm.Length > 0 && _library.AllGames.Any(game => CustomGameExeScorer.Norm(game.Name) == norm || CustomGameExeScorer.Norm(game.DisplayName()) == norm);
    }
}
