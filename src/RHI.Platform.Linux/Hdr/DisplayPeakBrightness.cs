using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RHI.Platform.Linux.Paths;

namespace RHI.Platform.Linux.Hdr;

/// <summary>Where a monitor's peak brightness came from.</summary>
public enum DisplayPeakSource
{
    /// <summary>The monitor's EDID (HDR static metadata).</summary>
    Monitor,
    /// <summary>The peak brightness the user set for that monitor in KDE's display settings.</summary>
    KdeDisplaySettings
}

/// <summary>A connected monitor's HDR peak brightness.</summary>
/// <param name="Connector">The DRM connector (e.g. <c>DP-1</c>, <c>HDMI-A-1</c>).</param>
/// <param name="Name">The monitor's own name from its EDID, or null.</param>
/// <param name="PeakNits">Peak brightness in whole nits (cd/m²).</param>
public sealed record DisplayPeak(string Connector, string? Name, int PeakNits, DisplayPeakSource Source)
{
    public string Label => string.IsNullOrWhiteSpace(Name) ? Connector : $"{Name} ({Connector})";
}

/// <summary>
/// #182 "Auto" peak brightness (upstream 2.8.7 <c>PeakNitsAuto_Click</c> reads Windows' <c>DisplayMonitor.MaxLuminanceInNits</c>):
/// on Linux the same value is the "desired content max luminance" of the HDR Static Metadata Data Block in the
/// CTA-861 extension of each connected monitor's EDID (<c>/sys/class/drm/card*-*/edid</c>, read-only). Luminance is
/// coded as <c>50 · 2^(CV/32)</c> cd/m² (CTA-861-G 7.5.13). Many TVs declare HDR without a luminance (an LG/Samsung
/// OLED's block often stops after the EOTF bytes), so when KDE's display settings hold a peak brightness the user set
/// for that very monitor (<c>~/.config/kwinoutputconfig.json</c>, <c>maxPeakBrightnessOverride</c>, matched by the
/// EDID's MD5 as KWin keys it), that value wins: it is what the desktop already uses for HDR on that screen. A monitor
/// with neither is not listed.
/// </summary>
public static class DisplayPeakBrightness
{
    /// <summary>The kernel's DRM class folder.</summary>
    public const string SysfsRoot = "/sys/class/drm";

    /// <summary>EDIDs are 128-byte blocks; the kernel exposes at most a few (DisplayID can add more).</summary>
    private const int MaxEdidBytes = 32 * 1024;

    /// <summary>KWin's per-output settings file (read-only).</summary>
    public static string KwinOutputConfigPath => Path.Combine(LinuxPaths.ConfigHome, "kwinoutputconfig.json");

    /// <summary>Every connected monitor with a known HDR peak, by connector name.</summary>
    /// <param name="root">Test seam: a folder laid out like <c>/sys/class/drm</c>.</param>
    /// <param name="kwinConfig">Test seam: KWin's output settings file (default <see cref="KwinOutputConfigPath"/>).</param>
    public static IReadOnlyList<DisplayPeak> Read(string root = SysfsRoot, string? kwinConfig = null)
    {
        var result = new List<DisplayPeak>();
        var kdeOverrides = KdePeakOverrides(kwinConfig ?? KwinOutputConfigPath);
        if (!Directory.Exists(root)) return result;
        IEnumerable<string> connectors;
        try { connectors = Directory.EnumerateDirectories(root, "card*-*").Order(StringComparer.Ordinal).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return result; }
        foreach (var connector in connectors)
        {
            try
            {
                var status = Path.Combine(connector, "status");
                if (File.Exists(status) && !File.ReadAllText(status).Trim().Equals("connected", StringComparison.Ordinal)) continue;
                var edidPath = Path.Combine(connector, "edid");
                if (!File.Exists(edidPath)) continue;
                var edid = ReadBounded(edidPath);
                if (edid.Length < 128) continue;
                var folder = Path.GetFileName(connector);
                var dash = folder.IndexOf('-');
                var name = dash >= 0 ? folder[(dash + 1)..] : folder;
                var hash = Convert.ToHexString(MD5.HashData(edid)).ToLowerInvariant();
                if (kdeOverrides.TryGetValue(hash, out var kde))
                    result.Add(new DisplayPeak(name, MonitorName(edid), kde, DisplayPeakSource.KdeDisplaySettings));
                else if (PeakNits(edid) is { } nits)
                    result.Add(new DisplayPeak(name, MonitorName(edid), nits, DisplayPeakSource.Monitor));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // One unreadable connector never hides the others.
            }
        }
        return result;
    }

    /// <summary>The HDR static metadata's desired content max luminance in whole nits, or null when the EDID has none.</summary>
    public static int? PeakNits(ReadOnlySpan<byte> edid)
    {
        if (edid.Length < 256) return null;
        for (var block = 128; block + 128 <= edid.Length; block += 128)
        {
            var ext = edid.Slice(block, 128);
            if (ext[0] != 0x02) continue; // CTA-861 extension
            var dtdStart = ext[2];
            if (dtdStart < 4 || dtdStart > 127) dtdStart = 127;
            for (var i = 4; i < dtdStart;)
            {
                var header = ext[i];
                var tag = header >> 5;
                var length = header & 0x1F;
                if (i + 1 + length > dtdStart) break;
                // Extended tag (7) with extended tag code 6 = HDR Static Metadata Data Block:
                // [6][EOTFs][metadata types][max luminance CV][max frame-average CV][min luminance CV]
                if (tag == 7 && length >= 4 && ext[i + 1] == 0x06)
                {
                    var cv = ext[i + 4];
                    if (cv == 0) return null; // "not indicated"
                    return Clamp(50 * Math.Pow(2, cv / 32.0));
                }
                i += 1 + length;
            }
        }
        return null;
    }

    /// <summary>The monitor name descriptor (0xFC) of the base block, or null.</summary>
    public static string? MonitorName(ReadOnlySpan<byte> edid)
    {
        if (edid.Length < 128) return null;
        for (var offset = 54; offset + 18 <= 126; offset += 18)
        {
            var d = edid.Slice(offset, 18);
            if (d[0] != 0 || d[1] != 0 || d[2] != 0 || d[3] != 0xFC) continue;
            var text = Encoding.ASCII.GetString(d[5..]);
            var end = text.IndexOf('\n');
            var name = (end >= 0 ? text[..end] : text).Trim();
            return name.Length == 0 || name.Any(ch => ch < 0x20 || ch > 0x7E) ? null : name;
        }
        return null;
    }

    /// <summary>KWin's <c>maxPeakBrightnessOverride</c> per EDID hash (lower-case MD5), from its output settings file.</summary>
    internal static IReadOnlyDictionary<string, int> KdePeakOverrides(string path)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024) return result;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var section in document.RootElement.EnumerateArray())
            {
                if (section.ValueKind != JsonValueKind.Object || !section.TryGetProperty("name", out var sectionName)
                    || sectionName.ValueKind != JsonValueKind.String || sectionName.GetString() != "outputs"
                    || !section.TryGetProperty("data", out var outputs) || outputs.ValueKind != JsonValueKind.Array) continue;
                foreach (var output in outputs.EnumerateArray())
                {
                    if (output.ValueKind != JsonValueKind.Object) continue;
                    if (!output.TryGetProperty("edidHash", out var hash) || hash.ValueKind != JsonValueKind.String) continue;
                    if (!output.TryGetProperty("maxPeakBrightnessOverride", out var peak) || peak.ValueKind != JsonValueKind.Number) continue;
                    if (peak.TryGetDouble(out var nits) && nits is >= 1 and <= 100_000 && hash.GetString() is { Length: > 0 } key)
                        result[key] = Clamp(nits);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // KDE's file is a hint; without it the EDID still answers.
        }
        return result;
    }

    /// <summary>
    /// Whole nits, at most what the peak brightness box and RenoDX's presets take (<see cref="RHI.Platform.Linux.Deployment.RenoDxPeakNits.MaxNits"/>),
    /// so the monitor line shows the value Auto puts in the box (review nit 11).
    /// </summary>
    private static int Clamp(double nits) => (int)Math.Min(Math.Round(nits), RHI.Platform.Linux.Deployment.RenoDxPeakNits.MaxNits);

    private static byte[] ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[MaxEdidBytes];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0) total += read;
        return buffer[..total];
    }
}
