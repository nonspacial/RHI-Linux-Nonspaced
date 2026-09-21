namespace RHI.Core.Services;

public interface IFileDeploymentService
{
    Task<FileDeploymentResult> ReplaceAsync(string sourcePath, string targetPath, CancellationToken cancellationToken = default);
    Task<FileDeploymentResult> RestoreAsync(string targetPath, CancellationToken cancellationToken = default);
    Task<FileDeploymentBatchResult> ReplaceBatchAsync(IEnumerable<(string Source, string Target)> files, CancellationToken cancellationToken = default);
    Task<FileDeploymentBatchResult> RestoreBatchAsync(IEnumerable<string> targetPaths, CancellationToken cancellationToken = default);
}

public sealed record FileDeploymentResult(
    bool Success,
    string TargetPath,
    string? BackupPath,
    string? Error = null,
    bool Changed = true);

public sealed record FileDeploymentBatchResult(bool Success, IReadOnlyList<FileDeploymentResult> Results, string? Error = null);
