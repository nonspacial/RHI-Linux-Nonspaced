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

    /// <summary>
    /// True for a Wine/Proton built-in or placeholder DLL: winebuild writes "Wine builtin DLL" (or "Wine placeholder
    /// DLL" for a prefix stub) into the DOS stub at offset 0x40, case exactly so. Such a file can still carry a
    /// Microsoft-like version resource and the DLL's own name (Wine's d3dcompiler_47 does), so string evidence alone
    /// cannot tell it from Microsoft's binary, and size is not a guarantee either (an unstripped build is larger).
    /// </summary>
    public static bool IsWineBuiltinDll(string path)
    {
        try
        {
            Span<byte> head = stackalloc byte[WineMarkerWindow];
            using var stream = File.OpenRead(path);
            var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            var window = head[..read];
            return window.IndexOf("Wine builtin DLL"u8) >= 0 || window.IndexOf("Wine placeholder DLL"u8) >= 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // The DOS header and stub (the marker sits at 0x40; real PE headers start at 0x80 or later).
    private const int WineMarkerWindow = 1024;

    /// <summary>Bounded streaming search (no whole-file buffers, early exit, cached per file version; #34).</summary>
    private static bool ContainsAny(string path, params string[] needles) => BinaryStringSearch.ContainsAny(path, needles);
}
