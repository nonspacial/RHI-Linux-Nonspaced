using System.Collections.ObjectModel;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Detection.Xbox;

namespace RHI.Desktop.ViewModels;

/// <summary>Card wording for Xbox / Game Pass installs (Refs #90).</summary>
public static class XboxCardText
{
    /// <summary>
    /// Why Play is unavailable on an Xbox card RHI found no way to open the owning app for: the user starts the game
    /// from that app (owner decision B). Null for any other card.
    /// </summary>
    public static string? PlayUnavailable(DetectedGame game)
        => game.XboxPackage is { } package && string.IsNullOrWhiteSpace(game.LaunchCommand)
            ? UiLocalizationService.Format("Scan.PlayStartFromOwningApp", package.OwningApp)
            : null;
}

/// <summary>
/// The Settings row "PartingX / Xbox folders" (Refs #90, owner decision C): the folders the Xbox detector searches besides
/// PartingX's default state folder and <c>~/Games</c>. The host (Settings) binds the list and texts, calls
/// <see cref="Add"/>/<see cref="Remove"/>, and rescans the Xbox detector when <see cref="Changed"/> fires.
/// </summary>
public sealed class XboxFoldersPresenter
{
    /// <summary>The "what" of a refused save ("Could not save {what}"), for the host's <c>TryPersist</c>.</summary>
    public const string WhatKey = "Scan.WhatXboxFolders";

    private readonly XboxFolderStore _store;
    private readonly Func<Action, string?> _persist;

    /// <param name="persist">The host's store-save helper (runs the save, returns the localised refusal or null).</param>
    public XboxFoldersPresenter(XboxFolderStore store, Func<Action, string?> persist)
    {
        _store = store;
        _persist = persist;
        Folders = new ObservableCollection<string>(store.Folders());
    }

    public ObservableCollection<string> Folders { get; }

    public event Action? Changed;

    public static string Label => UiLocalizationService.Text("Scan.XboxFoldersLabel");
    public static string ToolTip => UiLocalizationService.Format("Scan.XboxFoldersTip", XboxFolderStore.DefaultPartingXStateFolder);
    public static string AddText => UiLocalizationService.Text("Scan.XboxFoldersAdd");
    public static string RemoveText => UiLocalizationService.Text("Scan.XboxFoldersRemove");

    /// <summary>Adds a folder; null when saved (or already listed), else the refusal to show.</summary>
    public string? Add(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Trim()));
        if (Folders.Contains(full, StringComparer.Ordinal)) return null;
        return Save(Folders.Append(full).ToList());
    }

    /// <summary>Removes a folder; null when saved, else the refusal to show.</summary>
    public string? Remove(string folder) => Save(Folders.Where(f => !string.Equals(f, folder, StringComparison.Ordinal)).ToList());

    // The host's store-save helper: every view-model store write goes through it (LibraryStateSessionTests contract).
    private string? TryPersist(Action save) => _persist(save);

    private string? Save(List<string> folders)
    {
        if (TryPersist(() => _store.Save(folders)) is { } refused) return refused;
        Folders.Clear();
        foreach (var folder in _store.Folders()) Folders.Add(folder);
        Changed?.Invoke();
        return null;
    }
}
