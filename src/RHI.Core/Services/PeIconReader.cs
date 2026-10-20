using System.Buffers.Binary;
using System.IO.Compression;

namespace RHI.Core.Services;

/// <summary>
/// #202: the largest icon of a Windows PE file (its <c>RT_GROUP_ICON</c> / <c>RT_ICON</c> resources) as PNG bytes, for a
/// desktop shortcut's <c>Icon=</c>. PNG-format icons are returned as stored; BMP (DIB) icons are converted (1/4/8/24/32
/// bits per pixel, the AND mask as transparency where the image has no alpha). Reads are bounded (headers, section table,
/// three directory levels, one capped icon) and the file is never loaded whole. Anything unexpected — not a PE (an
/// encrypted Game Pass exe, a native Linux binary), no icon, a malformed or oversized resource — gives null; never throws.
/// </summary>
public static class PeIconReader
{
    /// <summary>Largest icon resource read (a 256×256 32-bit DIB is about 270 KB; PNG icons are smaller).</summary>
    public const int MaxIconBytes = 4 * 1024 * 1024;
    /// <summary>Largest side of a DIB icon that is converted.</summary>
    public const int MaxDibSide = 1024;
    private const int MaxGroupEntries = 256;
    private const int MaxSections = 96;
    private const int MaxDirectoryEntries = 4096;
    private const int DosHeaderSize = 0x40;
    private const uint ResourceTypeIcon = 3;
    private const uint ResourceTypeGroupIcon = 14;
    private static ReadOnlySpan<byte> PngSignature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The largest icon of the PE at <paramref name="path"/> as PNG bytes, or null.</summary>
    public static byte[]? ReadLargestIconPng(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            if (!IsRegularFileCandidate(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!stream.CanSeek) return null;
            var icon = ReadLargestIcon(stream);
            return icon is null ? null : ToPng(icon);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException
                                       or NotSupportedException or InvalidDataException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>An icon resource's bytes as PNG: PNG data as stored (checked), a DIB converted; null when neither.</summary>
    internal static byte[]? ToPng(byte[] icon)
    {
        if (icon.AsSpan().StartsWith(PngSignature))
            return icon.Length >= 33 && icon.AsSpan(12, 4).SequenceEqual("IHDR"u8) ? icon : null;
        return DibToRgba(icon) is { } image ? EncodePng(image.Width, image.Height, image.Rgba) : null;
    }

    private static bool IsRegularFileCandidate(string path)
    {
        FileSystemInfo info = new FileInfo(path);
        if (info.LinkTarget is not null)
            info = info.ResolveLinkTarget(returnFinalTarget: true) ?? info;
        return info is FileInfo { Exists: true } file && file.Length >= DosHeaderSize;
    }

    // ── PE resources ────────────────────────────────────────────────────────────────────────────────────────────

    private static byte[]? ReadLargestIcon(FileStream stream)
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
        byte[]? Data(ResourceEntry entry, int cap)
        {
            if (entry.IsDirectory) return null;
            var dataEntry = ReadAt(stream, resourceBase + entry.Offset, 16);
            if (dataEntry is null) return null;
            var dataRva = BinaryPrimitives.ReadUInt32LittleEndian(dataEntry);
            var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(dataEntry.AsSpan(4));
            if (dataSize == 0 || dataSize > cap || FileOffset(dataRva) is not { } dataOffset) return null;
            return ReadAt(stream, dataOffset, (int)dataSize);
        }

        // RT_GROUP_ICON → first group → first language → GRPICONDIR.
        if (FindEntry(stream, resourceBase, 0, ResourceTypeGroupIcon) is not { IsDirectory: true } groups) return null;
        if (FindEntry(stream, resourceBase, groups.Offset, null) is not { IsDirectory: true } groupLanguages) return null;
        if (FindEntry(stream, resourceBase, groupLanguages.Offset, null) is not { } groupData) return null;
        var group = Data(groupData, 6 + MaxGroupEntries * 14);
        if (group is null || group.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(2)) != 1) return null;
        var count = Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(4)), (group.Length - 6) / 14);

        if (FindEntry(stream, resourceBase, 0, ResourceTypeIcon) is not { IsDirectory: true } icons) return null;
        // Largest first: side (0 = 256), then colour depth, then size; the first one that reads wins.
        var candidates = Enumerable.Range(0, count)
            .Select(i => group.AsSpan(6 + i * 14, 14).ToArray())
            .Select(e => (Side: Math.Max(e[0] == 0 ? 256 : e[0], e[1] == 0 ? 256 : e[1]), Bits: BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(6)),
                Bytes: BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(8)), Id: BinaryPrimitives.ReadUInt16LittleEndian(e.AsSpan(12))))
            .OrderByDescending(e => e.Side).ThenByDescending(e => e.Bits).ThenByDescending(e => e.Bytes);
        foreach (var candidate in candidates)
        {
            if (FindEntry(stream, resourceBase, icons.Offset, candidate.Id) is not { IsDirectory: true } iconLanguages) continue;
            if (FindEntry(stream, resourceBase, iconLanguages.Offset, null) is not { } iconData) continue;
            if (Data(iconData, MaxIconBytes) is { } bytes && ToPng(bytes) is not null) return bytes;
        }
        return null;
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

    private static byte[]? ReadAt(FileStream stream, long offset, int count)
    {
        if (offset < 0 || count <= 0 || offset >= stream.Length || offset + count > stream.Length) return null;
        var buffer = new byte[count];
        stream.Position = offset;
        stream.ReadExactly(buffer);
        return buffer;
    }

    // ── DIB → RGBA ──────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record DecodedImage(int Width, int Height, byte[] Rgba);

    /// <summary>An icon DIB (BITMAPINFOHEADER, BI_RGB, 1/4/8/24/32 bpp; height = image + AND mask) as top-down RGBA.</summary>
    private static DecodedImage? DibToRgba(byte[] dib)
    {
        if (dib.Length < 40 || BinaryPrimitives.ReadUInt32LittleEndian(dib) < 40) return null;
        var headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(dib);
        var width = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(4));
        var doubledHeight = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8));
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14));
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(16));
        var colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(32));
        var height = doubledHeight / 2;
        if (width is <= 0 or > MaxDibSide || height is <= 0 or > MaxDibSide || headerSize > dib.Length) return null;
        if (compression != 0 || bits is not (1 or 4 or 8 or 24 or 32)) return null;

        var paletteCount = bits <= 8 ? (colorsUsed is > 0 and <= 256 ? (int)colorsUsed : 1 << bits) : 0;
        var paletteOffset = headerSize;
        var xorOffset = paletteOffset + paletteCount * 4;
        var xorStride = ((width * bits + 31) / 32) * 4;
        var andStride = ((width + 31) / 32) * 4;
        var andOffset = xorOffset + xorStride * height;
        if ((long)xorOffset + (long)xorStride * height > dib.Length) return null;
        var hasMask = (long)andOffset + (long)andStride * height <= dib.Length;

        var rgba = new byte[width * height * 4];
        var anyAlpha = false;
        for (var y = 0; y < height; y++)
        {
            var row = xorOffset + (height - 1 - y) * xorStride; // bottom-up
            for (var x = 0; x < width; x++)
            {
                byte r, g, b, a = 255;
                switch (bits)
                {
                    case 32:
                        b = dib[row + x * 4]; g = dib[row + x * 4 + 1]; r = dib[row + x * 4 + 2]; a = dib[row + x * 4 + 3];
                        if (a != 0) anyAlpha = true;
                        break;
                    case 24:
                        b = dib[row + x * 3]; g = dib[row + x * 3 + 1]; r = dib[row + x * 3 + 2];
                        break;
                    default:
                        var index = bits switch
                        {
                            8 => dib[row + x],
                            4 => (dib[row + x / 2] >> (x % 2 == 0 ? 4 : 0)) & 0x0F,
                            _ => (dib[row + x / 8] >> (7 - x % 8)) & 0x01
                        };
                        if (index >= paletteCount) index = 0;
                        var p = paletteOffset + index * 4;
                        b = dib[p]; g = dib[p + 1]; r = dib[p + 2];
                        break;
                }
                var o = (y * width + x) * 4;
                rgba[o] = r; rgba[o + 1] = g; rgba[o + 2] = b; rgba[o + 3] = a;
            }
        }
        // No usable alpha channel (below 32 bits, or a 32-bit image whose alpha is all zero): the AND mask decides.
        if (bits != 32 || !anyAlpha)
        {
            for (var y = 0; y < height; y++)
            {
                var row = andOffset + (height - 1 - y) * andStride;
                for (var x = 0; x < width; x++)
                {
                    var transparent = hasMask && ((dib[row + x / 8] >> (7 - x % 8)) & 0x01) == 1;
                    rgba[(y * width + x) * 4 + 3] = transparent ? (byte)0 : (byte)255;
                }
            }
        }
        return new DecodedImage(width, height, rgba);
    }

    // ── PNG encoder (RGBA, 8 bits, no interlace) ────────────────────────────────────────────────────────────────

    internal static byte[] EncodePng(int width, int height, byte[] rgba)
    {
        var raw = new byte[height * (width * 4 + 1)];
        for (var y = 0; y < height; y++)
        {
            raw[y * (width * 4 + 1)] = 0; // filter: none
            Buffer.BlockCopy(rgba, y * width * 4, raw, y * (width * 4 + 1) + 1, width * 4);
        }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(raw);

        using var png = new MemoryStream();
        png.Write(PngSignature);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // RGBA
        WriteChunk(png, "IHDR"u8, ihdr);
        WriteChunk(png, "IDAT"u8, compressed.ToArray());
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream png, ReadOnlySpan<byte> type, byte[] data)
    {
        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(four, data.Length);
        png.Write(four);
        png.Write(type);
        png.Write(data);
        var crc = Crc32(Crc32(0xFFFFFFFFu, type), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(four, crc);
        png.Write(four);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
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

    private static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
