using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;

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
    private readonly Func<(string Command, string Kind)?> _findExternalExtractor;

    public ReShadeArchiveExtractor() : this(FindExternalExtractor) { }

    internal ReShadeArchiveExtractor(Func<(string Command, string Kind)?> findExternalExtractor)
        => _findExternalExtractor = findExternalExtractor;

    public async Task ExtractDllsAsync(string setupExe, string outputDirectory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var managedFailure = TryExtractManaged(setupExe, outputDirectory);
        if (managedFailure is null) return;

        var extractor = _findExternalExtractor();
        if (extractor is null)
            throw new InvalidOperationException($"Could not read the ReShade setup archive ({managedFailure}), and no 7z/7zz/7za or bsdtar is installed to try instead.");

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

    /// <summary>Extracts the payload DLLs; returns null on success, otherwise why the archive could not be read.</summary>
    internal static string? TryExtractManaged(string setupExe, string outputDirectory)
    {
        using var file = new FileStream(setupExe, FileMode.Open, FileAccess.Read, FileShare.Read);
        ZipArchive archive;
        try
        {
            if (FindZipStart(file) is not { } zipStart) return "no ZIP archive found in the setup";
            archive = new ZipArchive(new OffsetReadStream(file, zipStart), ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            return ex.Message;
        }

        using (archive)
        {
            var extracted = 0;
            foreach (var name in PayloadNames)
            {
                // Leaf names only: an entry path can never place a file outside outputDirectory.
                var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (entry is null) continue;
                try
                {
                    entry.ExtractToFile(Path.Combine(outputDirectory, name), overwrite: true);
                }
                catch (InvalidDataException ex)
                {
                    return $"{name}: {ex.Message}";
                }
                extracted++;
            }
            return extracted > 0 ? null : "the archive holds no ReShade64.dll or ReShade32.dll";
        }
    }

    /// <summary>
    /// Finds the first byte of a (possibly prefixed) ZIP from its end-of-central-directory record:
    /// start = EOCD position - central directory size - central directory offset. The record need
    /// not sit at the very end: signed setups carry the Authenticode certificate after the ZIP.
    /// A candidate counts only when a central directory header sits where it points. ZIP64 is not
    /// handled (ReShade setups are far below 4 GiB); null means "not a ZIP this reader handles".
    /// </summary>
    internal static long? FindZipStart(Stream file)
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
            if (BinaryPrimitives.ReadUInt32LittleEndian(signature) == centralDirectorySignature) return zipStart;
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
            catch { }
        }
        return null;
    }

    /// <summary>Read-only view of <paramref name="inner"/> from <paramref name="start"/> to its end; does not own <paramref name="inner"/>.</summary>
    private sealed class OffsetReadStream(Stream inner, long start) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length - start;

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
}
