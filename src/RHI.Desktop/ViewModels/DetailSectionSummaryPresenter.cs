using RHI.Core.Models;

namespace RHI.Desktop.ViewModels;

public sealed record SectionSummaryItem(string Label, string? Version);

/// <summary>
/// One-line summaries shown on collapsed detail sections (upstream 2.7.6). Each section summarises
/// what is installed or overridden in it; an empty list means nothing is shown.
/// </summary>
public static class DetailSectionSummaryPresenter
{
    public const string Separator = "  ·  ";

    /// <summary>Upstream "Components": installed components with their versions.</summary>
    public static IReadOnlyList<SectionSummaryItem> ManagedComponents(IEnumerable<ComponentRowViewModel> rows)
        => rows
            .Where(row => row.Kind is not (ManagedComponentKind.Dlss or ManagedComponentKind.Streamline)
                          && row.Availability is ComponentAvailability.Installed or ComponentAvailability.UpdateAvailable)
            .Select(row => new SectionSummaryItem(row.Name, Blank(row.InstalledVersion)))
            .ToArray();

    /// <summary>Upstream "Extras" lists the ASI Loader by name only.</summary>
    public static IReadOnlyList<SectionSummaryItem> Extras(bool asiLoaderInstalled, bool shortFuseInstalled, string? shortFuseVersion)
    {
        var items = new List<SectionSummaryItem>();
        if (asiLoaderInstalled) items.Add(new SectionSummaryItem("ASI Loader", null));
        if (shortFuseInstalled) items.Add(new SectionSummaryItem("ShortFuse", Blank(shortFuseVersion)));
        return items;
    }

    /// <summary>Upstream "Game Overrides": only the graphics API override exists in this section on Linux.</summary>
    public static IReadOnlyList<SectionSummaryItem> Runtime(DetectedGame? game)
        => game is { HasExplicitGraphicsApiOverride: true, UserGraphicsApiOverride: { } api }
            ? [new SectionSummaryItem($"API: {ShortApi(api)}", null)]
            : [];

    public static string Text(IReadOnlyList<SectionSummaryItem> items)
        => string.Join(Separator, items.Select(item => item.Version is null ? item.Label : $"{item.Label} {item.Version}"));

    private static string ShortApi(GraphicsApi api) => api switch
    {
        GraphicsApi.DirectX8 => "DX8",
        GraphicsApi.DirectX9 => "DX9",
        GraphicsApi.DirectX10 => "DX10",
        GraphicsApi.DirectX11 => "DX11",
        GraphicsApi.DirectX12 => "DX12",
        GraphicsApi.Vulkan => "Vulkan",
        GraphicsApi.OpenGL => "OpenGL",
        _ => api.ToString()
    };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
