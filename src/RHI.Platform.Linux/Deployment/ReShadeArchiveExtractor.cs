using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using RHI.Core.Deployment;

namespace RHI.Platform.Linux.Deployment;

public interface IReShadeArchiveExtractor
{
    Task ExtractDllsAsync(string setupExe, string outputDirectory, CancellationToken cancellationToken = default);
}

/// <summary>
/// Extracts ReShade64.dll/ReShade32.dll from the official ReShade setup. The setup is a PE stub
/// with a ZIP appended whose offsets count from the start of the ZIP, not of the file, which
/// <see cref="ZipFile.OpenRead"/> rejects. The ZIP is read in place through an offset window, so
/// no host 7-Zip/bsdtar is needed; a host extractor is only tried when that managed read fails.
/// </summary>
public sealed class ReShadeArchiveExtractor : IReShadeArchiveExtractor
{
    private static readonly string[] PayloadNames = ["ReShade64.dll", "ReShade32.dll"];

    /// <summary>Fixed start of the "no way to read the setup" error (localisation key); the reason follows it.</summary>
    public const string SetupUnreadablePrefix = "The ReShade setup could not be read, and no 7z/7zz/7za or bsdtar is installed to try instead. Reason: ";

    /// <summary>Largest DLL accepted from the setup (real ones are about 5 MB).</summary>
    internal const long MaxEntryBytes = 64L * 1024 * 1024;
    /// <summary>Largest total written from one setup.</summary>
    internal const long MaxTotalBytes = 96L * 1024 * 1024;
    /// <summary>Mode of every extracted file (less umask); the archive's own mode bits are never applied.</summary>
    private const UnixFileMode FixedFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private readonly Func<(string Command, string Kind)?> _findExternalExtractor;

    public ReShadeArchiveExtractor() : this(FindExternalExtractor) { }

    internal ReShadeArchiveExtractor(Func<(string Command, string Kind)?> findExternalExtractor)
        => _findExternalExtractor = findExternalExtractor;

    public async Task ExtractDllsAsync(string setupExe, string outputDirectory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var managedFailure = await TryExtractManagedAsync(setupExe, outputDirectory, cancellationToken).ConfigureAwait(false);
        if (managedFailure is null) return;

        var extractor = _findExternalExtractor();
        if (extractor is null)
            // The reason goes last so the PT-BR presentation pair can translate the fixed text before it.
            throw new InvalidOperationException($"{SetupUnreadablePrefix}{managedFailure}");

        var (command, kind) = extractor.Value;
        var psi = new ProcessStartInfo(command)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = outputDirectory
        };
        if (kind == "7z")
        {
            psi.ArgumentList.Add("e");
            psi.ArgumentList.Add(setupExe);
            psi.ArgumentList.Add("ReShade64.dll");
            psi.ArgumentList.Add("ReShade32.dll");
            psi.ArgumentList.Add($"-o{outputDirectory}");
            psi.ArgumentList.Add("-y");
        }
        else
        {
            psi.ArgumentList.Add("-xf");
            psi.ArgumentList.Add(setupExe);
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(outputDirectory);
            psi.ArgumentList.Add("ReShade64.dll");
            psi.ArgumentList.Add("ReShade32.dll");
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {command}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        _ = await stdoutTask.ConfigureAwait(false);
        var error = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{command} could not extract ReShade setup (exit {process.ExitCode}): {error.Trim()}");
    }

    /// <summary>
    /// Extracts the payload DLLs; returns null on success, otherwise why the archive could not be
    /// read (the caller then tries a host tool). Every declared size is checked against the caps
    /// before anything is written; each file is staged through <see cref="AtomicFile.StageStreamAsync"/>
    /// (bounded by the declared size, fixed mode — never the archive's — fsynced and verified), length
    /// and CRC-32 are compared with the entry, and only then is it placed with
    /// <see cref="AtomicFile.MoveIntoPlace"/>. Cancellation throws; write failures on the output
    /// (disk full, permissions) propagate as they are, since a host tool would hit them too.
    /// </summary>
    internal static async Task<string?> TryExtractManagedAsync(string setupExe, string outputDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var file = new FileStream(setupExe, FileMode.Open, FileAccess.Read, FileShare.Read);
        ZipArchive? archive = null;
        try
        {
            List<(string Name, ZipArchiveEntry Entry)> payload = [];
            try
            {
                if (FindZip(file) is not { } zip) return "no ZIP archive found in the setup";
                archive = new ZipArchive(new OffsetReadStream(file, zip.Start, zip.End), ZipArchiveMode.Read, leaveOpen: false);
                // Entries reads the central directory lazily, so a corrupt directory throws here.
                foreach (var name in PayloadNames)
                {
                    // Leaf names only: an entry path can never place a file outside outputDirectory.
                    var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (entry is not null) payload.Add((name, entry));
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException)
            {
                return ex.Message;
            }

            if (payload.Count == 0) return "the archive holds no ReShade64.dll or ReShade32.dll";
            long total = 0;
            foreach (var (name, entry) in payload)
            {
                if (entry.Length > MaxEntryBytes)
                    return $"{name} declares {entry.Length} bytes, over the {MaxEntryBytes}-byte limit";
                total += entry.Length;
            }
            if (total > MaxTotalBytes)
                return $"the ReShade DLLs together declare {total} bytes, over the {MaxTotalBytes}-byte limit";

            foreach (var (name, entry) in payload)
                if (await ExtractVerifiedAsync(entry, Path.Combine(outputDirectory, name), cancellationToken).ConfigureAwait(false) is { } failure)
                    return $"{name}: {failure}";
            return null;
        }
        finally
        {
            archive?.Dispose();
        }
    }

    /// <summary>Copies one entry to <paramref name="destination"/>; returns null, or why the entry's bytes were refused.</summary>
    private static async Task<string?> ExtractVerifiedAsync(ZipArchiveEntry entry, string destination, CancellationToken cancellationToken)
    {
        Stream source;
        try { source = entry.Open(); }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException) { return ex.Message; }

        StagedFile staged;
        var reader = new EntryReadStream(source);
        await using (reader.ConfigureAwait(false))
        {
            try
            {
                staged = await AtomicFile.StageStreamAsync(reader, destination, null, null, entry.Length, cancellationToken, FixedFileMode).ConfigureAwait(false);
            }
            catch (EntryReadException ex)
            {
                return ex.InnerException!.Message;
            }
            catch (InvalidDataException) when (reader.BytesRead > entry.Length)
            {
                return $"holds more than the {entry.Length} bytes it declares";
            }
        }

        string? refusal = null;
        if (staged.Length != entry.Length) refusal = $"holds {staged.Length} bytes but declares {entry.Length}";
        else if (reader.Crc32 != entry.Crc32) refusal = "CRC-32 does not match the archive (corrupt download)";
        if (refusal is not null)
        {
            FileCleanup.TryDelete(staged.Path);
            return refusal;
        }
        try
        {
            AtomicFile.MoveIntoPlace(staged.Path, destination);
        }
        catch
        {
            FileCleanup.TryDelete(staged.Path);
            throw;
        }
        return null;
    }

    /// <summary>A read failure of the archive entry (not of the output), so it is reported rather than thrown.</summary>
    private sealed class EntryReadException(Exception inner) : Exception(inner.Message, inner);

    /// <summary>Counts and CRC-32s what is read from an archive entry; marks read failures as <see cref="EntryReadException"/>.</summary>
    private sealed class EntryReadStream(Stream inner) : Stream
    {
        private uint _crc = ReShadeArchiveExtractor.Crc32.Seed;

        public long BytesRead { get; private set; }
        public uint Crc32 => ReShadeArchiveExtractor.Crc32.Finish(_crc);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int read;
            try { read = inner.Read(buffer); }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException) { throw new EntryReadException(ex); }
            BytesRead += read;
            _crc = ReShadeArchiveExtractor.Crc32.Append(_crc, buffer[..read]);
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Locates a (possibly prefixed) ZIP from its end-of-central-directory record and returns its
    /// first byte (EOCD position - central directory size - central directory offset) and the byte
    /// after the record and its comment. The record need not sit at the very end: signed setups
    /// carry the Authenticode certificate after the ZIP, and the end bounds the reader so a stray
    /// "PK\x05\x06" in that certificate is never read as the ZIP's own record. A candidate counts
    /// only when a central directory header sits where it points. ZIP64 is not handled (ReShade
    /// setups are far below 4 GiB); null means "not a ZIP this reader handles".
    /// </summary>
    internal static (long Start, long End)? FindZip(Stream file)
    {
        const int eocdSize = 22;
        const int searchWindow = 512 * 1024;
        const uint eocdSignature = 0x06054b50;
        const uint centralDirectorySignature = 0x02014b50;
        if (file.Length < eocdSize) return null;

        var tailLength = (int)Math.Min(file.Length, searchWindow);
        var tailStart = file.Length - tailLength;
        var tail = new byte[tailLength];
        file.Seek(tailStart, SeekOrigin.Begin);
        file.ReadExactly(tail);

        Span<byte> signature = stackalloc byte[4];
        for (var i = tailLength - eocdSize; i >= 0; i--)
        {
            var record = tail.AsSpan(i);
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != eocdSignature) continue;
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(record[20..]);
            if (i + eocdSize + commentLength > tailLength) continue;

            var directorySize = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
            var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
            if (directoryOffset == uint.MaxValue || directorySize == uint.MaxValue) continue;

            var directoryPosition = tailStart + i - directorySize;
            var zipStart = directoryPosition - directoryOffset;
            if (directoryPosition < 0 || zipStart < 0 || directorySize == 0) continue;

            file.Seek(directoryPosition, SeekOrigin.Begin);
            file.ReadExactly(signature);
            if (BinaryPrimitives.ReadUInt32LittleEndian(signature) == centralDirectorySignature)
                return (zipStart, tailStart + i + eocdSize + commentLength);
        }
        return null;
    }

    internal static (string Command, string Kind)? FindExternalExtractor()
    {
        foreach (var candidate in new[] { "7zz", "7z", "7za" })
            if (FindInPath(candidate) is { } path) return (path, "7z");
        if (FindInPath("bsdtar") is { } bsdtar) return (bsdtar, "bsdtar");
        return null;
    }

    private static string? FindInPath(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, command);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>Read-only view of <paramref name="inner"/> from <paramref name="start"/> up to <paramref name="end"/>; does not own <paramref name="inner"/>.</summary>
    private sealed class OffsetReadStream(Stream inner, long start, long end) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => end - start;

        public override long Position
        {
            get => _position;
            set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= Length) return 0;
            inner.Seek(start + _position, SeekOrigin.Begin);
            var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, Length - _position)]);
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>CRC-32 (IEEE 802.3, reflected 0xEDB88320) as stored in ZIP entries.</summary>
    internal static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        public const uint Seed = 0xFFFFFFFFu;

        public static uint Append(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        public static uint Finish(uint crc) => crc ^ 0xFFFFFFFFu;

        public static uint Compute(ReadOnlySpan<byte> data) => Finish(Append(Seed, data));

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }
    }
}
