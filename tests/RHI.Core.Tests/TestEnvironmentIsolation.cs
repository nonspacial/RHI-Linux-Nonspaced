using System.Runtime.CompilerServices;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

/// <summary>
/// Points HOME and every XDG base directory at a fresh per-run temp root before any test code runs,
/// so a service constructed with its default paths (record store, settings, preference stores, add-on
/// caches) can never write into the owner's real ~/.cache, ~/.local/share or ~/.config. The module
/// initializer runs before the first method or static field of this assembly is touched, which is
/// before xunit instantiates any test class or evaluates any MemberData.
/// </summary>
internal static class TestEnvironmentIsolation
{
    private static readonly string[] Variables =
        ["HOME", "XDG_CACHE_HOME", "XDG_DATA_HOME", "XDG_CONFIG_HOME", "XDG_STATE_HOME"];

    /// <summary>Per-run root every redirected variable points under.</summary>
    public static string Root { get; private set; } = "";

    /// <summary>Values of the redirected variables as the test process inherited them (null when unset).</summary>
    public static IReadOnlyDictionary<string, string?> Original { get; private set; } = new Dictionary<string, string?>();

    /// <summary>The real per-user rhi-linux directories this run must never write to.</summary>
    public static IReadOnlyList<string> RealRhiDirectories { get; private set; } = [];

    [ModuleInitializer]
    internal static void Initialize()
    {
        var original = Variables.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        Original = original;
        RealRhiDirectories = RealRhiDirectoriesFrom(original);

        var root = Path.Combine(Path.GetTempPath(), $"rhi-tests-home-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Redirect(root, RealRhiDirectories, Environment.SetEnvironmentVariable, () =>
        {
            Root = root;
            AssertIsolated();
        });

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        };
    }

    /// <summary>
    /// Redirects HOME/XDG_* under <paramref name="root"/>. The overlap check against the real rhi-linux
    /// directories runs on the planned paths BEFORE any directory is created, so a root that would land
    /// in a protected location fails without leaving even an empty folder there. After the variables
    /// are set, what LinuxPaths actually resolves is checked too; if that fails the fresh root is removed.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> Redirect(
        string root,
        IReadOnlyList<string> realRhiDirectories,
        Action<string, string?> setVariable,
        Action assertResolved)
    {
        var paths = new Dictionary<string, string>
        {
            ["HOME"] = Path.Combine(root, "home"),
            ["XDG_CACHE_HOME"] = Path.Combine(root, "cache"),
            ["XDG_DATA_HOME"] = Path.Combine(root, "data"),
            ["XDG_CONFIG_HOME"] = Path.Combine(root, "config"),
            ["XDG_STATE_HOME"] = Path.Combine(root, "state"),
        };
        foreach (var (name, value) in paths)
        {
            var rhi = Path.Combine(value, "rhi-linux");
            foreach (var real in realRhiDirectories)
            {
                if (IsUnder(root, real) || IsUnder(rhi, real) || IsUnder(real, rhi))
                    throw new InvalidOperationException(
                        $"Test isolation failed: planned {name}/rhi-linux ('{rhi}') overlaps the real directory '{real}'.");
            }
        }

        // Only now, with the planned paths proven clear of every protected directory, create them.
        // (They must exist before LinuxPaths is checked: SpecialFolder.UserProfile resolves to "" for
        // a HOME that does not exist.)
        foreach (var value in paths.Values) Directory.CreateDirectory(value);
        try
        {
            foreach (var (name, value) in paths) setVariable(name, value);
            assertResolved();
        }
        catch
        {
            try { Directory.Delete(root, recursive: true); } catch { }
            throw;
        }
        return paths;
    }

    /// <summary>
    /// Fails fast (throws, so every test in the run fails with this message) when a LinuxPaths root
    /// does not resolve under <see cref="Root"/> or lands in one of the real rhi-linux directories.
    /// LinuxPaths reads the environment on every access (Home goes through SpecialFolder.UserProfile,
    /// which .NET resolves from $HOME at call time on Unix), so no stale copy can survive the redirect.
    /// </summary>
    internal static void AssertIsolated()
    {
        var roots = new Dictionary<string, string>
        {
            [nameof(LinuxPaths.Home)] = LinuxPaths.Home,
            [nameof(LinuxPaths.CacheHome)] = LinuxPaths.CacheHome,
            [nameof(LinuxPaths.DataHome)] = LinuxPaths.DataHome,
            [nameof(LinuxPaths.ConfigHome)] = LinuxPaths.ConfigHome,
            [nameof(LinuxPaths.StateHome)] = LinuxPaths.StateHome,
        };
        foreach (var (name, value) in roots)
        {
            if (!IsUnder(value, Root))
                throw new InvalidOperationException(
                    $"Test isolation failed: LinuxPaths.{name} resolves to '{value}', outside the per-run test root '{Root}'. " +
                    "Refusing to run tests that could write into the real user directories.");
            var rhi = Path.Combine(value, "rhi-linux");
            foreach (var real in RealRhiDirectories)
            {
                if (IsUnder(rhi, real) || IsUnder(real, rhi))
                    throw new InvalidOperationException(
                        $"Test isolation failed: LinuxPaths.{name}/rhi-linux ('{rhi}') overlaps the real directory '{real}'.");
            }
        }
    }

    internal static bool IsUnder(string path, string root)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return full.Equals(parent, StringComparison.Ordinal)
               || full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> RealRhiDirectoriesFrom(IReadOnlyDictionary<string, string?> original)
    {
        // The account's home from the password database too, in case the run was started with HOME
        // already redirected: the owner's real directories are what must stay untouched.
        var homes = new List<string>();
        if (!string.IsNullOrWhiteSpace(original["HOME"])) homes.Add(original["HOME"]!);
        var passwdHome = PasswdHome();
        if (!string.IsNullOrWhiteSpace(passwdHome)) homes.Add(passwdHome!);

        var result = new List<string>();
        foreach (var home in homes)
        {
            result.Add(Path.Combine(home, ".cache", "rhi-linux"));
            result.Add(Path.Combine(home, ".local", "share", "rhi-linux"));
            result.Add(Path.Combine(home, ".config", "rhi-linux"));
            result.Add(Path.Combine(home, ".local", "state", "rhi-linux"));
        }
        foreach (var name in new[] { "XDG_CACHE_HOME", "XDG_DATA_HOME", "XDG_CONFIG_HOME", "XDG_STATE_HOME" })
        {
            if (!string.IsNullOrWhiteSpace(original[name])) result.Add(Path.Combine(original[name]!, "rhi-linux"));
        }
        return result.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string? PasswdHome()
    {
        try
        {
            var user = Environment.UserName;
            foreach (var line in File.ReadLines("/etc/passwd"))
            {
                var fields = line.Split(':');
                if (fields.Length >= 6 && fields[0] == user) return fields[5];
            }
        }
        catch { }
        return null;
    }
}
