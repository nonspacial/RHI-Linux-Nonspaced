using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #31: the INI edits of DLSS5 Feeder and Luma go through the one user-file writer (verified, durable, atomic rename;
/// a symlink is never written through blindly). Install edits refuse a read-only ReShade.ini as before; the removal
/// edits write it and keep it read-only, as the owner's rule says (an RHI-owned edit may refuse install, never removal).
/// </summary>
public sealed class IniEditDurabilityTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("rhi-ini-durability-").FullName;

    private string Ini(string text)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + "-ReShade.ini");
        File.WriteAllText(path, text);
        return path;
    }

    private static void Lock(string path) => File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead);

    [Fact]
    public void FeederPresetAndTokenRemoval_WriteALockedIni_AndKeepItLocked()
    {
        var ini = Ini("[GENERAL]\nPresetPath=.\\user.ini\nLoadFromDllMain=0\n");
        var preset = ReShadeIniTokenEditor.SetPresetPath(ini, @".\RHI-DLSS5-Feeder.ini");
        var token = ReShadeIniTokenEditor.EnsureLoadFromDllMainToken(ini, "dlss5-feed.addon64");
        Lock(ini);

        ReShadeIniTokenEditor.RemoveLoadFromDllMainToken(ini, token);
        ReShadeIniTokenEditor.RestorePresetPath(ini, preset);

        var text = File.ReadAllText(ini);
        Assert.Contains(@"PresetPath=.\user.ini", text);
        Assert.DoesNotContain("dlss5-feed", text);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.GroupRead, File.GetUnixFileMode(ini) & (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead));
        Assert.Empty(Directory.GetFiles(_root, "*.rhi-*"));
    }

    [Fact]
    public void FeederPresetInstall_RefusesALockedIni_AndChangesNothing()
    {
        var ini = Ini("[GENERAL]\nPresetPath=.\\user.ini\n");
        Lock(ini);
        var before = File.ReadAllBytes(ini);

        Assert.ThrowsAny<IOException>(() => ReShadeIniTokenEditor.SetPresetPath(ini, @".\RHI-DLSS5-Feeder.ini"));

        Assert.Equal(before, File.ReadAllBytes(ini));
    }

    [Fact]
    public void FeederTokenInstall_DoesNotWriteThroughASymlinkedIni()
    {
        var real = Ini("[GENERAL]\nLoadFromDllMain=0\n");
        var link = Path.Combine(_root, "linked-ReShade.ini");
        File.CreateSymbolicLink(link, real);
        var before = File.ReadAllBytes(real);

        Assert.ThrowsAny<IOException>(() => ReShadeIniTokenEditor.EnsureLoadFromDllMainToken(link, "dlss5-feed.addon64"));

        Assert.Equal(before, File.ReadAllBytes(real));
    }

    public void Dispose()
    {
        foreach (var file in Directory.GetFiles(_root))
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
