using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;

namespace RHI.Platform.Linux.Detection;

/// <summary>
/// Read-only reader for tables of a small SQLite 3 database (Lutris' <c>pga.db</c>, the itch app's <c>butler.db</c>), so
/// the library does not need a native SQLite dependency. Supports UTF-8 databases, table b-trees (interior and leaf pages),
/// overflow pages and WAL mode: the committed transactions in <c>&lt;db&gt;-wal</c> (salt- and checksum-valid frames up to
/// the last commit frame, as SQLite's own recovery reads them) are laid over the main file, so a database whose app is
/// running reads as that app sees it. The main file and the <c>-wal</c> file are read as one snapshot: when a checkpoint
/// restarts the <c>-wal</c> file meanwhile, the read is repeated once, quietly. Anything unexpected (a newer format, a
/// damaged or half-written file, a database over the size cap, a <c>-wal</c> file that is not one) yields no rows; the
/// caller says what it does instead.
/// </summary>
internal static class SqliteTableReader
{
    private const int MaxDatabaseBytes = 64 * 1024 * 1024;
    private const int WalHeaderBytes = 32;
    private const int WalFrameHeaderBytes = 24;

    /// <summary>Test seam: runs (per database path) between reading the main file and reading its <c>-wal</c> file.</summary>
    internal static readonly ConcurrentDictionary<string, Action> AfterMainFileReadForTesting = new(StringComparer.Ordinal);

    /// <summary>One table; on failure a scan warning (Lutris' wording: it falls back to its config files) and no rows.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadTable(string path, string table)
    {
        if (TryReadTables(path, [table], out var tables, out var error)) return tables[table];
        RHI.Core.Services.DetectorDiagnostics.Warn($"SQLite table '{table}' in {path} not read; using the config files alone", error);
        return [];
    }

    /// <summary>
    /// Several tables from one snapshot of the database. A missing database or table reads as no rows. False (with the
    /// reason) when the database cannot be read; the caller decides how to report it.
    /// </summary>
    public static bool TryReadTables(string path, IReadOnlyList<string> names,
        out IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>> tables, out Exception? error)
    {
        var result = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names) result[name] = [];
        tables = result;
        error = null;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Snapshot(path) is not { } bytes) return true;
                var db = new Database(bytes);
                var master = db.Rows(1).ToList();
                foreach (var name in names) result[name] = Read(db, master, name);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                           or IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException)
            {
                // One quiet retry: the owning app may have been writing (a checkpoint, a -wal restart) during the read.
                if (attempt == 0) continue;
                foreach (var name in names) result[name] = [];
                error = ex;
                return false;
            }
        }
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Read(Database db, IReadOnlyList<IReadOnlyList<object?>> master, string table)
    {
        var entry = master.FirstOrDefault(row =>
            row.Count >= 5 && row[0] as string == "table" && string.Equals(row[1] as string, table, StringComparison.OrdinalIgnoreCase));
        if (entry is null || entry[3] is not long rootPage || entry[4] is not string sql) return [];
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

    /// <summary>
    /// The database as its writer sees it: the main file with the <c>-wal</c> file's committed pages laid over it. Null when
    /// the database does not exist. Throws when the files changed under the read (the caller retries once) or are no
    /// SQLite database.
    /// </summary>
    private static byte[]? Snapshot(string path)
    {
        var walPath = path + "-wal";
        var walBefore = ReadCapped(walPath, WalHeaderBytes, whole: false);
        var info = new FileInfo(path);
        if (!info.Exists) return null;
        if (info.Length > MaxDatabaseBytes) throw new InvalidDataException($"unexpected database size {info.Length} bytes");
        var bytes = ReadCapped(path, MaxDatabaseBytes) ?? throw new FileNotFoundException("database vanished while it was read", path);
        if (AfterMainFileReadForTesting.TryGetValue(path, out var hook)) hook();
        // WAL mode (read/write versions 2): committed rows can still sit in <db>-wal, so the main file alone may be stale
        // (an `installed = 0` row would hide a reinstalled game).
        var walMode = bytes.Length > 19 && (bytes[18] == 2 || bytes[19] == 2);
        if (walMode)
        {
            var wal = ReadCapped(walPath, MaxDatabaseBytes);
            // Pages the checkpoint copied into the main file during the read all come from this -wal generation, which is
            // laid over them again; a new generation (another salt, a truncated file) means the main file may be newer
            // than the -wal file just read.
            if (!SameHeader(walBefore, wal)) throw new InvalidDataException("WAL-mode database whose -wal file was restarted while it was read");
            if (wal is { Length: > 0 }) bytes = ApplyWal(bytes, wal);
        }
        if (bytes.Length < 512) throw new InvalidDataException($"unexpected database size {bytes.Length} bytes");
        return bytes;
    }

    /// <summary>The file's bytes (at most <paramref name="cap"/>; a longer file throws when <paramref name="whole"/>), null when it is missing.</summary>
    private static byte[]? ReadCapped(string path, int cap, bool whole = true)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (whole && stream.Length > cap) throw new InvalidDataException($"{path} is larger than {cap} bytes");
            var buffer = new byte[Math.Min(stream.Length, cap)];
            var read = 0;
            while (read < buffer.Length && stream.Read(buffer, read, buffer.Length - read) is var n && n > 0) read += n;
            return read == buffer.Length ? buffer : buffer[..read];
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static bool SameHeader(byte[]? before, byte[]? after)
    {
        var a = before is null ? [] : before.AsSpan(0, Math.Min(before.Length, WalHeaderBytes));
        var b = after is null ? [] : after.AsSpan(0, Math.Min(after.Length, WalHeaderBytes));
        return a.SequenceEqual(b);
    }

    /// <summary>
    /// Lays the committed frames of a <c>-wal</c> file over the main file (SQLite's WAL format: 32-byte header with magic,
    /// version, page size, salts and checksum; frames of a 24-byte header plus one page; a frame counts only while its
    /// salts match and the running checksum holds, and only up to the last commit frame).
    /// </summary>
    internal static byte[] ApplyWal(byte[] database, byte[] wal)
    {
        if (wal.Length < WalHeaderBytes) throw new InvalidDataException("WAL-mode database with a -wal file shorter than its header");
        var magic = BinaryPrimitives.ReadUInt32BigEndian(wal);
        if (magic is not (0x377f0682 or 0x377f0683)) throw new InvalidDataException("WAL-mode database whose -wal file is not a SQLite WAL");
        var bigEndian = (magic & 1) == 1;
        if (BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(4)) != 3007000) throw new InvalidDataException("WAL file of an unknown version");
        var pageSize = BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(8));
        if (pageSize is < 512 or > 65536 || (pageSize & (pageSize - 1)) != 0) throw new InvalidDataException("WAL file with a bad page size");
        if (database.Length >= 18)
        {
            int mainPageSize = BinaryPrimitives.ReadUInt16BigEndian(database.AsSpan(16));
            if ((mainPageSize == 1 ? 65536 : mainPageSize) != pageSize) throw new InvalidDataException("WAL page size differs from the database's");
        }
        var (s0, s1) = Checksum(wal.AsSpan(0, 24), bigEndian, 0, 0);
        if (s0 != BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(24)) || s1 != BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(28)))
            throw new InvalidDataException("WAL-mode database whose -wal header checksum does not match");
        var salt1 = BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(16));
        var salt2 = BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(20));

        var frameSize = WalFrameHeaderBytes + (int)pageSize;
        var lastCommit = -1;
        uint pagesAtCommit = 0;
        for (var frame = 0; WalHeaderBytes + (long)(frame + 1) * frameSize <= wal.Length; frame++)
        {
            var header = wal.AsSpan(WalHeaderBytes + frame * frameSize, WalFrameHeaderBytes);
            if (BinaryPrimitives.ReadUInt32BigEndian(header) == 0 ||
                BinaryPrimitives.ReadUInt32BigEndian(header[8..]) != salt1 || BinaryPrimitives.ReadUInt32BigEndian(header[12..]) != salt2)
                break;
            (s0, s1) = Checksum(header[..8], bigEndian, s0, s1);
            (s0, s1) = Checksum(wal.AsSpan(WalHeaderBytes + frame * frameSize + WalFrameHeaderBytes, (int)pageSize), bigEndian, s0, s1);
            if (s0 != BinaryPrimitives.ReadUInt32BigEndian(header[16..]) || s1 != BinaryPrimitives.ReadUInt32BigEndian(header[20..])) break;
            if (BinaryPrimitives.ReadUInt32BigEndian(header[4..]) is var commit and > 0)
            {
                lastCommit = frame;
                pagesAtCommit = commit;
            }
        }
        if (lastCommit < 0) return database;
        if ((long)pagesAtCommit * pageSize > MaxDatabaseBytes) throw new InvalidDataException("WAL commit makes the database larger than the cap");

        var image = new byte[pagesAtCommit * pageSize];
        database.AsSpan(0, Math.Min(database.Length, image.Length)).CopyTo(image);
        for (var frame = 0; frame <= lastCommit; frame++)
        {
            var offset = WalHeaderBytes + frame * frameSize;
            var page = BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(offset));
            if (page > pagesAtCommit) continue;
            wal.AsSpan(offset + WalFrameHeaderBytes, (int)pageSize).CopyTo(image.AsSpan((int)((page - 1) * pageSize)));
        }
        return image;
    }

    private static (uint, uint) Checksum(ReadOnlySpan<byte> data, bool bigEndian, uint s0, uint s1)
    {
        unchecked
        {
            for (var i = 0; i + 8 <= data.Length; i += 8)
            {
                var x0 = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data[i..]) : BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
                var x1 = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data[(i + 4)..]) : BinaryPrimitives.ReadUInt32LittleEndian(data[(i + 4)..]);
                s0 += x0 + s1;
                s1 += x1 + s0;
            }
        }
        return (s0, s1);
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

    // An INTEGER PRIMARY KEY column is an alias of the rowid and is stored as NULL in the record. So is an INTEGER column
    // that is the table's only key in a `PRIMARY KEY ("id")` constraint (the form butler's tables use, Refs #90).
    internal static string? IntegerPrimaryKey(string sql, IReadOnlyList<string> columns)
    {
        const System.Text.RegularExpressions.RegexOptions IgnoreCase = System.Text.RegularExpressions.RegexOptions.IgnoreCase;
        static string Quoted(string column) => $@"[""`\[]?{System.Text.RegularExpressions.Regex.Escape(column)}[""`\]]?";
        var inline = columns.FirstOrDefault(column =>
            System.Text.RegularExpressions.Regex.IsMatch(sql, $@"[(,]\s*{Quoted(column)}\s+INTEGER\s+PRIMARY\s+KEY", IgnoreCase));
        if (inline is not null) return inline;
        var constraint = System.Text.RegularExpressions.Regex.Match(sql, @"PRIMARY\s+KEY\s*\(\s*([^,()]+?)\s*\)", IgnoreCase);
        if (!constraint.Success) return null;
        var key = constraint.Groups[1].Value.Trim('"', '`', '[', ']', '\'');
        return columns.FirstOrDefault(column => column.Equals(key, StringComparison.OrdinalIgnoreCase) &&
            System.Text.RegularExpressions.Regex.IsMatch(sql, $@"[(,]\s*{Quoted(column)}\s+INTEGER\s*(NOT\s+NULL\s*)?[,)]", IgnoreCase));
    }

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
