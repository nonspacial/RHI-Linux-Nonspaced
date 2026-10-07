namespace RHI.Platform.Linux.Persistence;

public static class JsonStoreMaps
{
    /// <summary>
    /// Case-insensitive copy of a per-game map read from a store file. Keys that differ only in
    /// case (a hand edit; RHI never writes them) are merged instead of throwing: the entry later in
    /// the file wins, as a repeated key does in JSON. The next save writes the merged map.
    /// </summary>
    public static Dictionary<string, T> CaseInsensitive<T>(IEnumerable<KeyValuePair<string, T>>? source)
    {
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        if (source is null) return result;
        foreach (var (key, value) in source)
        {
            if (key is null) continue;
            result.Remove(key); // keep the later spelling as well as the later value
            result[key] = value;
        }
        return result;
    }
}
