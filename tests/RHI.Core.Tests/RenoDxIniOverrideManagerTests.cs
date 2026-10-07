using System.Runtime.Versioning;
using RHI.Platform.Linux.Deployment;
using RHI.Core.Services.RenoDxSafety;

namespace RHI.Core.Tests;

public sealed class RenoDxIniOverrideManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-renodx-ini-tests-" + Guid.NewGuid().ToString("N"));

    public RenoDxIniOverrideManagerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ApplyAndCleanup_NewSection_RemovesOnlyRhiOwnedContent()
    {
        var ini = Path.Combine(_root, "ReShade.ini");
        File.WriteAllText(ini, "[GENERAL]\nEffectSearchPaths=.\\reshade-shaders\\Shaders\\**\n");

        var result = RenoDxIniOverrideManager.Apply(
            _root,
            new Dictionary<string, string> { ["Set_Path"] = "1" });

        Assert.NotNull(result.State);
        Assert.Contains("[renodx]", File.ReadAllText(ini), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Set_Path=1", File.ReadAllText(ini));

        RenoDxIniOverrideManager.Cleanup(result.State);

        var final = File.ReadAllText(ini);
        Assert.DoesNotContain("[renodx]", final, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EffectSearchPaths=.\\reshade-shaders\\Shaders\\**", final);
    }

    [Fact]
    public void ApplyAndCleanup_PreexistingValue_RestoresExactPriorValue()
    {
        var ini = Path.Combine(_root, "reshade.ini");
        File.WriteAllText(ini, "[renodx]\nSet_Path=0\nKeepMe=abc\n");

        var result = RenoDxIniOverrideManager.Apply(
            _root,
            new Dictionary<string, string> { ["Set_Path"] = "1" });
        Assert.Contains("Set_Path=1", File.ReadAllText(ini));

        RenoDxIniOverrideManager.Cleanup(result.State);

        var final = File.ReadAllText(ini);
        Assert.Contains("Set_Path=0", final);
        Assert.Contains("KeepMe=abc", final);
    }

    [Fact]
    public void Cleanup_UserChangedManagedKey_PreservesUserValue()
    {
        var ini = Path.Combine(_root, "ReShade.ini");
        File.WriteAllText(ini, "[renodx]\nSet_Path=0\n");
        var result = RenoDxIniOverrideManager.Apply(
            _root,
            new Dictionary<string, string> { ["Set_Path"] = "1" });

        File.WriteAllText(ini, "[renodx]\nSet_Path=7\n");
        RenoDxIniOverrideManager.Cleanup(result.State);

        Assert.Contains("Set_Path=7", File.ReadAllText(ini));
    }

    [Fact]
    public void Apply_UpdateAfterUserEdit_DoesNotForceManifestAgain()
    {
        var ini = Path.Combine(_root, "ReShade.ini");
        File.WriteAllText(ini, "[renodx]\nSet_Path=0\n");
        var first = RenoDxIniOverrideManager.Apply(
            _root,
            new Dictionary<string, string> { ["Set_Path"] = "1" });

        File.WriteAllText(ini, "[renodx]\nSet_Path=7\n");
        var second = RenoDxIniOverrideManager.Apply(
            _root,
            new Dictionary<string, string> { ["Set_Path"] = "1" },
            first.State);

        Assert.Equal(1, second.PreservedUserEdits);
        Assert.Contains("Set_Path=7", File.ReadAllText(ini));
    }


    [Fact]
    public void Apply_UpdateAfterUserDeletesSection_DoesNotRecreateIt()
    {
        var ini = Path.Combine(_root, "ReShade.ini");
        File.WriteAllText(ini, "[renodx]\nSet_Path=0\n");
        var first = RenoDxIniOverrideManager.Apply(
            _root,
            new Dictionary<string, string> { ["Set_Path"] = "1" });

        File.WriteAllText(ini, "[GENERAL]\nEffectSearchPaths=.\\reshade-shaders\\Shaders\\**\n");
        var second = RenoDxIniOverrideManager.Apply(
            _root,
            new Dictionary<string, string> { ["Set_Path"] = "1" },
            first.State);

        Assert.Equal(1, second.PreservedUserEdits);
        Assert.DoesNotContain("[renodx]", File.ReadAllText(ini), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SerializeRoundTrip_PreservesRollbackAuthority()
    {
        var ini = Path.Combine(_root, "ReShade.ini");
        File.WriteAllText(ini, "[renodx]\nSet_Path=0\n");
        var applied = RenoDxIniOverrideManager.Apply(
            _root,
            new Dictionary<string, string> { ["Set_Path"] = "1" });

        var json = RenoDxIniOverrideManager.Serialize(applied.State!);
        var restoredState = RenoDxIniOverrideManager.Deserialize(json);
        RenoDxIniOverrideManager.Cleanup(restoredState);

        Assert.Contains("Set_Path=0", File.ReadAllText(ini));
    }

    [Fact]
    public void ResolveAddonDeployPath_RecognizesCanonicalUppercaseReShadeIni()
    {
        var addons = Path.Combine(_root, "reshade-addons");
        File.WriteAllText(Path.Combine(_root, "ReShade.ini"), "[ADDON]\nAddonPath=.\\reshade-addons\n");

        var resolved = RenoDxDeploymentService.ResolveAddonDeployPath(_root);

        Assert.Equal(Path.GetFullPath(addons), resolved);
    }

    // #121: a write that fails part-way (full disk, crash) must leave the owner's ReShade.ini as it was.
    [Fact]
    public void Apply_WriteFailsPartWay_LeavesOriginalBytesAndNoTempFile()
    {
        var ini = Path.Combine(_root, "ReShade.ini");
        File.WriteAllText(ini, "[GENERAL]\nEffectSearchPaths=.\\reshade-shaders\\Shaders\\**\nPresetPath=.\\my.ini\n");
        var before = File.ReadAllBytes(ini);

        Assert.Throws<IOException>(() => RenoDxIniOverrideManager.Apply(
            _root, new Dictionary<string, string> { ["Set_Path"] = "1" }, wrapOutput: DiskFullAfter(8)));

        Assert.Equal(before, File.ReadAllBytes(ini));
        Assert.Single(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public void Cleanup_WriteFailsPartWay_LeavesAppliedBytes()
    {
        var ini = Path.Combine(_root, "ReShade.ini");
        File.WriteAllText(ini, "[renodx]\nSet_Path=0\nKeepMe=abc\n");
        var applied = RenoDxIniOverrideManager.Apply(_root, new Dictionary<string, string> { ["Set_Path"] = "1" });
        var before = File.ReadAllBytes(ini);

        Assert.Throws<IOException>(() => RenoDxIniOverrideManager.Cleanup(applied.State, DiskFullAfter(4)));

        Assert.Equal(before, File.ReadAllBytes(ini));
        Assert.Single(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void Apply_KeepsFileModeAndWritesThroughASymlinkedIni()
    {
        var shared = Path.Combine(_root, "shared");
        Directory.CreateDirectory(shared);
        var real = Path.Combine(shared, "ReShade.ini");
        File.WriteAllText(real, "[renodx]\nSet_Path=0\n");
        File.SetUnixFileMode(real, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var game = Path.Combine(_root, "game");
        Directory.CreateDirectory(game);
        var link = Path.Combine(game, "ReShade.ini");
        File.CreateSymbolicLink(link, real);

        RenoDxIniOverrideManager.Apply(game, new Dictionary<string, string> { ["Set_Path"] = "1" });

        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Contains("Set_Path=1", File.ReadAllText(real));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(real));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void RestoreSnapshots_RestoresAtomicallyAndKeepsWhatItCannotRestore()
    {
        var writable = Path.Combine(_root, "writable.ini");
        File.WriteAllText(writable, "changed");
        var locked = Path.Combine(_root, "locked");
        Directory.CreateDirectory(locked);
        var lockedIni = Path.Combine(locked, "ReShade.ini");
        File.WriteAllText(lockedIni, "changed");
        var fallback = Path.Combine(_root, "fallback");
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var notes = RenoDxDeploymentService.RestoreSnapshots(
            [
                new(writable, "original"u8.ToArray()),
                new(lockedIni, "locked original"u8.ToArray())
            ], fallback);

            Assert.Equal("original", File.ReadAllText(writable));
            var note = Assert.Single(notes);
            Assert.StartsWith($"Could not restore {lockedIni}", note, StringComparison.Ordinal);
            var kept = Assert.Single(Directory.EnumerateFiles(fallback));
            Assert.EndsWith(kept, note, StringComparison.Ordinal);
            Assert.Equal("locked original", File.ReadAllText(kept));
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static Func<Stream, Stream> DiskFullAfter(int bytes) => inner => new FailingStream(inner, bytes);

    private sealed class FailingStream(Stream inner, int allowed) : Stream
    {
        private int _written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            var take = Math.Min(count, allowed - _written);
            if (take > 0) inner.Write(buffer, offset, take);
            _written += take;
            if (take < count) throw new IOException("No space left on device");
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
