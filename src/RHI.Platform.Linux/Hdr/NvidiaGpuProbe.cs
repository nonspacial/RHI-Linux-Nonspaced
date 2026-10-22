using System.Text.RegularExpressions;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Hdr;

/// <summary>
/// #211: what the host's NVIDIA GPU allows. <see cref="Present"/>: an NVIDIA GPU is in the machine;
/// <see cref="Rtx50"/>: one is an RTX 50-series (Blackwell) card, the only ones multi frame generation runs on.
/// </summary>
public sealed record NvidiaGpuFacts(bool Present, bool Rtx50, string? Model = null)
{
    public static NvidiaGpuFacts None { get; } = new(false, false);

    /// <summary>
    /// #229: the card generation Neural Rendering builds are labelled for: with several NVIDIA cards the NEWEST
    /// generation present; unknown without NVIDIA's driver, without a model name, or for a model RHI does not know
    /// (a card from before RTX included: NR cannot run there, and RHI never blocks the mod).
    /// </summary>
    public RtxGeneration Generation { get; init; } = RtxGeneration.Unknown;

    /// <summary>#231: the loaded NVIDIA kernel module's version (<c>615.78.08</c>); null when not loaded or unreadable.</summary>
    public string? DriverVersion { get; init; }

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
        var driver = ReadDriverVersion(procRoot);
        var models = ProcModels(procRoot);
        if (models.Count > 0)
        {
            // Re-review 5: with several GPUs, any RTX 50 decides, then any model that runs frame generation — never the
            // enumeration order (an RTX 30 listed before an RTX 40 hid the FG preset).
            var rtx50 = models.FirstOrDefault(IsRtx50);
            return new NvidiaGpuFacts(true, rtx50 is not null, rtx50 ?? models.FirstOrDefault(model => !IsBeforeFrameGeneration(model)) ?? models[0])
            {
                // #229: several cards = the newest generation present, never the enumeration order.
                Generation = models.Select(GenerationOf).Max(),
                DriverVersion = driver
            };
        }
        // Without NVIDIA's driver (nouveau/NVK) there is no DLSS: the PCI device alone is not enough.
        if (!File.Exists(Path.Combine(procRoot, "driver", "nvidia", "version"))) return NvidiaGpuFacts.None;
        return HasPciNvidiaDisplay(sysRoot) ? new NvidiaGpuFacts(true, false) { DriverVersion = driver } : NvidiaGpuFacts.None with { DriverVersion = driver };
    }

    /// <summary>
    /// #229: the RTX generation of one model name. GeForce by number (<c>RTX 4090</c>, <c>RTX 3060 Ti Laptop GPU</c>);
    /// workstation cards by family: Blackwell = 50, Ada = 40, Ampere <c>RTX A4000</c> = 30, Turing <c>Quadro RTX 5000</c> /
    /// <c>TITAN RTX</c> = 20 — never <c>RTX\s*50\d\d</c> alone (it matches Quadro RTX 5000 and RTX 5000 Ada). Anything
    /// else (GTX, TITAN X, Tesla, an unknown name) is unknown.
    /// </summary>
    public static RtxGeneration GenerationOf(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return RtxGeneration.Unknown;
        foreach (var (pattern, generation) in NumberIsNotTheArchitecture)
            if (pattern.IsMatch(model)) return generation;
        if (BlackwellRegex().IsMatch(model)) return RtxGeneration.Rtx50;
        if (AdaRegex().IsMatch(model)) return RtxGeneration.Rtx40;
        if (AmpereWorkstationRegex().IsMatch(model)) return RtxGeneration.Rtx30;
        if (TuringWorkstationRegex().IsMatch(model)) return RtxGeneration.Rtx20;
        var geforce = GeForceRtxRegex().Match(model);
        return geforce.Success
            ? geforce.Groups[1].Value switch
            {
                "2" => RtxGeneration.Rtx20,
                "3" => RtxGeneration.Rtx30,
                "4" => RtxGeneration.Rtx40,
                "5" => RtxGeneration.Rtx50,
                _ => RtxGeneration.Unknown
            }
            : RtxGeneration.Unknown;
    }

    /// <summary>
    /// GeForce cards whose number names another generation than their chip (review nit 5); checked before the number rule:
    /// the laptop RTX 2050 is an Ampere chip (GA107) = RTX 30, the RTX 3050 A Laptop GPU an Ada chip (AD106) = RTX 40.
    /// NR builds are labelled by architecture, so the chip decides. Add a card here only with NVIDIA's own spec page.
    /// </summary>
    private static readonly (Regex Pattern, RtxGeneration Generation)[] NumberIsNotTheArchitecture =
    [
        (new Regex(@"\bRTX\s*2050\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), RtxGeneration.Rtx30),
        (new Regex(@"\bRTX\s*3050\s+A\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), RtxGeneration.Rtx40),
    ];

    /// <summary>
    /// #231 (moved here from <c>LinuxHdrLaunchProfileService</c>, one reader): the loaded NVIDIA kernel module's version
    /// from <c>/proc/driver/nvidia/version</c>, read-only; null when NVIDIA's driver is not loaded or the file cannot be
    /// read. Never <c>nvidia-smi</c>, never a GPU device.
    /// </summary>
    public static string? ReadDriverVersion() => ReadDriverVersion("/proc");

    /// <summary>The driver version under another root (tests point it at a scratch tree shaped like /proc).</summary>
    public static string? ReadDriverVersion(string procRoot)
    {
        try
        {
            var path = Path.Combine(procRoot, "driver", "nvidia", "version");
            if (!File.Exists(path)) return null;
            using var reader = new StreamReader(path);
            var buffer = new char[4096];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            return ParseDriverVersion(new string(buffer, 0, read));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RHI.Core.Services.AppLog.Warn("nvidia", "NVIDIA driver version unreadable", ex);
            return null;
        }
    }

    /// <summary>
    /// The version on the <c>NVRM version:</c> line of <c>/proc/driver/nvidia/version</c>: the proprietary module
    /// (<c>NVIDIA UNIX x86_64 Kernel Module  550.54.14  Thu …</c>) and the open one
    /// (<c>NVIDIA UNIX Open Kernel Module for x86_64  615.78.08  Release Build …</c>, which the old pattern missed).
    /// </summary>
    public static string? ParseDriverVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var line in text.Split('\n'))
        {
            if (!line.TrimStart().StartsWith("NVRM version:", StringComparison.OrdinalIgnoreCase)) continue;
            var match = DriverVersionRegex().Match(line);
            return match.Success ? match.Groups[1].Value : null;
        }
        return null;
    }

    /// <summary>
    /// A model known to predate DLSS Frame Generation (RTX 20/30, GTX, Quadro, TITAN, RTX A-series). Ada and Blackwell
    /// workstation cards ("RTX 2000 Ada", "RTX PRO 6000 Blackwell") are not; "Quadro RTX 4000" (Turing) is. w11 follow-up
    /// E4: a GeForce whose number names another generation than its chip follows the chip, as <see cref="GenerationOf"/>
    /// does (the RTX 3050 A Laptop GPU is Ada: frame generation runs there).
    /// </summary>
    public static bool IsBeforeFrameGeneration(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        foreach (var (pattern, generation) in NumberIsNotTheArchitecture)
            if (pattern.IsMatch(model)) return generation < RtxGeneration.Rtx40;
        return !AdaOrNewerRegex().IsMatch(model) && BeforeAdaRegex().IsMatch(model);
    }

    /// <summary>
    /// An RTX 50-series (Blackwell) model name: "NVIDIA GeForce RTX 5080", "… RTX 5070 Ti Laptop GPU", "RTX PRO 6000 Blackwell …".
    /// GeForce 5050–5090 by number; workstation cards only by the word Blackwell (re-review 1: "Quadro RTX 5000" is Turing
    /// and "NVIDIA RTX 5000 Ada Generation" is Ada).
    /// </summary>
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
                // One card's unreadable file drops only that card; the readable ones still count (review nit 4).
                try
                {
                    foreach (var line in File.ReadLines(info))
                    {
                        if (!line.StartsWith("Model:", StringComparison.OrdinalIgnoreCase)) continue;
                        var model = line["Model:".Length..].Trim();
                        if (model.Length > 0) models.Add(model);
                        break;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    RHI.Core.Services.AppLog.Warn("nvidia", $"NVIDIA GPU information unreadable, that card is left out: {info}", ex);
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

    // A dotted version of a 3+ digit branch standing on its own (not x86_64's "86", not a date).
    [GeneratedRegex(@"(?<![\w.])([0-9]{3,}\.[0-9]{1,4}(?:\.[0-9]{1,4})?)(?![\w.])")]
    private static partial Regex DriverVersionRegex();

    [GeneratedRegex(@"\bBlackwell\b", RegexOptions.IgnoreCase)]
    private static partial Regex BlackwellRegex();

    [GeneratedRegex(@"\bAda\b", RegexOptions.IgnoreCase)]
    private static partial Regex AdaRegex();

    [GeneratedRegex(@"\bRTX\s*A[0-9]{3,4}\b", RegexOptions.IgnoreCase)]
    private static partial Regex AmpereWorkstationRegex();

    [GeneratedRegex(@"\bQuadro\s+RTX\b|\bTITAN\s+RTX\b", RegexOptions.IgnoreCase)]
    private static partial Regex TuringWorkstationRegex();

    // GeForce RTX x050–x090 (with Ti/Super/Laptop/D after it); the workstation "RTX 4000"/"RTX 5000" names never match.
    [GeneratedRegex(@"\bRTX\s*([2-5])0[5-9]0\b", RegexOptions.IgnoreCase)]
    private static partial Regex GeForceRtxRegex();

    [GeneratedRegex(@"\bRTX\s*50[5-9]0\b|\bBlackwell\b", RegexOptions.IgnoreCase)]
    private static partial Regex Rtx50Regex();

    [GeneratedRegex(@"\bAda\b|\bBlackwell\b", RegexOptions.IgnoreCase)]
    private static partial Regex AdaOrNewerRegex();

    [GeneratedRegex(@"\bRTX\s*[23]0[0-9]{2}\b|\bRTX\s*A[0-9]{3,4}\b|\bGTX\b|\bQuadro\b|\bTITAN\b", RegexOptions.IgnoreCase)]
    private static partial Regex BeforeAdaRegex();
}
