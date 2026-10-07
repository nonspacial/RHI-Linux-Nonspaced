using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.Versioning;
using System.Windows.Input;
using RHI.Core.Models;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>Release contracts formerly grepped from source by validate.sh, kept as behaviour checks.</summary>
public sealed class ReleaseContractTests
{
    [Fact]
    public void ProductVersion_IsTheBuiltVersion_WithoutBuildMetadata()
    {
        var informational = typeof(RhiProductInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        Assert.False(string.IsNullOrWhiteSpace(RhiProductInfo.Version));
        Assert.DoesNotContain('+', RhiProductInfo.Version);
        Assert.StartsWith(RhiProductInfo.Version, informational);
        Assert.Equal("RHI-Linux/" + RhiProductInfo.Version, RhiProductInfo.UserAgent);
        Assert.True(ProductInfoHeaderValue.TryParse(RhiProductInfo.UserAgent, out var parsed));
        Assert.Equal(RhiProductInfo.Version, parsed!.Product!.Version);
    }

    [Theory]
    [InlineData("1.2.3-beta.1+0ece748", "1.2.3-beta.1")]
    [InlineData("1.2.3", "1.2.3")]
    public void NormalizeVersion_DropsBuildMetadata(string informational, string expected)
        => Assert.Equal(expected, RhiProductInfo.NormalizeVersion(informational));

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void NormalizeVersion_RefusesAMissingVersion(string? informational)
        => Assert.Throws<InvalidOperationException>(() => RhiProductInfo.NormalizeVersion(informational));

    [Fact]
    public void UpstreamFallbackManifest_IsPinnedToTheFullCommit()
    {
        Assert.Matches("^[0-9a-f]{40}$", UpstreamRhiManifestService.PinnedManifestCommit);
        Assert.Equal(
            $"https://raw.githubusercontent.com/RankFTW/RHI/{UpstreamRhiManifestService.PinnedManifestCommit}/manifest.json",
            UpstreamRhiManifestService.PinnedManifestUrl);
    }

    [Theory]
    [InlineData(ManagedComponentKind.Dlss)]
    [InlineData(ManagedComponentKind.Streamline)]
    public void DlssAndStreamlineRows_HaveNoInstallAction(ManagedComponentKind kind)
    {
        var row = new ComponentRowViewModel(new ManagedComponent { Kind = kind, DisplayName = kind.ToString() }, new NoOpCommand());

        Assert.False(row.HasAction);
        Assert.Equal(string.Empty, row.Action);
    }

    [Fact]
    public void InstallableRows_KeepTheirAction()
        => Assert.True(new ComponentRowViewModel(new ManagedComponent { Kind = ManagedComponentKind.ReShade, DisplayName = "ReShade" }, new NoOpCommand()).HasAction);

    [Fact]
    public void LinuxOnlyAssemblies_DeclareLinux_SoCa1416CannotBreakTheWarningGate()
    {
        foreach (var assembly in new[] { typeof(ReShadeArchiveExtractor).Assembly, typeof(ComponentRowViewModel).Assembly, typeof(ReleaseContractTests).Assembly })
            Assert.Contains(assembly.GetCustomAttributes<SupportedOSPlatformAttribute>(), a => a.PlatformName == "linux");
    }

    [Fact]
    public void NoSourceFile_WalksAllDirectories_RecursiveWalksGoThroughSafeFileEnumeration()
    {
        // The framework's recursive search follows directory symlinks (a Wine prefix's z: -> /), so every
        // recursive walk in src/ must use SafeFileEnumeration (#64).
        Assert.Empty(SourceFilesContaining("SearchOption.AllDirectories"));
    }

    [Fact]
    public void UnhandledExceptionHooks_AreInstalledOnlyByUiExceptionGuard()
    {
        // One installer, one sink (the app log): two sets of handlers logged every crash twice to two files.
        foreach (var hook in new[] { "UnhandledException +=", "UnobservedTaskException +=" })
            Assert.Equal(["UiExceptionGuard.cs"], SourceFilesContaining(hook).Select(Path.GetFileName));
    }

    private static string[] SourceFilesContaining(string text)
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "RHI-Linux.sln")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("repository root not found");
        var obj = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";
        return Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", new EnumerationOptions { RecurseSubdirectories = true })
            .Where(file => !file.Contains(obj, StringComparison.Ordinal))
            .Where(file => File.ReadAllText(file).Contains(text, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private sealed class NoOpCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) { }
    }
}
