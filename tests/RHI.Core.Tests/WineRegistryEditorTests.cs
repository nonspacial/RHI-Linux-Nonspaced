using System.Text;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #24 (DEP-05): every user.reg edit goes through <see cref="WineRegistryEditor"/>: refused while a wineserver owns the
/// prefix (found in a synthetic /proc), serialised by a lock, backed up per edit, replaced atomically and byte-exactly.
/// </summary>
public sealed class WineRegistryEditorTests : IDisposable
{
    private const string Hive = "WINE REGISTRY Version 2\n;; All keys relative to \\\\User\\\\S-1-5-21\n\n[Software\\\\Wine\\\\Other] 1\n\"x\"=\"y\"\n";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-wine-editor-tests-" + Guid.NewGuid().ToString("N"));

    public WineRegistryEditorTests() => Directory.CreateDirectory(_root);

    private string NewPrefix(string name, string? hive = null)
    {
        var prefix = Path.Combine(_root, name);
        Directory.CreateDirectory(prefix);
        File.WriteAllText(Path.Combine(prefix, "user.reg"), hive ?? Hive);
        return prefix;
    }

    /// <summary>A /proc with one wineserver whose environment names <paramref name="winePrefix"/>.</summary>
    private string FakeProc(string winePrefix, string comm = "wineserver")
    {
        var proc = Path.Combine(_root, "proc-" + Guid.NewGuid().ToString("N"));
        var process = Path.Combine(proc, "4242");
        Directory.CreateDirectory(process);
        File.WriteAllText(Path.Combine(process, "comm"), comm + "\n");
        File.WriteAllText(Path.Combine(process, "environ"), $"HOME=/home/someone\0WINEPREFIX={winePrefix}\0");
        Directory.CreateDirectory(Path.Combine(proc, "self"));
        return proc;
    }

    private static WineDllOverrideService ServiceWithProc(string proc, TimeSpan? lockTimeout = null)
        => new(new WineRegistryEditor(prefix => WineServerProbe.IsRunningFor(prefix, proc), lockTimeout));

    private static string Read(string prefix) => File.ReadAllText(Path.Combine(prefix, "user.reg"));

    private static string[] Backups(string prefix)
        => Directory.GetFiles(prefix, "user.reg" + WineRegistryEditor.BackupMarker + "*").Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void Ensure_WhileAWineserverOwnsThePrefix_IsRefusedAndChangesNothing()
    {
        var prefix = NewPrefix("live");
        var before = File.ReadAllBytes(Path.Combine(prefix, "user.reg"));
        var service = ServiceWithProc(FakeProc(prefix));

        var refused = Assert.Throws<WineRegistryEditRefusedException>(() => service.EnsureNativeBuiltin(prefix, "dxgi.dll", "Game.exe"));

        Assert.True(refused.PrefixInUse);
        Assert.Equal(WineRegistryEditor.PrefixInUseRefusalPrefix + prefix, refused.Message);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(prefix, "user.reg")));
        Assert.Empty(Backups(prefix));
        Assert.Equal(WineRegistryEditor.PrefixInUseRefusalPrefix + prefix, service.EditRefusal(prefix));
    }

    [Fact]
    public void Restore_WhileAWineserverOwnsThePrefix_IsRefusedAndKeepsRhisValue()
    {
        var prefix = NewPrefix("live-restore");
        var record = new WineDllOverrideService().EnsureNativeBuiltin(prefix, "dxgi.dll", "Game.exe");
        var service = ServiceWithProc(FakeProc(prefix));

        Assert.Throws<WineRegistryEditRefusedException>(() => service.Restore(prefix, record));

        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(Read(prefix), "dxgi", "Game.exe"));
    }

    [Fact]
    public void WineserverOfAnotherPrefix_OrAnotherProgram_DoesNotBlock()
    {
        var prefix = NewPrefix("idle");
        var otherPrefixProc = FakeProc(Path.Combine(_root, "someone-else"));
        var notWineserverProc = FakeProc(prefix, comm: "wine64-preload");

        Assert.Null(ServiceWithProc(otherPrefixProc).EditRefusal(prefix));
        ServiceWithProc(otherPrefixProc).EnsureNativeBuiltin(prefix, "dxgi.dll", "Game.exe");
        ServiceWithProc(notWineserverProc).EnsureNativeBuiltin(prefix, "d3d9.dll", "Game.exe");

        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(Read(prefix), "dxgi", "Game.exe"));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(Read(prefix), "d3d9", "Game.exe"));
    }

    // w2 wine review, minor 2 (N8): a wineserver of a sibling prefix whose path shares a prefix with ours (Steam
    // compatdata ids, pfx vs pfx2) does not block; the identity is the whole path, not a string prefix of it.
    [Theory]
    [InlineData("compatdata/39754/pfx", "compatdata/397540/pfx")]
    [InlineData("compatdata/397540/pfx", "compatdata/39754/pfx")]
    [InlineData("library/pfx", "library/pfx2")]
    [InlineData("library/pfx2", "library/pfx")]
    public void WineserverOfASiblingPrefixSharingAPathPrefix_DoesNotBlock(string mine, string running)
    {
        var prefix = NewPrefix(mine);
        var sibling = Path.Combine(_root, running);
        Directory.CreateDirectory(sibling);

        Assert.False(WineServerProbe.IsRunningFor(prefix, FakeProc(sibling)));
        Assert.True(WineServerProbe.IsRunningFor(sibling, FakeProc(sibling)));
    }

    [Fact]
    public void WineserverReachedThroughASymlinkedLibraryPath_IsStillFound()
    {
        // Steam libraries are often reached through a symlinked parent; wineserver's WINEPREFIX is the real path.
        var realParent = Path.Combine(_root, "real-library");
        Directory.CreateDirectory(realParent);
        var prefix = Path.Combine(realParent, "pfx");
        Directory.CreateDirectory(prefix);
        File.WriteAllText(Path.Combine(prefix, "user.reg"), Hive);
        var link = Path.Combine(_root, "library-link");
        Directory.CreateSymbolicLink(link, realParent);

        Assert.True(WineServerProbe.IsRunningFor(Path.Combine(link, "pfx"), FakeProc(prefix)));
    }

    [Fact]
    public void Edit_ReplacesTheHiveByRename_NeverRewritesItInPlace()
    {
        var prefix = NewPrefix("atomic");
        var hive = Path.Combine(prefix, "user.reg");
        // A handle opened before the edit keeps reading the old file: a truncate-and-write would show through it.
        using var before = new FileStream(hive, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        new WineDllOverrideService().EnsureNativeBuiltin(prefix, "dxgi.dll", "Game.exe");

        using (var reader = new StreamReader(before, leaveOpen: true))
            Assert.Equal(Hive, reader.ReadToEnd());
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(Read(prefix), "dxgi", "Game.exe"));
        Assert.DoesNotContain(Directory.GetFiles(prefix), path => path.Contains(".rhi-tmp-", StringComparison.Ordinal));
    }

    [Fact]
    public void Edit_KeepsBytesItDidNotChange_ExactlyAsWineWroteThem()
    {
        // A byte that is not valid UTF-8 (a hand edit, another tool): a text round trip would turn it into U+FFFD.
        var prefix = Path.Combine(_root, "bytes");
        Directory.CreateDirectory(prefix);
        var hive = Path.Combine(prefix, "user.reg");
        var original = Encoding.Latin1.GetBytes("WINE REGISTRY Version 2\r\n\r\n[Software\\\\Vendor] 1\r\n\"name\"=\"caf\u00e9\"\r\n");
        File.WriteAllBytes(hive, original);
        var service = new WineDllOverrideService();

        var record = service.EnsureNativeBuiltin(prefix, "dxgi.dll", "Game.exe");
        Assert.Contains((byte)0xE9, File.ReadAllBytes(hive));
        service.Restore(prefix, record);

        Assert.Equal(original, File.ReadAllBytes(hive));
    }

    [Fact]
    public void ApplicationNamesOutsideAscii_AreWrittenAndFoundTheWayWineEscapesThem()
    {
        // wineserver writes key names as ASCII: UTF-16 units above 127 become \x hex, and [ ] \ get a backslash.
        Assert.Equal(@"Jogo\xe9.exe", WineRegistryEditor.EscapeKeyName("Jogo\u00e9.exe"));
        Assert.Equal(@"\x30b2\x30fc\x30e0.exe", WineRegistryEditor.EscapeKeyName("\u30b2\u30fc\u30e0.exe"));
        Assert.Equal(@"\x00e9a.exe", WineRegistryEditor.EscapeKeyName("\u00e9a.exe")); // a hex digit follows: four digits
        Assert.Equal(@"Game \[x64\].exe", WineRegistryEditor.EscapeKeyName("Game [x64].exe"));

        // A section Wine itself wrote for a non-ASCII executable is found, not duplicated.
        var prefix = NewPrefix("unicode", "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\AppDefaults\\\\Jogo\\xe9.exe\\\\DllOverrides] 1\n\"dxgi\"=\"builtin\"\n");
        var service = new WineDllOverrideService();

        var record = service.EnsureNativeBuiltin(prefix, "dxgi.dll", "Jogo\u00e9.exe");

        Assert.Equal("builtin", record.PreviousValue);
        Assert.False(record.SectionCreatedByRhi);
        var text = Read(prefix);
        Assert.Single(text.Split('\n'), line => line.StartsWith("[Software\\\\Wine\\\\AppDefaults\\\\", StringComparison.Ordinal));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(text, "dxgi", "Jogo\u00e9.exe"));
        Assert.All(File.ReadAllBytes(Path.Combine(prefix, "user.reg")), b => Assert.True(b < 128));
    }

    [Fact]
    public void EveryChange_KeepsTheHiveAsItWasBeforeIt_NewestThreeKept()
    {
        var prefix = NewPrefix("backups");
        var hive = Path.Combine(prefix, "user.reg");
        var service = new WineDllOverrideService();
        var states = new List<byte[]>();

        foreach (var dll in new[] { "dxgi", "d3d9", "d3d11", "winmm", "version" })
        {
            states.Add(File.ReadAllBytes(hive));
            service.EnsureNativeBuiltin(prefix, dll, "Game.exe");
        }

        var backups = Backups(prefix);
        Assert.Equal(WineRegistryEditor.BackupsKept, backups.Length);
        // Refreshed per change: the newest backups hold the hive exactly as it was before each of the last three edits.
        Assert.Equal(states.TakeLast(3).ToArray(), backups.Select(File.ReadAllBytes).ToArray());

        // A no-op (already native,builtin) writes nothing, so it adds no backup.
        service.EnsureNativeBuiltin(prefix, "dxgi", "Game.exe");
        Assert.Equal(backups, Backups(prefix));
    }

    [Fact]
    public void AnotherProcessHoldingThePrefixLock_RefusesTheEditAfterTheTimeout()
    {
        var prefix = NewPrefix("locked");
        var before = File.ReadAllBytes(Path.Combine(prefix, "user.reg"));
        var service = ServiceWithProc(FakeProc(Path.Combine(_root, "nobody")), lockTimeout: TimeSpan.FromMilliseconds(300));

        // Another RHI process mid-edit: its exclusive flock on user.reg.rhi-lock.
        using (new FileStream(Path.Combine(prefix, "user.reg" + WineRegistryEditor.LockSuffix), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var refused = Assert.Throws<WineRegistryEditRefusedException>(() => service.EnsureNativeBuiltin(prefix, "dxgi.dll", "Game.exe"));
            Assert.False(refused.PrefixInUse);
            Assert.Equal(WineRegistryEditor.BusyRefusalPrefix + prefix, refused.Message);
        }

        Assert.Equal(before, File.ReadAllBytes(Path.Combine(prefix, "user.reg")));
        service.EnsureNativeBuiltin(prefix, "dxgi.dll", "Game.exe"); // lock released: goes through
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(Read(prefix), "dxgi", "Game.exe"));
    }

    [Fact]
    public async Task ConcurrentEditsOfOnePrefix_AreSerialised_SoNeitherIsLost()
    {
        var prefix = NewPrefix("concurrent");
        using var firstInside = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var editor = new WineRegistryEditor(_ => false);
        var calls = 0;
        editor.InsideLockForTesting = _ =>
        {
            if (Interlocked.Increment(ref calls) != 1) return;
            firstInside.Set();
            releaseFirst.Wait(TimeSpan.FromSeconds(30)); // the first edit has read nothing yet and holds the lock
        };
        var service = new WineDllOverrideService(editor);

        var first = Task.Run(() => service.EnsureNativeBuiltin(prefix, "dxgi.dll", "Game.exe"));
        Assert.True(firstInside.Wait(TimeSpan.FromSeconds(30)));
        var second = Task.Run(() => service.EnsureNativeBuiltin(prefix, "d3d9.dll", "Game.exe"));
        await Task.Delay(200);
        Assert.False(second.IsCompleted); // waits for the lock instead of reading the hive the first edit is about to replace
        releaseFirst.Set();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));

        var text = Read(prefix);
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(text, "dxgi", "Game.exe"));
        Assert.Equal("native,builtin", WineDllOverrideService.TryReadOverride(text, "d3d9", "Game.exe"));
    }

    [Fact]
    public void ReadOnlyHive_IsNeverReplaced()
    {
        var prefix = NewPrefix("readonly");
        var hive = Path.Combine(prefix, "user.reg");
        File.SetUnixFileMode(hive, UnixFileMode.UserRead | UnixFileMode.GroupRead);
        try
        {
            Assert.ThrowsAny<IOException>(() => new WineDllOverrideService().EnsureNativeBuiltin(prefix, "dxgi.dll", "Game.exe"));
            Assert.Equal(Hive, Read(prefix));
        }
        finally
        {
            File.SetUnixFileMode(hive, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void MissingHive_LeavesNoLockFileInTheUninitialisedPrefix()
    {
        var prefix = Path.Combine(_root, "empty");
        Directory.CreateDirectory(prefix);

        var missing = Assert.Throws<FileNotFoundException>(() => new WineDllOverrideService().EnsureNativeBuiltin(prefix, "dxgi.dll", "Game.exe"));

        Assert.Equal(WineRegistryEditor.MissingHiveMessage, missing.Message);
        Assert.Empty(Directory.GetFileSystemEntries(prefix));
    }

    [Fact]
    public async Task WaitUntilIdle_ReturnsOnceWineserverHasGone_OrFalseAtTheDeadline()
    {
        var running = true;
        var editor = new WineRegistryEditor(_ => Volatile.Read(ref running));

        Assert.False(await editor.WaitUntilIdleAsync(_root, TimeSpan.FromMilliseconds(300)));
        var waiting = editor.WaitUntilIdleAsync(_root, TimeSpan.FromSeconds(30));
        await Task.Delay(300);
        Volatile.Write(ref running, false);
        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
    }
}
