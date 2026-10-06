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
    private static string L(string ptBr, string enUs) => UiLocalizationService.Choose(ptBr, enUs);

    public static UpdateHistoryDay Classify(DateTime localDate, DateTime today)
        => localDate.Date == today.Date ? UpdateHistoryDay.Today
            : localDate.Date == today.Date.AddDays(-1) ? UpdateHistoryDay.Yesterday
            : UpdateHistoryDay.Earlier;

    public static string DayLabel(DateTime localDate, DateTime today) => Classify(localDate, today) switch
    {
        UpdateHistoryDay.Today => L("Hoje", "Today"),
        UpdateHistoryDay.Yesterday => L("Ontem", "Yesterday"),
        _ => localDate.ToString("d MMMM yyyy", CultureInfo.CurrentUICulture)
    };

    public static string VersionText(ComponentUpdateLogEntry entry)
    {
        static string V(string? version) => string.IsNullOrWhiteSpace(version) ? "?" : version;
        return entry.Action switch
        {
            ComponentUpdateAction.Remove => $"{V(entry.FromVersion)}  →  {L("removido", "removed")}",
            _ when !entry.Success => string.IsNullOrWhiteSpace(entry.FromVersion) ? "" : V(entry.FromVersion),
            ComponentUpdateAction.Install when string.IsNullOrWhiteSpace(entry.FromVersion) => $"{V(entry.ToVersion)}  ({L("novo", "new")})",
            _ => $"{V(entry.FromVersion)}  →  {V(entry.ToVersion)}"
        };
    }

    public static string ActionText(ComponentUpdateLogEntry entry) => (entry.Success, entry.Action) switch
    {
        (false, ComponentUpdateAction.Install) => L("Falha na instalação", "Install failed"),
        (false, ComponentUpdateAction.Update) => L("Falha na atualização", "Update failed"),
        (false, _) => L("Falha na remoção", "Remove failed"),
        (_, ComponentUpdateAction.Install) => L("Instalado", "Installed"),
        (_, ComponentUpdateAction.Update) => L("Atualizado", "Updated"),
        _ => L("Removido", "Removed")
    };
}
