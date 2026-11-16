namespace RHI.Core.Services;

/// <summary>
/// #284 (upstream 2.9.0 <c>HookedDllNames</c>): "&lt;name&gt;Hooked.dll" names for extras installed beside Ultimate ASI
/// Loader. When ASI Loader is a game's X.dll it loads XHooked.dll from the same folder as the real X.dll, so an extra
/// built as X.dll can sit beside it as XHooked.dll (the X.dll build under the hooked name).
/// </summary>
public static class HookedDllNames
{
    private const string Suffix = "Hooked";

    /// <summary>version.dll → versionHooked.dll.</summary>
    public static string ToHooked(string dllName)
        => Path.GetFileNameWithoutExtension(dllName) + Suffix + Path.GetExtension(dllName);

    /// <summary>True for "XHooked.dll" (X non-empty); <paramref name="baseName"/> is then "X.dll".</summary>
    public static bool TryGetBase(string? name, out string baseName)
    {
        baseName = "";
        if (string.IsNullOrEmpty(name) || !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return false;
        var stem = Path.GetFileNameWithoutExtension(name);
        if (stem.Length <= Suffix.Length || !stem.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)) return false;
        baseName = stem[..^Suffix.Length] + Path.GetExtension(name);
        return true;
    }

    /// <summary>The name a hooked file stands in for (versionHooked.dll → version.dll); other names unchanged.</summary>
    public static string BaseOf(string name) => TryGetBase(name, out var baseName) ? baseName : name;

    /// <summary>True when <paramref name="name"/> is the hooked name for ASI Loader's <paramref name="ualName"/>.</summary>
    public static bool IsHookedFor(string? name, string? ualName)
        => !string.IsNullOrEmpty(ualName) && TryGetBase(name, out var baseName) && baseName.Equals(ualName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The spelling RHI stores for <paramref name="name"/> among an extra's <paramref name="offered"/> names: an offered
    /// name as listed, or the hooked name of an offered <c>.dll</c> name (its base as listed + "Hooked.dll"); null for
    /// anything else. Only bare file names pass, so a stored value can never name another folder.
    /// </summary>
    public static string? Canonical(string? name, IEnumerable<string> offered)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var trimmed = name.Trim();
        if (Path.GetFileName(trimmed) != trimmed) return null;
        var list = offered as IReadOnlyCollection<string> ?? offered.ToArray();
        if (list.FirstOrDefault(n => n.Equals(trimmed, StringComparison.OrdinalIgnoreCase)) is { } listed) return listed;
        if (!TryGetBase(trimmed, out var baseName)) return null;
        return list.FirstOrDefault(n => n.Equals(baseName, StringComparison.OrdinalIgnoreCase)) is { } listedBase ? ToHooked(listedBase) : null;
    }

    /// <summary>
    /// An extra's picker names with ASI Loader's hooked name inserted right after ASI Loader's name, when ASI Loader is
    /// installed as one of them. <paramref name="exclude"/> skips names the extra cannot use hooked (RTX Encore's Bink
    /// names, which already use Hooked for the game's own file).
    /// </summary>
    public static IReadOnlyList<string> WithHooked(IEnumerable<string> names, string? ualName, Func<string, bool>? exclude = null)
    {
        var list = names.ToList();
        if (string.IsNullOrEmpty(ualName) || !ualName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return list;
        if (exclude?.Invoke(ualName) == true) return list;
        var index = list.FindIndex(n => n.Equals(ualName, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return list;
        var hooked = ToHooked(list[index]);
        if (!list.Contains(hooked, StringComparer.OrdinalIgnoreCase)) list.Insert(index + 1, hooked);
        return list;
    }
}
