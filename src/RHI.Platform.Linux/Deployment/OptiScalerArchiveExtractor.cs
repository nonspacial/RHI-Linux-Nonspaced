using System.Diagnostics;

namespace RHI.Platform.Linux.Deployment;

public interface IOptiScalerArchiveExtractor
{
    Task ExtractAllAsync(string archivePath, string outputDirectory, CancellationToken cancellationToken = default);
}

public sealed class OptiScalerArchiveExtractor : IOptiScalerArchiveExtractor
{
    public async Task ExtractAllAsync(string archivePath, string outputDirectory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var extractor = ReShadeArchiveExtractor.FindExternalExtractor();
        if (extractor is null)
            throw new InvalidOperationException("OptiScaler extraction needs 7z/7zz/7za or bsdtar. On CachyOS install package '7zip' if none is available.");

        var (command, kind) = extractor.Value;
        var psi = new ProcessStartInfo(command)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (kind == "7z")
        {
            psi.ArgumentList.Add("x");
            psi.ArgumentList.Add(archivePath);
            psi.ArgumentList.Add($"-o{outputDirectory}");
            psi.ArgumentList.Add("-y");
        }
        else
        {
            psi.ArgumentList.Add("-xf");
            psi.ArgumentList.Add(archivePath);
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(outputDirectory);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {command}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        _ = await stdoutTask.ConfigureAwait(false);
        var error = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{command} could not extract OptiScaler (exit {process.ExitCode}): {error.Trim()}");
    }
}
