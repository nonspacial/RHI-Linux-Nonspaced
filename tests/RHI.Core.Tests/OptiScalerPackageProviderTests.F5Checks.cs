using System.Security.Cryptography;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #231 (wizardkof v0.9.25.0-beta.1 ValidatePackageRoot): the F5 DLSSNR package's forwarder must be a Windows DLL (MZ and
/// a PE header) and the package must hold its OptiScaler/ backend folder; otherwise it is refused before any game file
/// changes, with a plain line. Other channels are not checked.
/// </summary>
public sealed partial class OptiScalerPackageProviderTests
{
    private sealed class F5Extractor(bool forwarderIsPe, bool backend) : IOptiScalerArchiveExtractor
    {
        public Task ExtractAllAsync(string archivePath, string outputDirectory, CancellationToken cancellationToken = default)
        {
            var names = new List<string> { "OptiScaler.dll", "OptiScaler.ini", "nvngx.dll_dlssnr.dll" };
            if (backend) names.Add(@"OptiScaler\libxess.dll");
            new FlatBackslashExtractor(names.ToArray()).ExtractAllAsync(archivePath, outputDirectory, cancellationToken).GetAwaiter().GetResult();
            if (!forwarderIsPe)
            {
                // MZ only: no PE header (a stub, a truncated download, a text file renamed).
                var bytes = new byte[300_000];
                bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
                File.WriteAllBytes(Path.Combine(outputDirectory, "nvngx.dll_dlssnr.dll"), bytes);
            }
            return Task.CompletedTask;
        }
    }

    private async Task<Exception?> F5Async(string name, bool forwarderIsPe, bool backend)
    {
        var archive = CreateArchiveBytes();
        using var http = new HttpClient(new MapHandler(new()
        {
            [OptiScalerPackageProvider.F5ReleasesApi] = F5Release(("OptiScaler-DLSSNR-F5-0.1.30.zip", "https://github.com/rhi-tests/f5.zip", archive.LongLength,
                Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant())),
            ["https://github.com/rhi-tests/f5.zip"] = archive
        }));
        var provider = new OptiScalerPackageProvider(http, new F5Extractor(forwarderIsPe, backend), Path.Combine(_root, name));
        try
        {
            await provider.GetLatestAsync(OptiScalerVariant.F5);
            return null;
        }
        catch (InvalidOperationException ex) { return ex; }
    }

    [Fact]
    public async Task F5Package_WithAForwarderThatIsNotAWindowsDll_IsRefused()
        => Assert.Equal(OptiScalerPackageProvider.F5ForwarderNotPeMessage, (await F5Async("f5-notpe", forwarderIsPe: false, backend: true))?.Message);

    [Fact]
    public async Task F5Package_WithoutItsOptiScalerBackendFolder_IsRefused()
        => Assert.Equal(OptiScalerPackageProvider.F5BackendMissingMessage, (await F5Async("f5-nobackend", forwarderIsPe: true, backend: false))?.Message);

    [Fact]
    public async Task F5Package_WithBoth_IsAccepted()
        => Assert.Null(await F5Async("f5-fine", forwarderIsPe: true, backend: true));
}
