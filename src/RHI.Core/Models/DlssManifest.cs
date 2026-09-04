using System.Text.Json.Serialization;

namespace RHI.Core.Models;

public sealed class DlssManifestData
{
    [JsonPropertyName("dlss")] public List<DlssManifestEntry> Dlss { get; set; } = [];
    [JsonPropertyName("dlssd")] public List<DlssManifestEntry> Dlssd { get; set; } = [];
    [JsonPropertyName("dlssg")] public List<DlssManifestEntry> Dlssg { get; set; } = [];
    [JsonPropertyName("dlssnr")] public List<DlssManifestEntry> Dlssnr { get; set; } = [];
    [JsonPropertyName("streamline")] public List<DlssManifestEntry> Streamline { get; set; } = [];
}

public sealed class DlssManifestEntry
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}

public enum DlssComponentKind
{
    SuperResolution,
    RayReconstruction,
    FrameGeneration,
    NeuralRendering
}

public sealed class DlssDetectionResult
{
    public IReadOnlyList<string> DlssPaths { get; init; } = [];
    public IReadOnlyList<string> DlssdPaths { get; init; } = [];
    public IReadOnlyList<string> DlssgPaths { get; init; } = [];
    public IReadOnlyList<string> DlssnrPaths { get; init; } = [];
    public IReadOnlyList<string> StreamlineFolders { get; init; } = [];

    public bool HasAny => DlssPaths.Count + DlssdPaths.Count + DlssgPaths.Count + DlssnrPaths.Count + StreamlineFolders.Count > 0;

    public IReadOnlyList<string> PathsFor(DlssComponentKind kind) => kind switch
    {
        DlssComponentKind.SuperResolution => DlssPaths,
        DlssComponentKind.RayReconstruction => DlssdPaths,
        DlssComponentKind.FrameGeneration => DlssgPaths,
        DlssComponentKind.NeuralRendering => DlssnrPaths,
        _ => []
    };
}
