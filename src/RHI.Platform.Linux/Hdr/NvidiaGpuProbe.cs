using System.Text.RegularExpressions;

namespace RHI.Platform.Linux.Hdr;

/// <summary>
/// #211: what the host's NVIDIA GPU allows. <see cref="Present"/>: an NVIDIA GPU is in the machine;
/// <see cref="Rtx50"/>: one is an RTX 50-series (Blackwell) card, the only ones multi frame generation runs on.
/// </summary>
public sealed record NvidiaGpuFacts(bool Present, bool Rtx50, string? Model = null)
{
    public static NvidiaGpuFacts None { get; } = new(false, false);

    /// <summary>
    /// DLSS Frame Generation runs here (RTX 40 or newer): false only for a model known to be older (RTX 20/30, GTX,
    /// Quadro, TITAN, RTX A-series); an unknown model keeps it.
    /// </summary>
    public bool FrameGeneration => Present && (Rtx50 || !NvidiaGpuProbe.IsBeforeFrameGeneration(Model));
}

/// <summary>
/// #211: a read-only look at the GPUs: the NVIDIA driver's <c>/proc/driver/nvidia/gpus/*/information</c> (model
/// names), else — only while NVIDIA's own driver is loaded (<c>/proc/driver/nvidia/version</c>) — a PCI display device
/// of vendor 0x10de under <c>/sys/bus/pci/devices</c> (no model; RTX 50 then unknown, so multi frame generation stays
/// hidden). An NVIDIA card on nouveau/NVK counts as none: DLSS needs NVIDIA's driver. Never runs <c>nvidia-smi</c> and
/// never opens a GPU device.
/// </summary>
public static partial class NvidiaGpuProbe
{
    public const string NvidiaVendorId = "0x10de";

    public static NvidiaGpuFacts Read() => Read("/proc", "/sys");

    /// <summary>The probe over another root (tests point it at a scratch tree shaped like /proc and /sys).</summary>
    public static NvidiaGpuFacts Read(string procRoot, string sysRoot)
    {
        var models = ProcModels(procRoot);
        if (models.Count > 0)
        {
            var rtx50 = models.FirstOrDefault(IsRtx50);
            return new NvidiaGpuFacts(true, rtx50 is not null, rtx50 ?? models[0]);
        }
        // Without NVIDIA's driver (nouveau/NVK) there is no DLSS: the PCI device alone is not enough.
        if (!File.Exists(Path.Combine(procRoot, "driver", "nvidia", "version"))) return NvidiaGpuFacts.None;
        return HasPciNvidiaDisplay(sysRoot) ? new NvidiaGpuFacts(true, false) : NvidiaGpuFacts.None;
    }

    /// <summary>
    /// A model known to predate DLSS Frame Generation (RTX 20/30, GTX, Quadro, TITAN, RTX A-series). Ada and Blackwell
    /// workstation cards ("RTX 2000 Ada", "RTX PRO 6000 Blackwell") are not; "Quadro RTX 4000" (Turing) is.
    /// </summary>
    public static bool IsBeforeFrameGeneration(string? model)
        => !string.IsNullOrWhiteSpace(model) && !AdaOrNewerRegex().IsMatch(model) && BeforeAdaRegex().IsMatch(model);

    /// <summary>An RTX 50-series (Blackwell) model name: "NVIDIA GeForce RTX 5080", "… RTX 5070 Ti Laptop GPU", "RTX PRO 6000 Blackwell …".</summary>
    public static bool IsRtx50(string? model)
        => !string.IsNullOrWhiteSpace(model) && Rtx50Regex().IsMatch(model);

    private static List<string> ProcModels(string procRoot)
    {
        var models = new List<string>();
        var gpus = Path.Combine(procRoot, "driver", "nvidia", "gpus");
        try
        {
            if (!Directory.Exists(gpus)) return models;
            foreach (var dir in Directory.EnumerateDirectories(gpus).Order(StringComparer.Ordinal))
            {
                var info = Path.Combine(dir, "information");
                if (!File.Exists(info)) continue;
                foreach (var line in File.ReadLines(info))
                {
                    if (!line.StartsWith("Model:", StringComparison.OrdinalIgnoreCase)) continue;
                    var model = line["Model:".Length..].Trim();
                    if (model.Length > 0) models.Add(model);
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RHI.Core.Services.AppLog.Warn("nvidia", "NVIDIA GPU information unreadable", ex);
        }
        return models;
    }

    private static bool HasPciNvidiaDisplay(string sysRoot)
    {
        var devices = Path.Combine(sysRoot, "bus", "pci", "devices");
        try
        {
            if (!Directory.Exists(devices)) return false;
            foreach (var dir in Directory.EnumerateDirectories(devices))
            {
                var vendor = ReadSmall(Path.Combine(dir, "vendor"));
                if (!string.Equals(vendor, NvidiaVendorId, StringComparison.OrdinalIgnoreCase)) continue;
                // PCI class 0x03xxxx = display controller (the card's audio function is 0x0403xx).
                if (ReadSmall(Path.Combine(dir, "class"))?.StartsWith("0x03", StringComparison.OrdinalIgnoreCase) == true) return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RHI.Core.Services.AppLog.Warn("nvidia", "PCI devices unreadable", ex);
        }
        return false;
    }

    private static string? ReadSmall(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var reader = new StreamReader(path);
            var buffer = new char[64];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, read).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"\bRTX\s*50[0-9]{2}\b|\bBlackwell\b", RegexOptions.IgnoreCase)]
    private static partial Regex Rtx50Regex();

    [GeneratedRegex(@"\bAda\b|\bBlackwell\b", RegexOptions.IgnoreCase)]
    private static partial Regex AdaOrNewerRegex();

    [GeneratedRegex(@"\bRTX\s*[23]0[0-9]{2}\b|\bRTX\s*A[0-9]{3,4}\b|\bGTX\b|\bQuadro\b|\bTITAN\b", RegexOptions.IgnoreCase)]
    private static partial Regex BeforeAdaRegex();
}
