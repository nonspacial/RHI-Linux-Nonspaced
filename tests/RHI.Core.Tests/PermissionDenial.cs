namespace RHI.Core.Tests;

/// <summary>
/// #222 item 5: tests that need this process to be refused by file modes (a read-only game folder, so an undo fails; an
/// unreadable ledger) cannot prove anything where modes are not enforced (running as root). They are skipped there with
/// that reason, and everywhere else they assert the precondition (<see cref="PermissionDenial.MakeReadOnly"/>,
/// <see cref="PermissionDenial.MakeUnreadable"/>) — never pass without checking.
/// </summary>
internal static class PermissionDenial
{
    public const string SkipReason =
        "This process is not refused by file modes (running as root), so a read-only folder or an unreadable file cannot be produced here.";

    private static readonly Lazy<bool> EnforcedValue = new(Probe);

    /// <summary>Whether a mode-0500 folder refuses this process a new file and a mode-0000 file refuses it a read.</summary>
    public static bool Enforced => EnforcedValue.Value;

    private static bool Probe()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rhi-permission-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "file");
        File.WriteAllBytes(file, [1]);
        try
        {
            File.SetUnixFileMode(file, UnixFileMode.None);
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            return !CanCreateIn(folder) && !CanRead(file);
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Makes <paramref name="folder"/> read-only (0500) and asserts this process can no longer create a file in it.</summary>
    public static void MakeReadOnly(string folder)
    {
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        Assert.False(CanCreateIn(folder), $"Precondition: {folder} should refuse new files now ({SkipReason})");
    }

    public static void MakeWritable(string folder)
        => File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

    /// <summary>Makes <paramref name="file"/> unreadable (0000) and asserts this process can no longer read it.</summary>
    public static void MakeUnreadable(string file)
    {
        File.SetUnixFileMode(file, UnixFileMode.None);
        Assert.False(CanRead(file), $"Precondition: {file} should refuse reads now ({SkipReason})");
    }

    private static bool CanCreateIn(string folder)
    {
        var probe = Path.Combine(folder, ".rhi-permission-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool CanRead(string file)
    {
        try
        {
            File.ReadAllBytes(file);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>A fact that needs file modes to refuse this process (<see cref="PermissionDenial"/>); skipped, with the reason, where they do not.</summary>
public sealed class PermissionDenialFactAttribute : FactAttribute
{
    public PermissionDenialFactAttribute()
    {
        if (!PermissionDenial.Enforced) Skip = PermissionDenial.SkipReason;
    }
}

/// <summary>A theory that needs file modes to refuse this process (<see cref="PermissionDenial"/>); skipped, with the reason, where they do not.</summary>
public sealed class PermissionDenialTheoryAttribute : TheoryAttribute
{
    public PermissionDenialTheoryAttribute()
    {
        if (!PermissionDenial.Enforced) Skip = PermissionDenial.SkipReason;
    }
}
