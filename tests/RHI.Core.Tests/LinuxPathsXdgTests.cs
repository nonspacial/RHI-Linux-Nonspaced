using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

/// <summary>
/// Integration w1 review (ui-build, incidental): an empty or relative <c>XDG_*_HOME</c> is invalid under the XDG
/// base-directory spec and must fall back to the default under $HOME (only <c>StateHome</c> did), never become a folder
/// relative to the directory RHI was started from.
/// </summary>
public sealed class LinuxPathsXdgTests
{
    public static TheoryData<string, string, string> Homes() => new()
    {
        { "XDG_CONFIG_HOME", nameof(LinuxPaths.ConfigHome), ".config" },
        { "XDG_DATA_HOME", nameof(LinuxPaths.DataHome), Path.Combine(".local", "share") },
        { "XDG_CACHE_HOME", nameof(LinuxPaths.CacheHome), ".cache" },
        { "XDG_STATE_HOME", nameof(LinuxPaths.StateHome), Path.Combine(".local", "state") },
    };

    [Theory]
    [MemberData(nameof(Homes))]
    public void EmptyOrRelativeValue_IsIgnored(string variable, string property, string defaultUnderHome)
    {
        var expected = Path.Combine(LinuxPaths.Home, defaultUnderHome);
        foreach (var value in new string?[] { null, "", "relative/dir" })
            Assert.Equal(expected, Resolve(property, name => name == variable ? value : null));
    }

    [Theory]
    [MemberData(nameof(Homes))]
    public void AbsoluteValue_IsUsed(string variable, string property, string _)
        => Assert.Equal("/srv/xdg", Resolve(property, name => name == variable ? "/srv/xdg" : null));

    private static string Resolve(string property, Func<string, string?> env) => property switch
    {
        nameof(LinuxPaths.ConfigHome) => LinuxPaths.ConfigHomeFrom(env),
        nameof(LinuxPaths.DataHome) => LinuxPaths.DataHomeFrom(env),
        nameof(LinuxPaths.CacheHome) => LinuxPaths.CacheHomeFrom(env),
        _ => LinuxPaths.StateHomeFrom(env),
    };
}
