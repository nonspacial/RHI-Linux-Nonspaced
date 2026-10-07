namespace RHI.Core.Services;

public interface IFileDeploymentService
{
    Task<FileDeploymentResult> ReplaceAsync(string sourcePath, string targetPath, CancellationToken cancellationToken = default);
    Task<FileDeploymentResult> RestoreAsync(string targetPath, CancellationToken cancellationToken = default);
    Task<FileDeploymentBatchResult> ReplaceBatchAsync(IEnumerable<(string Source, string Target)> files, CancellationToken cancellationToken = default);
    Task<FileDeploymentBatchResult> RestoreBatchAsync(IEnumerable<string> targetPaths, CancellationToken cancellationToken = default);
}

/// <param name="Sha256">
/// SHA-256 (upper-case hex) of the content now at <paramref name="TargetPath"/> after a successful
/// call, so callers can record ownership without hashing the target again. Null on failure.
/// </param>
public sealed record FileDeploymentResult(
    bool Success,
    string TargetPath,
    string? BackupPath,
    string? Error = null,
    bool Changed = true,
    string? Sha256 = null);

public sealed record FileDeploymentBatchResult(bool Success, IReadOnlyList<FileDeploymentResult> Results, string? Error = null);
