using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// #286 (upstream 2.9.3 <c>870f4a9</c>, <c>SourceEngineSetup</c> / <c>MainViewModel.SourceEngine</c>): RenoDX's Source engine
/// mod (CommandGenius's generic add-on) for the games of <c>RenoDXdb-source.json</c>, and where upstream installs it.
/// The Linux one-click setup of the mod's DXVK chain waits for an owner game test (UPSTREAM.md, #286); until then an
/// install of this add-on is refused with <see cref="SetupNotYetMessage"/>.
/// </summary>
public static class RenoDxSourceEngine
{
    public const string AddonName64 = "renodx-sourceengine.addon64";
    public const string AddonName32 = "renodx-sourceengine.addon32";
    public const string AddonUrl64 = "https://github.com/CommandGenius/renodx/releases/download/snapshot/" + AddonName64;
    public const string AddonUrl32 = "https://github.com/CommandGenius/renodx/releases/download/snapshot/" + AddonName32;
    public const string ModName = "Generic Source Engine";
    public const string Author = "CommandGenius";
    /// <summary>Upstream's engine badge text for Source games.</summary>
    public const string EngineHint = "Source Engine";

    /// <summary>Why the Source mod is not installed by RHI Linux yet (PT-BR pair).</summary>
    public const string SetupNotYetMessage =
        "RenoDX's Source engine mod needs DXVK loaded in front of ReShade, which RHI Linux does not set up yet (it is waiting for a test on a real game under Proton). RHI did not install the add-on on its own, because without that setup it does nothing. To try it now, follow the Source engine steps on the RenoDX wiki.";

    /// <summary>The Source engine mod for a game listed in the Source database (upstream <c>MakeGenericSourceFor</c>).</summary>
    public static RenoDxModEntry EntryFor(RenoDxSourceDbEntry entry)
        => new(ModName, AddonUrl64, AddonUrl32, string.IsNullOrWhiteSpace(entry.Comments) ? "Generic RenoDX Source engine addon" : entry.Comments!.Trim(),
            IsGeneric: true, IsWorkInProgress: string.Equals(entry.Status?.Trim(), "WIP", StringComparison.OrdinalIgnoreCase), Author: Author);

    /// <summary>Whether <paramref name="url"/> is the Source engine add-on (either bitness).</summary>
    public static bool IsSourceAddonUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (Path.GetFileName(uri.LocalPath).Equals(AddonName64, StringComparison.OrdinalIgnoreCase)
               || Path.GetFileName(uri.LocalPath).Equals(AddonName32, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Upstream <c>GetDbSourceEntry</c>: the exact name first, then ignoring punctuation and spacing (the database has
    /// "Half Life 2" and "Counter Strike: Source"; Steam has hyphens).
    /// </summary>
    public static RenoDxSourceDbEntry? Find(RenoDxSourceDatabase? database, string? gameName)
    {
        if (database is null || string.IsNullOrWhiteSpace(gameName)) return null;
        var exact = database.Entries.FirstOrDefault(entry => entry.Name.Equals(gameName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var normalized = RenoDxCatalogService.NormalizeName(gameName);
        return normalized.Length == 0 ? null : database.Entries.FirstOrDefault(entry => RenoDxCatalogService.NormalizeName(entry.Name) == normalized);
    }

    /// <summary>A folder that gets ReShade and the add-on, and which build (32/64-bit) goes there.</summary>
    public sealed record Target(string Dir, bool Is64Bit);

    // Tools and launchers beside the game exe that say nothing about the game's build (upstream IgnoredExeParts).
    private static readonly string[] IgnoredExeParts = ["unins", "crash", "setup", "redist", "vconsole", "launcher"];

    /// <summary>
    /// Upstream <c>DetectTargets</c>: Garry's Mod (64- and 32-bit exes in the main folder, engines in bin/win64 and bin) →
    /// bin/win64, then bin; other 64-bit games → the main folder; 32-bit games → bin when it holds the engine (else the
    /// main folder), plus bin/win64 when that holds a 64-bit build. Linux: folder names are matched ignoring case. Empty
    /// when nothing matches.
    /// </summary>
    public static IReadOnlyList<Target> DetectTargets(string? root)
    {
        var list = new List<Target>();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return list;
        var machines = GameExes(root).Select(PeArchitectureDetector.Detect).ToList();
        bool has64 = machines.Contains(PeArchitecture.X64), has32 = machines.Contains(PeArchitecture.X86);
        var bin = Child(root, "bin");
        var win64 = bin is null ? null : Child(bin, "win64");

        if (has64 && IsEngineFolder(win64))
        {
            list.Add(new Target(win64!, true));
            if (has32 && IsEngineFolder(bin)) list.Add(new Target(bin!, false));
        }
        else if (has64)
            list.Add(new Target(root, true));
        else if (has32)
        {
            list.Add(new Target(IsEngineFolder(bin) ? bin! : root, false));
            if (IsEngineFolder(win64) && GameExes(win64!).Any(exe => PeArchitectureDetector.Detect(exe) == PeArchitecture.X64))
                list.Add(new Target(win64!, true));
        }
        return list;
    }

    /// <summary>Upstream <c>PrimarySubfolder</c>: the main target relative to the main folder ("bin"), or null for the main folder.</summary>
    public static string? PrimarySubfolder(string? root)
    {
        var first = DetectTargets(root).FirstOrDefault();
        return first is null || Path.GetFullPath(first.Dir).TrimEnd('/') == Path.GetFullPath(root!).TrimEnd('/')
            ? null
            : Path.GetRelativePath(root!, first.Dir);
    }

    private static bool IsEngineFolder(string? dir)
        => dir is not null && (Child(dir, "shaderapidx9.dll", file: true) is not null || Child(dir, "engine.dll", file: true) is not null);

    private static IEnumerable<string> GameExes(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir)
                .Where(file => Path.GetExtension(file).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                .Where(file => !IgnoredExeParts.Any(part => Path.GetFileName(file).Contains(part, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // A child folder or file by name, ignoring case (Windows games on a case-sensitive file system).
    private static string? Child(string dir, string name, bool file = false)
    {
        try
        {
            var entries = file ? Directory.EnumerateFiles(dir) : Directory.EnumerateDirectories(dir);
            return entries.FirstOrDefault(path => Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
