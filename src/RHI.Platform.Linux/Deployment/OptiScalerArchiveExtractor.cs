namespace RHI.Platform.Linux.Deployment;

public interface IOptiScalerArchiveExtractor
{
    Task ExtractAllAsync(string archivePath, string outputDirectory, CancellationToken cancellationToken = default);
}

/// <summary>OptiScaler ships .7z archives: extracted by the host tool through the shared runner (time limit, link check; #37).</summary>
public sealed class OptiScalerArchiveExtractor : IOptiScalerArchiveExtractor
{
    public Task ExtractAllAsync(string archivePath, string outputDirectory, CancellationToken cancellationToken = default)
        => ExternalArchiveRunner.ExtractAsync(archivePath, outputDirectory, cancellationToken);
}
