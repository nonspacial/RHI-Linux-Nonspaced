using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Hdr;

namespace RHI.Core.Tests;

/// <summary>
/// w11/nr-gpu test kit: the live DLSS manifest's three Neural Rendering entries (as on 2026-10-22, newest first), each
/// served as a zip holding an <c>nvngx_dlssnr.dll</c> with its own PE version resource, fake NVIDIA facts and fake /proc
/// trees with real "Model:" strings. Never the real /proc, never a real download.
/// </summary>
internal static class NrGpuTestKit
{
    public const string For50 = "310.8.3 (50xx)";
    public const string ForAll = "310.8.2 (20/30/40/50)";
    public const string Old50 = "310.8.0 (50xx)";

    /// <summary>The live manifest's dlssnr list, in its order (newest first).</summary>
    public static readonly string[] LiveVersions = [For50, ForAll, Old50];

    public static string UrlOf(string version) => version switch
    {
        For50 => "https://github.com/rhi-tests/nr/releases/download/1/nr-3103.zip",
        ForAll => "https://github.com/rhi-tests/nr/releases/download/1/nr-3102.zip",
        Old50 => "https://github.com/rhi-tests/nr/releases/download/1/nr-3100.zip",
        _ => "https://github.com/rhi-tests/nr/releases/download/1/nr-" + new string(version.Where(char.IsLetterOrDigit).ToArray()) + ".zip"
    };

    /// <summary>The PE FileVersion string of each fixture DLL (commas, as NVIDIA writes them).</summary>
    public static string PeVersionOf(string version) => version.Split(' ')[0].Replace('.', ',') + ",0";

    private static readonly ConcurrentDictionary<string, byte[]> Dlls = new();

    /// <summary>The fixture DLL of one manifest version: a 64-bit PE with that version in its resource, 80 KB.</summary>
    public static byte[] Dll(string version)
        => Dlls.GetOrAdd(version, v => PeVersionReaderTests.BuildVersionedPe(PeVersionOf(v), PeVersionOf(v), size: 80_000,
            bodyText: "nvngx_dlssnr " + v));

    /// <summary>One Super Resolution entry (310.9.16) beside the NR list, for the paths that apply SR too.</summary>
    public const string SrVersion = "310.9.16";
    public const string SrUrl = "https://github.com/rhi-tests/nr/releases/download/1/sr-31016.zip";
    public static readonly byte[] SrDll = PeVersionReaderTests.BuildVersionedPe("310,9,16,0", "310,9,16,0", size: 90_000, bodyText: "nvngx_dlss");

    public static string ManifestJson(IEnumerable<string>? versions = null)
    {
        var entries = string.Join(",", (versions ?? LiveVersions).Select(v => $$"""{"version":"{{v}}","url":"{{UrlOf(v)}}"}"""));
        var sr = $$"""{"version":"{{SrVersion}}","url":"{{SrUrl}}"}""";
        return $$"""{"dlss":[{{sr}}],"dlssd":[],"dlssg":[],"dlssnr":[{{entries}}],"streamline":[]}""";
    }

    /// <summary>Answers the manifest and every NR zip; records every request (thread-safe).</summary>
    public sealed class Server(IEnumerable<string>? versions = null) : HttpMessageHandler
    {
        private readonly string[] _versions = (versions ?? LiveVersions).ToArray();
        public ConcurrentQueue<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Enqueue(url);
            if (url == DlssManagerService.ManifestUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ManifestJson(_versions)) });
            if (url == SrUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip("nvngx_dlss.dll", SrDll)) });
            var version = _versions.FirstOrDefault(v => UrlOf(v) == url);
            return Task.FromResult(version is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip("nvngx_dlssnr.dll", Dll(version))) });
        }

        public int DownloadsOf(string version) => Requests.Count(url => url == UrlOf(version));
    }

    public static byte[] Zip(string name, byte[] bytes)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(name);
            entry.LastWriteTime = new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
            using var stream = entry.Open();
            stream.Write(bytes);
        }
        return ms.ToArray();
    }

    /// <summary>NVIDIA facts for a card generation (and, for #231, a driver version).</summary>
    public static NvidiaGpuFacts Facts(RtxGeneration generation, string? driver = "615.78.08")
        => generation == RtxGeneration.Unknown && driver is null
            ? NvidiaGpuFacts.None
            : new NvidiaGpuFacts(true, generation == RtxGeneration.Rtx50, generation == RtxGeneration.Unknown ? null : $"NVIDIA GeForce RTX {(int)generation}90")
            {
                Generation = generation,
                DriverVersion = driver
            };

    /// <summary>No game is running (Update All defers running games).</summary>
    public sealed class NeverRunning : RHI.Platform.Linux.Deployment.IGameRunningProbe
    {
        public bool IsRunning(DetectedGame game) => false;
    }

    /// <summary>An HDR launch service over the same fake facts (its default reads the real /proc).</summary>
    public static LinuxHdrLaunchProfileService Hdr(NvidiaGpuFacts facts)
        => new(environment: _ => null, commandExists: _ => false, nvidiaVersion: () => facts.DriverVersion, nvidiaGpu: () => facts);

    /// <summary>A DLSS service over <paramref name="server"/> whose NR lists follow <paramref name="card"/>.</summary>
    public static DlssManagerService Dlss(HttpClient http, string cacheDir, RtxGeneration card)
        => new(http, new TransactionalFileDeploymentService(), cacheDir) { CardGeneration = () => card };

    /// <summary>A scratch tree shaped like /proc: one <c>gpus/&lt;bus&gt;/information</c> per model, plus the version file.</summary>
    public static string ProcTree(string root, string? versionText, params string[] models)
    {
        var proc = Path.Combine(root, "proc-" + Guid.NewGuid().ToString("N"));
        var nvidia = Directory.CreateDirectory(Path.Combine(proc, "driver", "nvidia")).FullName;
        if (versionText is not null) File.WriteAllText(Path.Combine(nvidia, "version"), versionText);
        for (var i = 0; i < models.Length; i++)
        {
            var dir = Directory.CreateDirectory(Path.Combine(nvidia, "gpus", $"0000:0{i + 1}:00.0")).FullName;
            File.WriteAllText(Path.Combine(dir, "information"),
                $"Model: \t\t {models[i]}\nIRQ:   \t\t 70\nGPU UUID: \t GPU-0000\nVideo BIOS: \t 95.02.18.40.14\nBus Type: \t PCIe\n");
        }
        return proc;
    }

    /// <summary>The owner's open kernel module line (615.78.08) and a proprietary one.</summary>
    public const string OpenModuleVersion =
        "NVRM version: NVIDIA UNIX Open Kernel Module for x86_64  615.78.08  Release Build  (root@host)  \nGCC version:  gcc version 16.2.1 20260810 (GCC) \n";

    public static string ProprietaryModuleVersion(string version)
        => $"NVRM version: NVIDIA UNIX x86_64 Kernel Module  {version}  Thu Sep  4 01:44:30 UTC 2026\nGCC version:  gcc version 15.2.1 20250813 (GCC) \n";

    public static string Utf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}
