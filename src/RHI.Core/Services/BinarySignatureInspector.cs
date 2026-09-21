using System.Text;

namespace RHI.Core.Services;

/// <summary>Small binary-string classifier used for conservative foreign-DLL protection.</summary>
public static class BinarySignatureInspector
{
    public static bool LooksLikeReShade(string path)
        => ContainsAny(path, "ReShade", "reshade.me", "crosire");

    public static bool LooksLikeOptiScaler(string path)
        => ContainsAny(path, "OptiScaler", "optiscaler", "OptiScaler.ini");

    public static bool LooksLikeRenoDx(string path)
        => Path.GetFileName(path).Contains("renodx", StringComparison.OrdinalIgnoreCase) ||
           ContainsAny(path, "RenoDX", "renodx");

    public static bool HasPeSignature(string path, long minimumSize = 1)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < Math.Max(2, minimumSize)) return false;
            return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
        }
        catch { return false; }
    }

    private static bool ContainsAny(string path, params string[] needles)
    {
        if (!File.Exists(path)) return false;
        try
        {
            // ReShade/OptiScaler identification strings are normally present in PE string tables.
            // Cap reads to 32 MiB so a malformed/huge file never blocks the UI indefinitely.
            const int maxBytes = 32 * 1024 * 1024;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = (int)Math.Min(stream.Length, maxBytes);
            var bytes = new byte[length];
            var read = 0;
            while (read < length)
            {
                var n = stream.Read(bytes, read, length - read);
                if (n <= 0) break;
                read += n;
            }
            var ascii = Encoding.Latin1.GetString(bytes, 0, read);
            foreach (var needle in needles)
                if (ascii.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
            var unicode = Encoding.Unicode.GetString(bytes, 0, read - (read % 2));
            return needles.Any(n => unicode.Contains(n, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }
}
