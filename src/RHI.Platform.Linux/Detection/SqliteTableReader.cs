using System.Buffers.Binary;
using System.Text;

namespace RHI.Platform.Linux.Detection;

/// <summary>
/// Read-only reader for one table of a small SQLite 3 database (Lutris' <c>pga.db</c>), so the library
/// does not need a native SQLite dependency. Supports UTF-8 databases, table b-trees (interior and leaf
/// pages) and overflow pages. Anything unexpected (a newer format, a damaged or half-written file, a
/// database over the size cap) yields no rows, and callers fall back to the config files alone.
/// </summary>
internal static class SqliteTableReader
{
    private const int MaxDatabaseBytes = 64 * 1024 * 1024;

    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadTable(string path, string table)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 512 || info.Length > MaxDatabaseBytes) return [];
            var db = new Database(File.ReadAllBytes(path));

            var master = db.Rows(1).FirstOrDefault(row =>
                row.Count >= 5 && row[0] as string == "table" && string.Equals(row[1] as string, table, StringComparison.OrdinalIgnoreCase));
            if (master is null || master[3] is not long rootPage || master[4] is not string sql) return [];
            var columns = ColumnNames(sql);
            var rowIdColumn = IntegerPrimaryKey(sql, columns);

            var result = new List<IReadOnlyDictionary<string, object?>>();
            foreach (var (rowId, values) in db.RowsWithIds((int)rootPage))
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var c = 0; c < columns.Count; c++)
                    row[columns[c]] = c < values.Count ? values[c] : null;
                if (rowIdColumn is not null) row[rowIdColumn] = rowId;
                result.Add(row);
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>Column names of a <c>CREATE TABLE name (col type, …, constraint …)</c> statement.</summary>
    internal static IReadOnlyList<string> ColumnNames(string createSql)
    {
        var open = createSql.IndexOf('(');
        var close = createSql.LastIndexOf(')');
        if (open < 0 || close <= open) throw new InvalidDataException("CREATE TABLE without a column list");
        var names = new List<string>();
        var depth = 0;
        var start = open + 1;
        for (var i = open + 1; i <= close; i++)
        {
            var ch = createSql[i];
            if (ch == '(') depth++;
            else if (ch == ')' && i < close) depth--;
            else if ((ch == ',' && depth == 0) || i == close)
            {
                var part = createSql[start..i].Trim();
                start = i + 1;
                var first = part.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
                if (first.Length == 0 || IsConstraint(first)) continue;
                names.Add(first.Trim('"', '`', '[', ']', '\''));
            }
        }
        return names;
    }

    private static bool IsConstraint(string word)
        => word.ToUpperInvariant() is "PRIMARY" or "UNIQUE" or "CHECK" or "FOREIGN" or "CONSTRAINT";

    // An INTEGER PRIMARY KEY column is an alias of the rowid and is stored as NULL in the record.
    private static string? IntegerPrimaryKey(string sql, IReadOnlyList<string> columns)
        => columns.FirstOrDefault(column =>
            System.Text.RegularExpressions.Regex.IsMatch(sql,
                $@"[(,]\s*[""`\[]?{System.Text.RegularExpressions.Regex.Escape(column)}[""`\]]?\s+INTEGER\s+PRIMARY\s+KEY",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase));

    private sealed class Database
    {
        private readonly byte[] _data;
        private readonly int _pageSize;
        private readonly int _usable;

        public Database(byte[] data)
        {
            if (!Encoding.ASCII.GetString(data, 0, 16).Equals("SQLite format 3\0", StringComparison.Ordinal))
                throw new InvalidDataException("not a SQLite 3 database");
            int pageSize = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(16));
            _pageSize = pageSize == 1 ? 65536 : pageSize;
            if (_pageSize < 512 || (_pageSize & (_pageSize - 1)) != 0) throw new InvalidDataException("bad page size");
            _usable = _pageSize - data[20];
            if (BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(56)) is not (0 or 1))
                throw new InvalidDataException("only UTF-8 databases are read");
            _data = data;
        }

        public IEnumerable<IReadOnlyList<object?>> Rows(int rootPage) => RowsWithIds(rootPage).Select(r => r.Values);

        public IEnumerable<(long RowId, IReadOnlyList<object?> Values)> RowsWithIds(int rootPage)
        {
            var visited = new HashSet<int>();
            var pending = new Stack<int>();
            pending.Push(rootPage);
            var rows = new List<(long, IReadOnlyList<object?>)>();
            while (pending.Count > 0)
            {
                var page = pending.Pop();
                if (!visited.Add(page)) throw new InvalidDataException("b-tree cycle");
                var offset = PageOffset(page);
                var header = page == 1 ? offset + 100 : offset;
                var type = _data[header];
                var cellCount = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(header + 3));
                var cellPointers = header + (type == 0x05 ? 12 : 8);
                if (type == 0x05)
                {
                    // Interior table page: push children right to left so rows come out in rowid order.
                    pending.Push((int)BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(header + 8)));
                    for (var c = cellCount - 1; c >= 0; c--)
                    {
                        var cell = offset + BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(cellPointers + c * 2));
                        pending.Push((int)BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(cell)));
                    }
                }
                else if (type == 0x0D)
                {
                    for (var c = 0; c < cellCount; c++)
                    {
                        var cell = offset + BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(cellPointers + c * 2));
                        var payloadSize = ReadVarint(ref cell);
                        var rowId = ReadVarint(ref cell);
                        rows.Add((rowId, Record(Payload(cell, payloadSize))));
                    }
                }
                else throw new InvalidDataException($"unexpected page type {type}");
            }
            return rows;
        }

        private int PageOffset(int page)
        {
            var offset = (long)(page - 1) * _pageSize;
            if (page < 1 || offset + _pageSize > _data.Length) throw new InvalidDataException("page out of range");
            return (int)offset;
        }

        private byte[] Payload(int cell, long size)
        {
            if (size < 0 || size > MaxDatabaseBytes) throw new InvalidDataException("bad payload size");
            var maxLocal = _usable - 35;
            if (size <= maxLocal) return _data.AsSpan(cell, (int)size).ToArray();

            var minLocal = (_usable - 12) * 32 / 255 - 23;
            var local = minLocal + (int)((size - minLocal) % (_usable - 4));
            if (local > maxLocal) local = minLocal;
            var payload = new byte[size];
            _data.AsSpan(cell, local).CopyTo(payload);
            var written = local;
            var next = (int)BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(cell + local));
            var visited = new HashSet<int>();
            while (written < size)
            {
                if (next == 0 || !visited.Add(next)) throw new InvalidDataException("broken overflow chain");
                var offset = PageOffset(next);
                var chunk = (int)Math.Min(_usable - 4, size - written);
                _data.AsSpan(offset + 4, chunk).CopyTo(payload.AsSpan(written));
                written += chunk;
                next = (int)BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(offset));
            }
            return payload;
        }

        private static IReadOnlyList<object?> Record(byte[] payload)
        {
            var position = 0;
            var headerSize = ReadVarint(payload, ref position);
            var types = new List<long>();
            while (position < headerSize) types.Add(ReadVarint(payload, ref position));
            var body = (int)headerSize;
            var values = new List<object?>(types.Count);
            foreach (var type in types)
            {
                switch (type)
                {
                    case 0: values.Add(null); break;
                    case >= 1 and <= 6:
                        var width = type switch { 5 => 6, 6 => 8, _ => (int)type == 4 ? 4 : (int)type };
                        long value = (sbyte)payload[body];
                        for (var b = 1; b < width; b++) value = (value << 8) | payload[body + b];
                        values.Add(value);
                        body += width;
                        break;
                    case 7: values.Add(BinaryPrimitives.ReadDoubleBigEndian(payload.AsSpan(body))); body += 8; break;
                    case 8: values.Add(0L); break;
                    case 9: values.Add(1L); break;
                    case >= 12 when type % 2 == 0:
                        var blob = (int)((type - 12) / 2);
                        values.Add(payload.AsSpan(body, blob).ToArray());
                        body += blob;
                        break;
                    case >= 13:
                        var text = (int)((type - 13) / 2);
                        values.Add(Encoding.UTF8.GetString(payload, body, text));
                        body += text;
                        break;
                    default: throw new InvalidDataException($"reserved serial type {type}");
                }
            }
            return values;
        }

        private long ReadVarint(ref int position) => ReadVarint(_data, ref position);

        private static long ReadVarint(byte[] data, ref int position)
        {
            long value = 0;
            for (var i = 0; i < 9; i++)
            {
                var b = data[position++];
                if (i == 8) return (value << 8) | b;
                value = (value << 7) | (long)(b & 0x7f);
                if ((b & 0x80) == 0) return value;
            }
            return value;
        }
    }
}
