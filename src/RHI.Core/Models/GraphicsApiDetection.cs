namespace RHI.Core.Models;

public enum GraphicsApiConfidence
{
    Unknown,
    Low,
    Medium,
    High
}

/// <summary>
/// Result of a graphics API probe. RHI Linux keeps the primary API for compatibility
/// with the original model while also retaining every API signature that was observed.
/// </summary>
public sealed record GraphicsApiDetection(
    GraphicsApi Primary,
    IReadOnlyList<GraphicsApi> Candidates,
    GraphicsApiConfidence Confidence,
    string Evidence)
{
    public static GraphicsApiDetection Unknown { get; } =
        new(GraphicsApi.Unknown, [], GraphicsApiConfidence.Unknown, "No reliable graphics API signature detected");
}
