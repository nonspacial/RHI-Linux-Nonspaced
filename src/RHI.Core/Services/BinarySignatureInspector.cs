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

    /// <summary>Bounded streaming search (no whole-file buffers, early exit, cached per file version; #34).</summary>
    private static bool ContainsAny(string path, params string[] needles) => BinaryStringSearch.ContainsAny(path, needles);
}
