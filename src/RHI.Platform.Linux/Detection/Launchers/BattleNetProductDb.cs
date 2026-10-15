using System.Text;

namespace RHI.Platform.Linux.Detection.Launchers;

/// <summary>
/// Battle.net's install database (<c>ProgramData/Battle.net/Agent/product.db</c>), a protobuf <c>ProductDb</c>
/// (Lutris' <c>product_db.proto</c>): <c>product_installs</c> (1) of <c>ProductInstall</c> { uid (1), product_code (2),
/// settings (3) { install_path (1) }, cached_product_state (4) { base_product_state (1) { installed (1), playable (2) } } }.
/// Only those fields are decoded; anything else is skipped by wire type.
/// </summary>
internal static class BattleNetProductDb
{
    internal const long MaxBytes = 16L * 1024 * 1024;

    /// <summary>One product: Battle.net's own agent/app entries are included (callers skip them by product code).</summary>
    internal sealed record Product(string Uid, string ProductCode, string InstallPath, bool Installed);

    public static IReadOnlyList<Product> Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return [];
        if (info.Length > MaxBytes) throw new InvalidDataException($"Battle.net product.db {path} is larger than {MaxBytes} bytes.");
        return Parse(File.ReadAllBytes(path));
    }

    internal static IReadOnlyList<Product> Parse(ReadOnlySpan<byte> data)
    {
        var products = new List<Product>();
        foreach (var (field, bytes) in LengthDelimited(data))
        {
            if (field != 1) continue;
            string uid = "", code = "", installPath = "";
            var installed = false;
            foreach (var (inner, value) in LengthDelimited(bytes))
            {
                switch (inner)
                {
                    case 1: uid = Text(value); break;
                    case 2: code = Text(value); break;
                    case 3:
                        foreach (var (setting, settingValue) in LengthDelimited(value))
                            if (setting == 1) installPath = Text(settingValue);
                        break;
                    case 4:
                        foreach (var (state, stateValue) in LengthDelimited(value))
                            if (state == 1) installed = Flags(stateValue).Any(flag => flag.Field is 1 && flag.Value != 0);
                        break;
                }
            }
            products.Add(new Product(uid, code, installPath, installed));
        }
        return products;
    }

    private static string Text(byte[] value) => Encoding.UTF8.GetString(value);

    /// <summary>Every length-delimited field of one message as (field number, bytes); other wire types are skipped.</summary>
    private static List<(int Field, byte[] Bytes)> LengthDelimited(ReadOnlySpan<byte> data)
    {
        var fields = new List<(int, byte[])>();
        var position = 0;
        while (position < data.Length)
        {
            var key = Varint(data, ref position);
            var field = (int)(key >> 3);
            switch ((int)(key & 7))
            {
                case 0: Varint(data, ref position); break;
                case 1: position = Advance(data, position, 8); break;
                case 5: position = Advance(data, position, 4); break;
                case 2:
                    var length = Varint(data, ref position);
                    if (length > (ulong)(data.Length - position)) throw new InvalidDataException("product.db: field runs past the end.");
                    fields.Add((field, data.Slice(position, (int)length).ToArray()));
                    position += (int)length;
                    break;
                default: throw new InvalidDataException($"product.db: unsupported wire type {key & 7}.");
            }
        }
        return fields;
    }

    /// <summary>The varint fields of one message (booleans such as <c>installed</c>).</summary>
    private static List<(int Field, ulong Value)> Flags(ReadOnlySpan<byte> data)
    {
        var flags = new List<(int, ulong)>();
        var position = 0;
        while (position < data.Length)
        {
            var key = Varint(data, ref position);
            switch ((int)(key & 7))
            {
                case 0: flags.Add(((int)(key >> 3), Varint(data, ref position))); break;
                case 1: position = Advance(data, position, 8); break;
                case 5: position = Advance(data, position, 4); break;
                case 2:
                    var length = Varint(data, ref position);
                    if (length > (ulong)(data.Length - position)) throw new InvalidDataException("product.db: field runs past the end.");
                    position += (int)length;
                    break;
                default: throw new InvalidDataException($"product.db: unsupported wire type {key & 7}.");
            }
        }
        return flags;
    }

    private static int Advance(ReadOnlySpan<byte> data, int position, int count)
        => position + count <= data.Length ? position + count : throw new InvalidDataException("product.db: field runs past the end.");

    private static ulong Varint(ReadOnlySpan<byte> data, ref int position)
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (position >= data.Length) throw new InvalidDataException("product.db: truncated varint.");
            var b = data[position++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
        }
        throw new InvalidDataException("product.db: varint too long.");
    }
}
