using System.IO.Compression;
using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Deployment;

/// <param name="Download">The verified release archive the D3D9.dll was extracted from (record metadata of the deploying component, #26).</param>
public sealed record DgVoodooPreparedPayload(
    string Version,
    string SourceUrl,
    string D3D9Path,
    string ConfigPath,
    VerifiedDownload? Download = null)
{
    /// <summary>Record metadata part name of the dgVoodoo2 download (<c>Download.dgVoodoo.Sha256</c>).</summary>
    public const string DownloadPart = "dgVoodoo";
}

/// <summary>
/// Stages the official dgVoodoo2 D3D9 wrapper used by the upstream RHI DX9 paths.
/// This service only prepares trusted payloads; component-specific ownership and rollback
/// stay with Luma/Feeder so shared use can be reference-aware.
/// </summary>
public sealed class DgVoodooDeploymentService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/dege-diosg/dgVoodoo2/releases/latest";
    private const string ZipPattern = @"^dgVoodoo2_.*\.zip$";
    private const long MinimumDllSize = 64 * 1024;

    private readonly HttpClient _http;
    private readonly GitHubReleaseAssetResolver _resolver;
    private readonly string _cacheDir;

    public DgVoodooDeploymentService(HttpClient http, string? cacheDir = null)
    {
        _http = http;
        _resolver = new GitHubReleaseAssetResolver(http);
        _cacheDir = cacheDir ?? Path.Combine(LinuxPaths.CacheHome, "rhi-linux", "dgvoodoo2");
    }

    internal static GitHubReleaseAssetQuery CreateLatestReleaseQuery()
        => new(LatestReleaseApi, AssetNamePattern: ZipPattern);

    public async Task<DgVoodooPreparedPayload> PrepareLatestAsync(
        PeArchitecture architecture,
        CancellationToken cancellationToken = default)
    {
        if (architecture is not (PeArchitecture.X86 or PeArchitecture.X64))
            throw new NotSupportedException($"dgVoodoo2 D3D9 staging requires x86/x64, not {architecture}.");
        var release = await _resolver.ResolveAsync(CreateLatestReleaseQuery(), cancellationToken).ConfigureAwait(false);
        return await PrepareAsync(release.Version, release.AssetUrl, architecture, release.AssetName, release.Sha256, cancellationToken).ConfigureAwait(false);
    }

    public Task<DgVoodooPreparedPayload> PrepareManifestVersionAsync(
        string version,
        string url,
        PeArchitecture architecture,
        CancellationToken cancellationToken = default)
    {
        if (!IsTrustedArchiveUrl(url))
            throw new InvalidDataException("dgVoodoo2 manifest URL is outside the official dege-diosg GitHub repository.");
        var assetName = Path.GetFileName(Uri.UnescapeDataString(new Uri(url).AbsolutePath));
        if (string.IsNullOrWhiteSpace(assetName) || !System.Text.RegularExpressions.Regex.IsMatch(assetName, ZipPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new InvalidDataException("dgVoodoo2 manifest URL does not reference the expected official ZIP asset.");
        return PrepareAsync(version, url, architecture, assetName, sha256: null, cancellationToken: cancellationToken);
    }

    private async Task<DgVoodooPreparedPayload> PrepareAsync(
        string version,
        string url,
        PeArchitecture architecture,
        string assetName,
        string? sha256,
        CancellationToken cancellationToken)
    {
        if (architecture is not (PeArchitecture.X86 or PeArchitecture.X64))
            throw new NotSupportedException($"dgVoodoo2 D3D9 staging requires x86/x64, not {architecture}.");
        var safeVersion = string.Concat(version.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_'));
        var versionDir = Path.Combine(_cacheDir, safeVersion);
        Directory.CreateDirectory(versionDir);
        var zipPath = Path.Combine(versionDir, assetName);
        // Reuse only an archive this service stamped for this URL, version and published digest (#142);
        // anything else is downloaded again under the dgVoodoo2 host policy and verified (#26, #42).
        var download = await TrustedDownload.EnsureVerifiedAsync(
            _http, url, zipPath, DownloadSources.DgVoodoo, version, sha256,
            validate: (temp, ct) =>
            {
                if (new FileInfo(temp).Length < 1024)
                    throw new InvalidDataException("Downloaded dgVoodoo2 archive is unexpectedly small.");
                return ReShadeArchiveExtractor.RequireReadableZip(temp, ct);
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var archDir = Path.Combine(versionDir, architecture == PeArchitecture.X86 ? "x86" : "x64");
        Directory.CreateDirectory(archDir);
        var d3d9 = Path.Combine(archDir, "D3D9.dll");
        await ExtractD3D9Async(zipPath, d3d9, architecture, cancellationToken).ConfigureAwait(false);
        var config = Path.Combine(archDir, "dgVoodoo.conf");
        AtomicFile.WriteAllText(config, BuildConfiguration(), new UTF8Encoding(false));
        return new DgVoodooPreparedPayload(version, url, d3d9, config, download);
    }

    internal static bool IsTrustedArchiveUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith("/dege-diosg/dgVoodoo2/releases/download/", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task<string> ExtractD3D9Async(string zipPath, string destination, PeArchitecture architecture, CancellationToken cancellationToken)
    {
        var expected = architecture switch
        {
            PeArchitecture.X86 => "MS/x86/D3D9.dll",
            PeArchitecture.X64 => "MS/x64/D3D9.dll",
            _ => throw new NotSupportedException($"Unsupported dgVoodoo2 architecture: {architecture}")
        };
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.Entries.FirstOrDefault(item =>
            item.FullName.Replace('\\', '/').TrimStart('/').Equals(expected, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"dgVoodoo2 archive does not contain {expected}.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await ReShadeArchiveExtractor.ExtractEntryOrThrowAsync(entry, destination, TrustedDownloadPolicy.AddonMaxBytes, cancellationToken).ConfigureAwait(false);
        if (!BinarySignatureInspector.HasPeSignature(destination, MinimumDllSize))
            throw new InvalidDataException($"Extracted dgVoodoo2 {expected} is not a plausible PE DLL.");
        return destination;
    }

    internal static string BuildConfiguration() =>
        "; dgVoodoo2 configuration — managed by RHI Linux\r\n" +
        "; DX9 is translated to D3D11 so ReShade/Luma/DLSS5 Feeder can run on the translated API.\r\n\r\n" +
        "[General]\r\n" +
        "OutputAPI = d3d11_fl11_0\r\n\r\n" +
        "[DirectX]\r\n" +
        "DisableAndPassThru = false\r\n" +
        "VideoCard = geforce_9800_gt\r\n" +
        "VRAM = 1024\r\n" +
        "dgVoodooWatermark = false\r\n";
}
