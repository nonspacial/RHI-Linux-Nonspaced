using System.Diagnostics;
using System.IO.Compression;

namespace RHI.Platform.Linux.Deployment;

public interface IReShadeArchiveExtractor
{
    Task ExtractDllsAsync(string setupExe, string outputDirectory, CancellationToken cancellationToken = default);
}

public sealed class ReShadeArchiveExtractor : IReShadeArchiveExtractor
{
    public async Task ExtractDllsAsync(string setupExe, string outputDirectory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        try
        {
            using var archive = ZipFile.OpenRead(setupExe);
            var extracted = 0;
            foreach (var name in new[] { "ReShade64.dll", "ReShade32.dll" })
            {
                var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (entry is null) continue;
                entry.ExtractToFile(Path.Combine(outputDirectory, name), overwrite: true);
                extracted++;
            }
            if (extracted > 0) return;
        }
        catch (InvalidDataException) { }
        catch (IOException) { }

        var extractor = FindExternalExtractor();
        if (extractor is null)
            throw new InvalidOperationException("ReShade setup extraction needs 7z/7zz/7za or bsdtar. On CachyOS install package '7zip' if none is available.");

        var (command, kind) = extractor.Value;
        var psi = new ProcessStartInfo(command)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = outputDirectory
        };
        if (kind == "7z")
        {
            psi.ArgumentList.Add("e");
            psi.ArgumentList.Add(setupExe);
            psi.ArgumentList.Add("ReShade64.dll");
            psi.ArgumentList.Add("ReShade32.dll");
            psi.ArgumentList.Add($"-o{outputDirectory}");
            psi.ArgumentList.Add("-y");
        }
        else
        {
            psi.ArgumentList.Add("-xf");
            psi.ArgumentList.Add(setupExe);
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(outputDirectory);
            psi.ArgumentList.Add("ReShade64.dll");
            psi.ArgumentList.Add("ReShade32.dll");
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {command}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        _ = await stdoutTask.ConfigureAwait(false);
        var error = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{command} could not extract ReShade setup (exit {process.ExitCode}): {error.Trim()}");
    }

    internal static (string Command, string Kind)? FindExternalExtractor()
    {
        foreach (var candidate in new[] { "7zz", "7z", "7za" })
            if (FindInPath(candidate) is { } path) return (path, "7z");
        if (FindInPath("bsdtar") is { } bsdtar) return (bsdtar, "bsdtar");
        return null;
    }

    private static string? FindInPath(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, command);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }
}
