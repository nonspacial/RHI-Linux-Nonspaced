namespace RHI.Core.Models;

/// <summary>
/// Cross-platform evolution of RHI's original DetectedGame model.
/// The original fields are intentionally preserved; Linux-specific metadata is additive.
/// </summary>
public sealed class DetectedGame
{
    public string Name { get; set; } = "";
    public string InstallPath { get; set; } = "";
    public string Source { get; set; } = "";
    public bool IsManuallyAdded { get; set; }
    public int? SteamAppId { get; set; }
    public string? EpicCatalogNamespace { get; set; }
    public string? EpicAppName { get; set; }
    public string? XboxAumid { get; set; }

    // Linux additions.
    public string? PrefixPath { get; set; }
    public string? ExecutablePath { get; set; }
    public string? Runner { get; set; }
    public string? LaunchCommand { get; set; }
    public string? CompatibilityTool { get; set; }
    public GraphicsApi GraphicsApi { get; set; } = GraphicsApi.Unknown;
    public bool UsesProton => !string.IsNullOrWhiteSpace(PrefixPath) && SteamAppId.HasValue;

    public override string ToString() => $"{Name} ({Source})";
}
