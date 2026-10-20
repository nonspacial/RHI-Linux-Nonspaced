using System.Buffers.Binary;
using System.IO.Compression;

namespace RHI.Core.Tests;

/// <summary>
/// #202: builds small PE32+ files with an icon group in memory (no binaries from games), plus icon payloads (PNG and
/// BMP/DIB) and a minimal reader for the PNGs RHI writes (filter 0, RGBA 8-bit), with its own CRC check.
/// </summary>
internal static class TestPeFiles
{
    /// <summary>One icon of the group: its directory entry fields and the stored resource bytes.</summary>
    internal sealed record Icon(byte Width, byte Height, ushort Bits, byte[] Data, ushort Id);

    /// <summary>A PE32+ image whose only section holds RT_ICON resources and one RT_GROUP_ICON naming them.</summary>
    internal static byte[] WithIcons(params Icon[] icons) => Build(icons, groupIds: null);

    /// <summary>As <see cref="WithIcons"/>, but the group names <paramref name="groupIds"/> (lets a test point at a missing icon).</summary>
    internal static byte[] Build(Icon[] icons, ushort[]? groupIds)
    {
        var n = icons.Length;
        var rootOff = 0;
        var type3Off = rootOff + 16 + 2 * 8;
        var type14Off = type3Off + 16 + n * 8;
        var iconLangOff = type14Off + 16 + 8;
        var groupLangOff = iconLangOff + n * 24;
        var dataEntryOff = groupLangOff + 24;
        var blobOff = dataEntryOff + (n + 1) * 16;

        var ids = groupIds ?? icons.Select(i => i.Id).ToArray();
        var group = new byte[6 + ids.Length * 14];
        BinaryPrimitives.WriteUInt16LittleEndian(group.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(group.AsSpan(4), (ushort)ids.Length);
        for (var i = 0; i < ids.Length; i++)
        {
            var icon = icons.FirstOrDefault(x => x.Id == ids[i]) ?? icons[0];
            var e = group.AsSpan(6 + i * 14);
            e[0] = icon.Width; e[1] = icon.Height;
            BinaryPrimitives.WriteUInt16LittleEndian(e[4..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(e[6..], icon.Bits);
            BinaryPrimitives.WriteUInt32LittleEndian(e[8..], (uint)icon.Data.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(e[12..], ids[i]);
        }

        var blobs = icons.Select(i => i.Data).Append(group).ToArray();
        var blobOffsets = new int[blobs.Length];
        var cursor = blobOff;
        for (var i = 0; i < blobs.Length; i++)
        {
            blobOffsets[i] = cursor;
            cursor = (cursor + blobs[i].Length + 3) & ~3;
        }
        var rsrc = new byte[cursor];
        const uint rva = 0x1000;
        void Dir(int at, int idEntries) => BinaryPrimitives.WriteUInt16LittleEndian(rsrc.AsSpan(at + 14), (ushort)idEntries);
        void Entry(int at, uint id, int target, bool directory)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(rsrc.AsSpan(at), id);
            BinaryPrimitives.WriteUInt32LittleEndian(rsrc.AsSpan(at + 4), (uint)target | (directory ? 0x80000000u : 0));
        }
        Dir(rootOff, 2);
        Entry(rootOff + 16, 3, type3Off, true);
        Entry(rootOff + 24, 14, type14Off, true);
        Dir(type3Off, n);
        for (var i = 0; i < n; i++)
        {
            Entry(type3Off + 16 + i * 8, icons[i].Id, iconLangOff + i * 24, true);
            Dir(iconLangOff + i * 24, 1);
            Entry(iconLangOff + i * 24 + 16, 0x409, dataEntryOff + i * 16, false);
        }
        Dir(type14Off, 1);
        Entry(type14Off + 16, 1, groupLangOff, true);
        Dir(groupLangOff, 1);
        Entry(groupLangOff + 16, 0x409, dataEntryOff + n * 16, false);
        for (var i = 0; i < blobs.Length; i++)
        {
            var de = rsrc.AsSpan(dataEntryOff + i * 16);
            BinaryPrimitives.WriteUInt32LittleEndian(de, rva + (uint)blobOffsets[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(de[4..], (uint)blobs[i].Length);
            blobs[i].CopyTo(rsrc, blobOffsets[i]);
        }

        const int peOffset = 0x40, optionalSize = 240, rawPointer = 0x200;
        var file = new byte[rawPointer + rsrc.Length];
        file[0] = (byte)'M'; file[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(0x3C), peOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(peOffset), 0x00004550);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(peOffset + 4), 0x8664);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(peOffset + 6), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(peOffset + 20), optionalSize);
        var optional = file.AsSpan(peOffset + 24, optionalSize);
        BinaryPrimitives.WriteUInt16LittleEndian(optional, 0x20B);
        BinaryPrimitives.WriteUInt32LittleEndian(optional[108..], 16);
        BinaryPrimitives.WriteUInt32LittleEndian(optional[(112 + 2 * 8)..], rva);
        BinaryPrimitives.WriteUInt32LittleEndian(optional[(112 + 2 * 8 + 4)..], (uint)rsrc.Length);
        var section = file.AsSpan(peOffset + 24 + optionalSize, 40);
        ".rsrc"u8.CopyTo(section);
        BinaryPrimitives.WriteUInt32LittleEndian(section[8..], (uint)rsrc.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(section[12..], rva);
        BinaryPrimitives.WriteUInt32LittleEndian(section[16..], (uint)rsrc.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(section[20..], rawPointer);
        rsrc.CopyTo(file, rawPointer);
        return file;
    }

    /// <summary>A PNG-format icon payload (any valid PNG; RHI keeps it as stored).</summary>
    internal static byte[] PngIcon(int side, byte red) => RHI.Core.Services.PeIconReader.EncodePng(side, side,
        Enumerable.Range(0, side * side).SelectMany(_ => new byte[] { red, 1, 2, 255 }).ToArray());

    /// <summary>
    /// A BMP/DIB icon payload: BITMAPINFOHEADER (height doubled), optional palette, bottom-up XOR rows, AND mask.
    /// <paramref name="pixel"/> gives (B, G, R, A) for 24/32 bpp or the palette index for 8 bpp; <paramref name="masked"/>
    /// marks pixels the AND mask makes transparent.
    /// </summary>
    internal static byte[] DibIcon(int width, int height, ushort bits, Func<int, int, byte[]> pixel, Func<int, int, bool> masked,
        byte[][]? palette = null)
    {
        var paletteCount = bits <= 8 ? 1 << bits : 0;
        var xorStride = ((width * bits + 31) / 32) * 4;
        var andStride = ((width + 31) / 32) * 4;
        var dib = new byte[40 + paletteCount * 4 + xorStride * height + andStride * height];
        BinaryPrimitives.WriteUInt32LittleEndian(dib, 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), height * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), bits);
        for (var i = 0; i < paletteCount && palette is not null && i < palette.Length; i++) palette[i].CopyTo(dib, 40 + i * 4);
        var xor = 40 + paletteCount * 4;
        var and = xor + xorStride * height;
        for (var y = 0; y < height; y++)
        {
            var row = xor + (height - 1 - y) * xorStride;
            var maskRow = and + (height - 1 - y) * andStride;
            for (var x = 0; x < width; x++)
            {
                var p = pixel(x, y);
                if (bits == 8) dib[row + x] = p[0];
                else p.AsSpan(0, bits / 8).CopyTo(dib.AsSpan(row + x * (bits / 8)));
                if (masked(x, y)) dib[maskRow + x / 8] |= (byte)(0x80 >> (x % 8));
            }
        }
        return dib;
    }

    /// <summary>Reads a PNG RHI wrote: checks the signature and every chunk CRC, then returns width, height and RGBA rows.</summary>
    internal static (int Width, int Height, byte[] Rgba) ReadPng(byte[] png)
    {
        Assert.True(png.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
        int width = 0, height = 0;
        using var idat = new MemoryStream();
        var at = 8;
        while (at < png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            var type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            var data = png.AsSpan(at + 8, length);
            Assert.Equal(BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at + 8 + length)), Crc(png.AsSpan(at + 4, 4 + length)));
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                Assert.Equal(8, data[8]);
                Assert.Equal(6, data[9]);
            }
            else if (type == "IDAT") idat.Write(data);
            else if (type == "IEND") break;
            at += 12 + length;
        }
        idat.Position = 0;
        using var inflated = new MemoryStream();
        using (var zlib = new ZLibStream(idat, CompressionMode.Decompress)) zlib.CopyTo(inflated);
        var raw = inflated.ToArray();
        Assert.Equal(height * (width * 4 + 1), raw.Length);
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            Assert.Equal(0, raw[y * (width * 4 + 1)]);
            Buffer.BlockCopy(raw, y * (width * 4 + 1) + 1, rgba, y * width * 4, width * 4);
        }
        return (width, height, rgba);
    }

    private static uint Crc(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
