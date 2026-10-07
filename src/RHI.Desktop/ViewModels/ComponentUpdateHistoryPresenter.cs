using System.Globalization;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.ViewModels;

public enum UpdateHistoryDay
{
    Today,
    Yesterday,
    Earlier
}

/// <summary>Text for the component update history dialog (upstream 2.7.9 "Updates" window).</summary>
public static class ComponentUpdateHistoryPresenter
{
    private static string T(string key) => UiLocalizationService.Text(key);

    public static UpdateHistoryDay Classify(DateTime localDate, DateTime today)
        => localDate.Date == today.Date ? UpdateHistoryDay.Today
            : localDate.Date == today.Date.AddDays(-1) ? UpdateHistoryDay.Yesterday
            : UpdateHistoryDay.Earlier;

    public static string DayLabel(DateTime localDate, DateTime today) => Classify(localDate, today) switch
    {
        UpdateHistoryDay.Today => T("Hist.Today"),
        UpdateHistoryDay.Yesterday => T("Hist.Yesterday"),
        _ => localDate.ToString("d MMMM yyyy", CultureInfo.CurrentUICulture)
    };

    public static string VersionText(ComponentUpdateLogEntry entry)
    {
        static string V(string? version) => string.IsNullOrWhiteSpace(version) ? "?" : version;
        return entry.Action switch
        {
            ComponentUpdateAction.Remove => $"{V(entry.FromVersion)}  →  {T("Hist.Removed")}",
            _ when !entry.Success => string.IsNullOrWhiteSpace(entry.FromVersion) ? "" : V(entry.FromVersion),
            ComponentUpdateAction.Install when string.IsNullOrWhiteSpace(entry.FromVersion) => $"{V(entry.ToVersion)}  ({T("Hist.New")})",
            _ => $"{V(entry.FromVersion)}  →  {V(entry.ToVersion)}"
        };
    }

    public static string ActionText(ComponentUpdateLogEntry entry) => (entry.Success, entry.Action) switch
    {
        (false, ComponentUpdateAction.Install) => T("Hist.InstallFailed"),
        (false, ComponentUpdateAction.Update) => T("Hist.UpdateFailed"),
        (false, _) => T("Hist.RemoveFailed"),
        (_, ComponentUpdateAction.Install) => T("Hist.Installed"),
        (_, ComponentUpdateAction.Update) => T("Hist.Updated"),
        _ => T("Hist.Removed2")
    };
}
