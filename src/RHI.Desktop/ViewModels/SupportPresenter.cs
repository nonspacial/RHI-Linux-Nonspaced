using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.Input;
using RHI.Core;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Ux;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// The support bundle's actions (Refs #192): Copy logs and Export game data (Settings → Support), Copy game report (card
/// menu). Each writes its file through <see cref="DiagnosticsBundle"/> into RHI's support folder and puts the file's path
/// (or, for a report, the report itself) on the clipboard through the window's <c>copyText</c>; the outcome is one
/// localised line. Files are built off the UI thread. A clipboard the session does not offer still leaves the file saved,
/// and the line says where.
/// </summary>
public sealed class SupportPresenter
{
    private static readonly ConditionalWeakTable<MainWindowViewModel, SupportPresenter> Presenters = new();

    private readonly MainWindowViewModel _shell;

    public static SupportPresenter For(MainWindowViewModel shell) => Presenters.GetValue(shell, s => new SupportPresenter(s));

    private SupportPresenter(MainWindowViewModel shell)
    {
        _shell = shell;
        ReportCommand = new AsyncRelayCommand<DetectedGame>(async game =>
        {
            if (game is not null && ShowReportDialogAsync is { } show) await show(game);
        });
    }

    /// <summary>The bundle the actions write through (test seam; the current user's folders by default).</summary>
    internal DiagnosticsBundle Bundle { get; set; } = DiagnosticsBundle.ForCurrentUser();

    /// <summary>Set by the window: shows Copy game report's dialog (the note box) for a game.</summary>
    public Func<DetectedGame, Task>? ShowReportDialogAsync { get; set; }

    /// <summary>The card menu's Copy game report.</summary>
    public AsyncRelayCommand<DetectedGame> ReportCommand { get; }

    /// <summary>RHI's log folder (Open logs folder).</summary>
    public string LogDirectory => Bundle.LogDirectory;

    /// <summary>Zips RHI's logs (masked) and puts the zip's path on the clipboard.</summary>
    public async Task<string> CopyLogsAsync(Func<string, Task<bool>> copyText)
    {
        try
        {
            var bundle = Bundle;
            var path = await Task.Run(bundle.CreateLogsArchive);
            if (path is null) return T("Shell.NoLogsYet");
            return await copyText(path)
                ? TF("Shell.LogsCopied", path)
                : TF("Shell.SavedNoClipboard", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TF("Shell.SupportFileFailed", UiLocalizationService.Present(ex.Message));
        }
    }

    /// <summary>Exports every library game's data (upstream's community-database JSON) as a zip; its path goes on the clipboard.</summary>
    public async Task<string> ExportGameDataAsync(Func<string, Task<bool>> copyText)
    {
        var games = _shell.AllLibraryGames.ToArray();
        try
        {
            var bundle = Bundle;
            var path = await Task.Run(() => bundle.ExportGameData(games.Select(GameReport.DataRow).ToArray(), RhiProductInfo.Version));
            return await copyText(path)
                ? TF("Shell.GameDataExported", games.Length, path)
                : TF("Shell.SavedNoClipboard", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TF("Shell.SupportFileFailed", UiLocalizationService.Present(ex.Message));
        }
    }

    /// <summary>The masked markdown report of <paramref name="game"/> with the user's note.</summary>
    public string ReportFor(DetectedGame game, string? note)
        => Bundle.Mask(GameReport.Markdown(game, ((ILibraryEditHost)_shell).DeploymentRecords.FindAll(game), RhiProductInfo.Version, note, DateTime.Now));

    /// <summary>Saves the report in the support folder and puts its text on the clipboard.</summary>
    public async Task<string> CopyReportAsync(DetectedGame game, string? note, Func<string, Task<bool>> copyText)
    {
        var report = ReportFor(game, note);
        try
        {
            var bundle = Bundle;
            var path = await Task.Run(() => bundle.SaveReport(game.Name, report));
            return await copyText(report)
                ? TF("Shell.ReportCopied", game.DisplayName(), path)
                : TF("Shell.SavedNoClipboard", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TF("Shell.SupportFileFailed", UiLocalizationService.Present(ex.Message));
        }
    }

    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);
}
