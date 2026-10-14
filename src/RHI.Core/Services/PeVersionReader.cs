using System.Buffers.Binary;
using System.Text;

namespace RHI.Core.Services;

/// <summary>
/// Version strings and numbers from a PE file's RT_VERSION resource. <see cref="FileDescription"/> and
/// <see cref="CompanyName"/> identify the binary (OptiScaler writes "OptiScaler" / "nitec"; #154).
/// </summary>
public sealed record PeVersionInfo(
    string? ProductVersion,
    string? FileVersion,
    Version? FixedFileVersion,
    string? FileDescription = null,
    string? CompanyName = null);

/// <summary>
/// Reads the <c>VS_VERSIONINFO</c> resource of a Windows PE file. <c>FileVersionInfo</c> on Linux
/// does not read PE resources, so the resource tree is walked directly. Reads are bounded: headers,
/// the section table, at most three directory levels and one capped resource blob; the file is
/// never loaded whole. Returns null when the file is not a PE or carries no usable version resource.
/// </summary>
public static class PeVersionReader
{
    public const int MaxVersionResourceBytes = 64 * 1024;
    private const int MaxSections = 96;
    private const int MaxDirectoryEntries = 4096;
    private const uint ResourceTypeVersion = 16;
    private const uint FixedFileInfoSignature = 0xFEEF04BD;

    public static PeVersionInfo? Read(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            // Regular files only: File.Exists is true for a FIFO or device, and opening a FIFO blocks
            // until a writer appears. Those report a zero size (also through a symlink), and no PE is
            // smaller than its DOS header, so the size check keeps them from ever being opened.
            if (!IsRegularFileCandidate(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!stream.CanSeek) return null;
            var blob = ReadVersionResource(stream);
            return blob is null ? null : ParseVersionInfo(blob);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException or NotSupportedException)
        {
            // Unreadable or malformed file: the caller falls back as for a file without a version resource.
            return null;
        }
    }

    private const int DosHeaderSize = 0x40;

    private static bool IsRegularFileCandidate(string path)
    {
        FileSystemInfo info = new FileInfo(path);
        if (info.LinkTarget is not null)
            info = info.ResolveLinkTarget(returnFinalTarget: true) ?? info;
        return info is FileInfo { Exists: true } file && file.Length >= DosHeaderSize;
    }

    private static byte[]? ReadVersionResource(FileStream stream)
    {
        var dos = ReadAt(stream, 0, DosHeaderSize);
        if (dos is null || dos[0] != 'M' || dos[1] != 'Z') return null;
        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(dos.AsSpan(0x3C));
        var coff = peOffset < DosHeaderSize ? null : ReadAt(stream, peOffset, 24);
        if (coff is null || BinaryPrimitives.ReadUInt32LittleEndian(coff) != 0x00004550) return null;
        var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(coff.AsSpan(6));
        var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(coff.AsSpan(20));
        if (sectionCount is 0 or > MaxSections || optionalSize < 2) return null;

        var optional = ReadAt(stream, peOffset + 24L, optionalSize);
        if (optional is null) return null;
        var (countOffset, directoriesOffset) = BinaryPrimitives.ReadUInt16LittleEndian(optional) switch
        {
            0x10B => (92, 96),   // PE32
            0x20B => (108, 112), // PE32+
            _ => (-1, -1)
        };
        if (countOffset < 0 || optional.Length < directoriesOffset + 3 * 8) return null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(optional.AsSpan(countOffset)) < 3) return null;
        var resourceRva = BinaryPrimitives.ReadUInt32LittleEndian(optional.AsSpan(directoriesOffset + 2 * 8));
        if (resourceRva == 0) return null;

        var sections = ReadAt(stream, peOffset + 24L + optionalSize, sectionCount * 40);
        if (sections is null) return null;
        long? FileOffset(uint rva)
        {
            for (var i = 0; i < sectionCount; i++)
            {
                var s = sections.AsSpan(i * 40, 40);
                var virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(s[8..]);
                var virtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(s[12..]);
                var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(s[16..]);
                var rawPointer = BinaryPrimitives.ReadUInt32LittleEndian(s[20..]);
                var span = Math.Max(virtualSize, rawSize);
                if (rva >= virtualAddress && rva - virtualAddress < span)
                {
                    var delta = rva - virtualAddress;
                    return delta < rawSize ? rawPointer + (long)delta : null;
                }
            }
            return null;
        }

        if (FileOffset(resourceRva) is not { } resourceBase) return null;
        // Type (RT_VERSION) → name (first) → language (first) → data entry.
        if (FindEntry(stream, resourceBase, 0, ResourceTypeVersion) is not { } nameDir || !nameDir.IsDirectory) return null;
        if (FindEntry(stream, resourceBase, nameDir.Offset, null) is not { } languageDir || !languageDir.IsDirectory) return null;
        if (FindEntry(stream, resourceBase, languageDir.Offset, null) is not { } data || data.IsDirectory) return null;

        var dataEntry = ReadAt(stream, resourceBase + data.Offset, 16);
        if (dataEntry is null) return null;
        var dataRva = BinaryPrimitives.ReadUInt32LittleEndian(dataEntry);
        var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(dataEntry.AsSpan(4));
        if (dataSize == 0 || FileOffset(dataRva) is not { } dataOffset) return null;
        return ReadAt(stream, dataOffset, (int)Math.Min(dataSize, MaxVersionResourceBytes), allowShort: true);
    }

    private readonly record struct ResourceEntry(bool IsDirectory, uint Offset);

    // Null id = first entry of any kind (name and language levels).
    private static ResourceEntry? FindEntry(FileStream stream, long resourceBase, uint directoryOffset, uint? id)
    {
        var header = ReadAt(stream, resourceBase + directoryOffset, 16);
        if (header is null) return null;
        var named = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(12));
        var ids = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14));
        var total = named + ids;
        if (total == 0 || total > MaxDirectoryEntries) return null;
        var entries = ReadAt(stream, resourceBase + directoryOffset + 16, total * 8);
        if (entries is null) return null;
        for (var i = 0; i < total; i++)
        {
            var name = BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan(i * 8));
            var target = BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan(i * 8 + 4));
            if (id is { } wanted && (i < named || name != wanted)) continue;
            return new ResourceEntry((target & 0x80000000) != 0, target & 0x7FFFFFFF);
        }
        return null;
    }

    private static byte[]? ReadAt(FileStream stream, long offset, int count, bool allowShort = false)
    {
        if (offset < 0 || count <= 0 || offset >= stream.Length) return null;
        if (!allowShort && offset + count > stream.Length) return null;
        count = (int)Math.Min(count, stream.Length - offset);
        var buffer = new byte[count];
        stream.Position = offset;
        stream.ReadExactly(buffer);
        return buffer;
    }

    internal static PeVersionInfo? ParseVersionInfo(byte[] blob)
    {
        if (!TryReadBlock(blob, 0, out var root) || root.Key != "VS_VERSION_INFO") return null;
        // The root value is always the binary VS_FIXEDFILEINFO: count wValueLength in bytes whatever
        // wType claims, and never read it past the block (a short or text-typed root keeps its strings).
        var valueEnd = (long)root.ValueOffset + root.ValueLength;
        root = root with { ChildrenOffset = (int)Math.Min(Align4((int)Math.Min(valueEnd, root.End)), root.End) };
        Version? fixedVersion = null;
        if (root.ValueLength >= 52 && root.ValueOffset + 52 <= root.End)
        {
            var value = blob.AsSpan(root.ValueOffset, 52);
            if (BinaryPrimitives.ReadUInt32LittleEndian(value) == FixedFileInfoSignature)
            {
                var ms = BinaryPrimitives.ReadUInt32LittleEndian(value[8..]);
                var ls = BinaryPrimitives.ReadUInt32LittleEndian(value[12..]);
                if (ms != 0 || ls != 0)
                    fixedVersion = new Version((int)(ms >> 16), (int)(ms & 0xFFFF), (int)(ls >> 16), (int)(ls & 0xFFFF));
            }
        }

        string? product = null, file = null, description = null, company = null;
        foreach (var info in Children(blob, root))
        {
            if (info.Key != "StringFileInfo") continue;
            foreach (var table in Children(blob, info))
            {
                foreach (var entry in Children(blob, table))
                {
                    if (entry.ValueLength == 0) continue;
                    // String values are counted in UTF-16 characters, including the terminator.
                    var bytes = Math.Min(entry.ValueLength * 2, entry.End - entry.ValueOffset);
                    var text = Encoding.Unicode.GetString(blob, entry.ValueOffset, Math.Max(0, bytes)).TrimEnd('\0').Trim();
                    if (text.Length == 0) continue;
                    if (entry.Key == "ProductVersion") product ??= text;
                    else if (entry.Key == "FileVersion") file ??= text;
                    else if (entry.Key == "FileDescription") description ??= text;
                    else if (entry.Key == "CompanyName") company ??= text;
                }
                // The first table that names the file wins (versions or identity).
                if (product is not null || file is not null || description is not null || company is not null) break;
            }
        }
        return product is null && file is null && fixedVersion is null && description is null && company is null
            ? null
            : new PeVersionInfo(product, file, fixedVersion, description, company);
    }

    private readonly record struct Block(string Key, int ValueOffset, int ValueLength, int ChildrenOffset, int End);

    private static IEnumerable<Block> Children(byte[] blob, Block parent)
    {
        var offset = parent.ChildrenOffset;
        while (offset < parent.End && TryReadBlock(blob, offset, out var child))
        {
            yield return child;
            var next = Align4(child.End);
            if (next <= offset) yield break;
            offset = next;
        }
    }

    private static bool TryReadBlock(byte[] data, int offset, out Block block)
    {
        block = default;
        ReadOnlySpan<byte> blob = data;
        if (offset < 0 || offset + 6 > blob.Length) return false;
        var length = BinaryPrimitives.ReadUInt16LittleEndian(blob[offset..]);
        var valueLength = BinaryPrimitives.ReadUInt16LittleEndian(blob[(offset + 2)..]);
        if (length < 6) return false;
        var end = Math.Min(offset + length, blob.Length);
        var keyStart = offset + 6;
        var keyEnd = keyStart;
        while (keyEnd + 1 < end && (blob[keyEnd] != 0 || blob[keyEnd + 1] != 0)) keyEnd += 2;
        if (keyEnd + 1 >= end) return false;
        var key = Encoding.Unicode.GetString(blob[keyStart..keyEnd]);
        var valueOffset = Align4(keyEnd + 2);
        if (valueOffset > end) return false;
        var type = BinaryPrimitives.ReadUInt16LittleEndian(blob[(offset + 4)..]);
        var valueBytes = type == 1 ? valueLength * 2 : valueLength;
        var children = Math.Min(Align4(valueOffset + valueBytes), end);
        if (type != 1 && valueOffset + valueLength > end) return false;
        block = new Block(key, valueOffset, valueLength, children, end);
        return true;
    }

    private static int Align4(int value) => (value + 3) & ~3;
}
