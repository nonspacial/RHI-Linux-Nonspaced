namespace RHI.Core.Services;

/// <summary>Small binary-string classifier used for conservative foreign-DLL protection.</summary>
public static class BinarySignatureInspector
{
    /// <summary>
    /// ReShade by its strings. A file whose version resource names OptiScaler is never ReShade: OptiScaler's own DLL
    /// carries "ReShade" (it can load ReShade), and reading it as ReShade sent an OptiScaler proxy down the
    /// external-ReShade refusal and blocked ReShade beside an RHI-managed OptiScaler (#154).
    /// </summary>
    public static bool LooksLikeReShade(string path)
    {
        if (!ContainsAny(path, "ReShade", "reshade.me", "crosire")) return false;
        // RTX Encore's proxies also mention ReShade (#158): their own resource rules ReShade out, as OptiScaler's does.
        var version = PeVersionReader.Read(path);
        return !IsOptiScalerVersionResource(version) && !IsRtxEncoreVersionResource(version);
    }

    /// <summary>
    /// RTX Encore (SilyNoMeta/rtx-encore, #158) by its PE version resource: every build checked (v1.0.0-rc.3 main DLL, its
    /// pre-named proxies and the ASI) writes <c>CompanyName = SilyNoMeta</c> and a <c>FileDescription</c> starting
    /// "RTX Encore". Its body mentions both ReShade and OptiScaler, so strings never identify it.
    /// </summary>
    public static bool LooksLikeRtxEncore(string path) => IsRtxEncoreVersionResource(PeVersionReader.Read(path));

    /// <summary>True for RTX Encore's version resource (<c>CompanyName</c> SilyNoMeta, description "RTX Encore…").</summary>
    public static bool IsRtxEncoreVersionResource(PeVersionInfo? version)
        => version is not null
           && string.Equals(version.CompanyName?.Trim(), "SilyNoMeta", StringComparison.OrdinalIgnoreCase)
           && version.FileDescription?.TrimStart().StartsWith("RTX Encore", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// OptiScaler identification (#154, upstream 2.8.6/2.8.7 7920ae6, 5031d97). The PE version resource decides when
    /// the file has one: OptiScaler always writes <c>FileDescription = OptiScaler</c> and <c>CompanyName = nitec</c>,
    /// and a resource naming anything else (RTX Encore's proxies say SilyNoMeta, yet mention "OptiScaler") is not
    /// OptiScaler. Only a file without a version resource falls back to strings, and only to ones OptiScaler's own
    /// binary carries (its GitHub path and its registry escape key), never the bare name other tools mention. A file
    /// this rejects is not trusted as anything: install paths still refuse it as a foreign DLL.
    /// </summary>
    public static bool LooksLikeOptiScaler(string path)
    {
        var version = PeVersionReader.Read(path);
        return version is not null
            ? IsOptiScalerVersionResource(version)
            : ContainsAny(path, OptiScalerOwnStrings);
    }

    /// <summary>True for OptiScaler's version resource (<c>FileDescription</c> "OptiScaler", <c>CompanyName</c> "nitec").</summary>
    public static bool IsOptiScalerVersionResource(PeVersionInfo? version)
        => version is not null
           && string.Equals(version.FileDescription, "OptiScaler", StringComparison.OrdinalIgnoreCase)
           && string.Equals(version.CompanyName, "nitec", StringComparison.OrdinalIgnoreCase);

    // Upstream also matches "optiscaler.dll" case-sensitively; the shared search ignores ASCII case, which turns that
    // needle into the common "OptiScaler.dll" any tool can mention, so it is left out. Both real builds checked
    // (OptiScaler 0.9.4, F5 DLSSNR 0.1.30) carry "optiscaler/OptiScaler".
    private static readonly string[] OptiScalerOwnStrings = ["optiscaler/OptiScaler", "OptiScaler_DontLoad"];

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
