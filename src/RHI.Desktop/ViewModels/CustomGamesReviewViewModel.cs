using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Detection.CustomFolders;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// #293 (upstream 2.9.0 review of the games found in the custom game folders): one row per game with the name to fix,
/// the exe to choose ("Check the exe" when two look alike), and — Linux, as Add game — the Wine/Proton prefix and an
/// optional launch command. Add selected adds the ticked rows as manual games (Add game's checks, one save, one refresh);
/// Ignore selected keeps them from being offered again; Not now hides the status line's link until the next scan.
/// </summary>
public sealed partial class CustomGamesReviewViewModel : ObservableObject
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    private readonly MainWindowViewModel _shell;

    internal CustomGamesReviewViewModel(MainWindowViewModel shell, IReadOnlyList<CustomGameCandidate> candidates, Func<string, Task<string?>> pickFolder)
    {
        _shell = shell;
        foreach (var candidate in candidates) Rows.Add(new CustomGameReviewRow(candidate, pickFolder));
        WindowTitle = candidates.Count == 1 ? T("CustomFolders.ReviewTitleOne") : TF("CustomFolders.ReviewTitleMany", candidates.Count);
    }

    public string WindowTitle { get; }
    public string Intro => T("CustomFolders.ReviewIntro");
    public ObservableCollection<CustomGameReviewRow> Rows { get; } = [];

    [ObservableProperty] private string? status;
    /// <summary>The buttons are off while the games are being added.</summary>
    [ObservableProperty] private bool canAct = true;

    public event Action? CloseRequested;

    /// <summary>"Not now": the link stays hidden until the next scan finds something.</summary>
    [RelayCommand]
    private void NotNow()
    {
        _shell.DismissCustomGamesNotice();
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void IgnoreSelected()
    {
        var picked = Rows.Where(row => row.IsChecked).ToArray();
        if (picked.Length == 0)
        {
            Status = T("CustomFolders.NoneSelected");
            return;
        }
        if (_shell.IgnoreCustomGames(picked.Select(row => row.Candidate).ToArray()) is { } notSaved)
        {
            Status = notSaved;
            return;
        }
        _shell.StatusText = picked.Length == 1 ? T("CustomFolders.IgnoredOne") : TF("CustomFolders.IgnoredMany", picked.Length);
        CloseRequested?.Invoke();
    }

    /// <summary>
    /// Adds the ticked rows. Rows that were added leave the dialog (and the pending list); refused rows stay with the
    /// reason in <see cref="Status"/>. The dialog closes once nothing is left to add.
    /// </summary>
    [RelayCommand]
    private async Task AddSelected()
    {
        var picked = Rows.Where(row => row.IsChecked).ToArray();
        if (picked.Length == 0)
        {
            Status = T("CustomFolders.NoneSelected");
            return;
        }
        CanAct = false;
        try
        {
            var refusals = new List<string>();
            var entries = new List<(CustomGameReviewRow Row, LinuxManualGameEntry Entry)>();
            foreach (var row in picked)
            {
                var name = row.Name?.Trim() ?? "";
                if (name.Length > 0 && _shell.IsListedName(name))
                {
                    refusals.Add(TF("CustomFolders.NotAdded", name, TF("CustomFolders.AlreadyInLibrary", name)));
                    continue;
                }
                entries.Add((row, row.ToEntry()));
            }
            var (added, refused, _) = await _shell.ManualGames.TryAddManualGamesAsync(entries.Select(e => e.Entry).ToArray());
            refusals.AddRange(refused.Select(r => TF("CustomFolders.NotAdded", string.IsNullOrWhiteSpace(r.Entry.Name) ? r.Entry.InstallPath : r.Entry.Name, r.Reason)));
            var addedRows = entries.Where(e => added.Contains(e.Entry)).Select(e => e.Row).ToArray();
            _shell.DismissCustomGames(addedRows.Select(row => row.Candidate));
            foreach (var row in addedRows) Rows.Remove(row);
            if (addedRows.Length > 0)
                _shell.StatusText = addedRows.Length == 1 ? T("CustomFolders.AddedOne") : TF("CustomFolders.AddedMany", addedRows.Length);
            Status = refusals.Count == 0 ? null : string.Join("\n", refusals);
            // Unticked rows stay pending for later; a refused row keeps the dialog open with its reason.
            if (refusals.Count == 0) CloseRequested?.Invoke();
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

/// <summary>One found game in the review: tick, name, exe, and (Linux) prefix and launch command.</summary>
public sealed partial class CustomGameReviewRow : ObservableObject
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    private readonly Func<string, Task<string?>> _pickFolder;

    internal CustomGameReviewRow(CustomGameCandidate candidate, Func<string, Task<string?>> pickFolder)
    {
        Candidate = candidate;
        _pickFolder = pickFolder;
        name = candidate.SuggestedName;
        ExeItems = candidate.Exes.Select(exe => $"{exe.RelativePath}  ({FormatSize(exe.SizeBytes)})").ToArray();
        Details = TF("CustomFolders.Engine", candidate.Engine ?? T("CustomFolders.EngineUnknown"), candidate.FolderPath);
    }

    public CustomGameCandidate Candidate { get; }
    public IReadOnlyList<string> ExeItems { get; }
    public bool CheckTheExe => Candidate.IsAmbiguous;
    public string CheckTheExeText => T("CustomFolders.CheckExe");
    public string Details { get; }

    [ObservableProperty] private bool isChecked = true;
    [ObservableProperty] private string? name;
    [ObservableProperty] private int exeIndex;
    [ObservableProperty] private string? prefix;
    [ObservableProperty] private string? launchCommand;

    /// <summary>The chosen exe (the best match unless the user picked another).</summary>
    public ScoredExe Exe => Candidate.Exes[ExeIndex >= 0 && ExeIndex < Candidate.Exes.Count ? ExeIndex : 0];

    // A picker or portal failure is logged and reported under the button's name, like Add game's (UI-05).
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task BrowsePrefix() => UiExceptionGuard.RunAsync(async () =>
    {
        if (await _pickFolder(T("Win.SelectWineProtonPrefix")) is { } chosen) Prefix = chosen;
    }, "CustomGames.BrowsePrefix");

    /// <summary>The manual-game entry Add game would save for this row (runner from the prefix, as Add game).</summary>
    internal LinuxManualGameEntry ToEntry()
    {
        var prefix = string.IsNullOrWhiteSpace(Prefix) ? null : Prefix.Trim();
        return new LinuxManualGameEntry
        {
            Name = Name?.Trim() ?? "",
            InstallPath = Candidate.FolderPath,
            ExecutablePath = Exe.FullPath,
            PrefixPath = prefix,
            LaunchCommand = LinuxGameEditOverride.NormalizeLaunchCommand(LaunchCommand),
            Runner = prefix is null ? "Manual Windows" : "Manual Wine"
        };
    }

    internal static string FormatSize(long bytes)
        => bytes >= 1024L * 1024 * 1024 ? (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.CurrentCulture) + " GB"
            : bytes >= 1024 * 1024 ? (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.CurrentCulture) + " MB"
            : Math.Max(1, bytes / 1024).ToString(CultureInfo.CurrentCulture) + " KB";
}
