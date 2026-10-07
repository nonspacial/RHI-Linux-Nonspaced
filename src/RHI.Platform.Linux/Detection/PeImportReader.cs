using System.Text;

namespace RHI.Platform.Linux.Detection;

/// <summary>DLL names a PE executable imports, from its import and delay-load import tables.</summary>
internal sealed record PeImports(IReadOnlyList<string> Regular, IReadOnlyList<string> Delay);

/// <summary>
/// Minimal PE import-table reader, the same evidence upstream RHI's GraphicsApiDetector uses: the DLLs
/// the executable links against, rather than any string that happens to appear in the file. Returns null
/// for anything that is not a readable PE image; never throws for malformed input.
/// </summary>
internal static class PeImportReader
{
    private const int HeaderBufferSize = 4096;
    private const int ImportReadSize = 65536;
    private const int MaxDescriptors = 1024;

    public static PeImports? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var header = new byte[HeaderBufferSize];
            var headerRead = ReadAtMost(stream, header);
            if (headerRead < 0x40 || header[0] != 'M' || header[1] != 'Z') return null;
            var pe = BitConverter.ToInt32(header, 0x3C);
            if (pe < 0 || pe + 24 > headerRead) return null;
            if (header[pe] != 'P' || header[pe + 1] != 'E' || header[pe + 2] != 0 || header[pe + 3] != 0) return null;

            var coff = pe + 4;
            int sectionCount = BitConverter.ToUInt16(header, coff + 2);
            int optionalSize = BitConverter.ToUInt16(header, coff + 16);
            var optional = coff + 20;
            if (optional + optionalSize > headerRead || optional + 2 > headerRead) return null;
            var dataDirectories = BitConverter.ToUInt16(header, optional) switch
            {
                0x10B => optional + 96,
                0x20B => optional + 112,
                _ => -1
            };
            if (dataDirectories < 0) return null;
            var optionalEnd = optional + optionalSize;
            uint ImportDirectoryRva(int index)
            {
                var at = dataDirectories + index * 8;
                return at + 8 <= optionalEnd ? BitConverter.ToUInt32(header, at) : 0;
            }

            var sections = new List<(uint Va, uint Size, uint Raw)>(sectionCount);
            var table = optionalEnd;
            for (var i = 0; i < sectionCount && table + (i + 1) * 40 <= headerRead; i++)
            {
                var s = table + i * 40;
                var virtualSize = BitConverter.ToUInt32(header, s + 8);
                var rawSize = BitConverter.ToUInt32(header, s + 16);
                sections.Add((BitConverter.ToUInt32(header, s + 12), Math.Max(virtualSize, rawSize), BitConverter.ToUInt32(header, s + 20)));
            }

            var regular = ReadNames(stream, sections, ImportDirectoryRva(1), descriptorSize: 20, nameOffset: 12);
            var delay = ReadNames(stream, sections, ImportDirectoryRva(13), descriptorSize: 32, nameOffset: 4);
            return new PeImports(regular, delay);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null; // unreadable or truncated: callers fall back to signature scanning
        }
    }

    private static IReadOnlyList<string> ReadNames(
        FileStream stream, List<(uint Va, uint Size, uint Raw)> sections, uint directoryRva, int descriptorSize, int nameOffset)
    {
        var names = new List<string>();
        if (directoryRva == 0) return names;
        var offset = RvaToOffset(sections, directoryRva);
        if (offset < 0 || offset >= stream.Length) return names;
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[ImportReadSize];
        var read = ReadAtMost(stream, buffer);
        for (var i = 0; i < MaxDescriptors && (i + 1) * descriptorSize <= read; i++)
        {
            var nameRva = BitConverter.ToUInt32(buffer, i * descriptorSize + nameOffset);
            if (nameRva == 0) break;
            if (ReadName(stream, sections, nameRva) is { Length: > 0 } name) names.Add(name);
        }
        return names;
    }

    private static string? ReadName(FileStream stream, List<(uint Va, uint Size, uint Raw)> sections, uint rva)
    {
        var offset = RvaToOffset(sections, rva);
        if (offset < 0 || offset >= stream.Length) return null;
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[256];
        var read = ReadAtMost(stream, buffer);
        var end = 0;
        while (end < read && buffer[end] != 0) end++;
        return Encoding.ASCII.GetString(buffer, 0, end);
    }

    private static long RvaToOffset(List<(uint Va, uint Size, uint Raw)> sections, uint rva)
    {
        foreach (var (va, size, raw) in sections)
            if (rva >= va && rva < (long)va + size)
                return raw + (long)(rva - va);
        return -1;
    }

    private static int ReadAtMost(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = stream.Read(buffer, total, buffer.Length - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }
}
