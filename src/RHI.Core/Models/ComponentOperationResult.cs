namespace RHI.Core.Models;

public sealed record ComponentOperationResult(
    bool Success,
    string Message,
    IReadOnlyList<string>? ChangedFiles = null)
{
    public static ComponentOperationResult Ok(string message, params string[] changedFiles)
        => new(true, message, changedFiles);

    public static ComponentOperationResult Fail(string message)
        => new(false, message, Array.Empty<string>());
}
