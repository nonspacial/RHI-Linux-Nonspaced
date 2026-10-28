using System.Diagnostics;

namespace RHI.Core.Tests;

/// <summary>
/// w12 final review 2 (nit 2): <c>packaging/build-packages.sh</c> refuses a pre-release version (one with a '-', such as
/// 2.8.8-beta.1) before it fetches, publishes or packages anything; a release version gets past that check. The script
/// runs from a copy in a temp tree with a stand-in <c>dotnet</c> that answers the version and a stand-in <c>curl</c> that
/// always fails, so no run reaches the network or the real repository.
/// </summary>
public sealed class PackagingScriptTests
{
    private const string PreReleaseRefusal =
        "is a pre-release: .deb/.rpm/.tar.gz are built for releases only (betas are installed builds)";

    [Theory]
    [InlineData("2.8.8-beta.1")]
    [InlineData("2.8.8-rc.2")]
    public async Task BuildPackages_RefusesAPreReleaseVersion_AndBuildsNothing(string version)
    {
        var (exitCode, stderr, outDir, root) = await RunAsync(version);
        try
        {
            Assert.Equal(1, exitCode);
            Assert.Equal($"ERROR: version {version} {PreReleaseRefusal}", stderr.Trim());
            Assert.Empty(Directory.EnumerateFileSystemEntries(outDir));
            Assert.False(Directory.Exists(Path.Combine(root, "artifacts", "package-tools")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // Negative control: a release version passes the check and stops only at the stand-in download.
    [Fact]
    public async Task BuildPackages_ReleaseVersion_GetsPastTheCheck()
    {
        var (exitCode, stderr, outDir, root) = await RunAsync("2.8.8");
        try
        {
            Assert.Equal(1, exitCode);
            Assert.DoesNotContain("pre-release", stderr, StringComparison.Ordinal);
            Assert.Contains("ERROR: download failed: https://github.com/goreleaser/nfpm/", stderr, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFiles(outDir));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<(int ExitCode, string Stderr, string OutDir, string Root)> RunAsync(string version)
    {
        var root = Path.Combine(Path.GetTempPath(), "rhi-build-packages-" + Guid.NewGuid().ToString("N"));
        var packaging = Directory.CreateDirectory(Path.Combine(root, "packaging")).FullName;
        File.Copy(Path.Combine(LocalizationKeyUsageTests.RepoRoot(), "packaging", "build-packages.sh"),
            Path.Combine(packaging, "build-packages.sh"));
        File.WriteAllText(Path.Combine(root, "Directory.Build.props"),
            $"<Project><PropertyGroup><Version>{version}</Version></PropertyGroup></Project>\n");
        var bin = Directory.CreateDirectory(Path.Combine(root, "fake-bin")).FullName;
        WriteTool(Path.Combine(bin, "dotnet"), $"#!/bin/sh\necho '{version}'\n");
        WriteTool(Path.Combine(bin, "curl"), "#!/bin/sh\nexit 22\n");
        var outDir = Path.Combine(root, "out");

        var start = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = root,
        };
        foreach (var arg in new[] { Path.Combine(packaging, "build-packages.sh"), "--out", outDir })
            start.ArgumentList.Add(arg);
        start.Environment["PATH"] = bin + ":" + Environment.GetEnvironmentVariable("PATH");
        start.Environment.Remove("RHI_PACKAGE_TOOLS_DIR");
        start.Environment.Remove("SOURCE_DATE_EPOCH");

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        await stdout;
        return (process.ExitCode, await stderr, outDir, root);
    }

    private static void WriteTool(string path, string text)
    {
        File.WriteAllText(path, text);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
