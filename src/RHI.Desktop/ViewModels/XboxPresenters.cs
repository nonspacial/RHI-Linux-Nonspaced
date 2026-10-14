using System.Collections.ObjectModel;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Core.Services;
using RHI.Platform.Linux.Detection.Xbox;
using RHI.Platform.Linux.Launch;

namespace RHI.Desktop.ViewModels;

/// <summary>Card wording for Xbox / Game Pass installs (Refs #90).</summary>
public static class XboxCardText
{
    /// <summary>
    /// How the UI names the app that owns a card: the Xbox installer app is never named, it is "the app that installed it"
    /// (owner decision); Xodus and any launcher keep their own names.
    /// </summary>
    public static string? AppName(string? app)
        => string.Equals(app, XboxInstallerApp.DisplayName, StringComparison.Ordinal) ? UiLocalizationService.Text("Scan.InstallerAppName") : app;

    /// <summary>
    /// Why Play is unavailable on an Xbox card RHI found no way to open the owning app for: the user starts the game
    /// from that app (owner decision B). Null for any other card.
    /// </summary>
    public static string? PlayUnavailable(DetectedGame game)
        => game.XboxPackage is { } package && string.IsNullOrWhiteSpace(game.LaunchCommand)
            ? UiLocalizationService.Format("Scan.PlayStartFromOwningApp", AppName(package.OwningApp)!)
            : null;
}

/// <summary>
/// "Show in the app that installed it" (Refs #90, spec item 3), for the library card's right-click menu (#161): the item is shown only for
/// a card whose installer-app breadcrumb has a show entry point, and runs it (an argument vector, no shell).
/// </summary>
public static class XboxCardActions
{
    /// <summary>The menu item's text ("Show in the app that installed it"); null when the card has no such entry point (no item).</summary>
    public static string? ShowInOwningAppLabel(DetectedGame? game)
        => game is not null && LinuxGameLauncher.CanShowInOwningApp(game)
            ? UiLocalizationService.Format("Scan.ShowInOwningApp", XboxCardText.AppName(game.XboxPackage!.OwningApp)!)
            : null;

    /// <summary>Runs the show entry point; null when it started, else the status line saying why not.</summary>
    public static async Task<string?> ShowInOwningAppAsync(DetectedGame game, LinuxGameLauncher launcher, CancellationToken cancellationToken = default)
    {
        var app = XboxCardText.AppName(game.XboxPackage?.OwningApp ?? XboxInstallerApp.DisplayName)!;
        try
        {
            await launcher.ShowInOwningAppAsync(game, cancellationToken);
            return null;
        }
        catch (GameLaunchFailedException ex)
        {
            return string.IsNullOrWhiteSpace(ex.Detail)
                ? UiLocalizationService.Format("Scan.ShowInOwningAppExited", app, ex.ExitCode)
                : UiLocalizationService.Format("Scan.ShowInOwningAppExited2", app, ex.ExitCode, ex.Detail);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return UiLocalizationService.Format("Scan.ShowInOwningAppFailed", app, UiLocalizationService.Present(ex.Message));
        }
    }
}

/// <summary>
/// The Settings row "Xbox / Game Pass folders" (Refs #90, owner decision C): the folders the Xbox detector searches besides
/// The installer app's default state folder and <c>~/Games</c>. The host (Settings) binds the list and texts, calls
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

    /// <summary>Xodus' project page (the upcoming Linux Game Pass client the tab's description names).</summary>
    public const string XodusUrl = "https://github.com/xodus-gaming/xodus";

    public static string Label => UiLocalizationService.Text("Scan.XboxFoldersLabel");
    /// <summary>The tab's plain description: what is listed, Xodus as an upcoming client, when to add a folder.</summary>
    public static string Intro => UiLocalizationService.Text("Scan.XboxFoldersIntro");
    public static string XodusLinkText => UiLocalizationService.Text("Scan.XodusLink");
    public static string ToolTip => UiLocalizationService.Text("Scan.XboxFoldersTip");
    public static string AddText => UiLocalizationService.Text("Scan.XboxFoldersAdd");
    public static string RemoveText => UiLocalizationService.Text("Vm.Remove");

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
