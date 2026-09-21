namespace RHI.Platform.Linux.Deployment;

internal static class OptiScalerIniEditor
{
    public static void SetLoadReShade(string binaryDirectory, bool enabled)
        => SetValue(binaryDirectory, "Plugins", "LoadReshade", enabled ? "true" : "false");

    public static string? GetValue(string binaryDirectory, string section, string key)
    {
        var path = Path.Combine(binaryDirectory, "OptiScaler.ini");
        if (!File.Exists(path))
            throw new FileNotFoundException("OptiScaler.ini was not found beside the game executable.", path);
        return GetValueFromLines(File.ReadAllLines(path), section, key);
    }

    public static void SetValue(string binaryDirectory, string section, string key, string value)
        => SetValues(binaryDirectory, [(section, key, value)]);

    public static void SetValues(
        string binaryDirectory,
        IReadOnlyList<(string Section, string Key, string Value)> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        if (updates.Count == 0) return;

        var path = Path.Combine(binaryDirectory, "OptiScaler.ini");
        if (!File.Exists(path))
            throw new FileNotFoundException("OptiScaler.ini was not found beside the game executable.", path);

        var text = File.ReadAllText(path);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();

        foreach (var update in updates)
        {
            if (string.IsNullOrWhiteSpace(update.Section))
                throw new ArgumentException("INI section cannot be empty.", nameof(updates));
            if (string.IsNullOrWhiteSpace(update.Key))
                throw new ArgumentException("INI key cannot be empty.", nameof(updates));
            SetValueInLines(lines, update.Section.Trim(), update.Key.Trim(), update.Value ?? "");
        }

        AtomicWrite(path, string.Join(newline, lines));
    }

    private static string? GetValueFromLines(IReadOnlyList<string> lines, string section, string key)
    {
        var inSection = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                inSection = line[1..^1].Trim().Equals(section, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            if (!line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            return line[(eq + 1)..].Trim();
        }
        return null;
    }

    private static void SetValueInLines(List<string> lines, string section, string key, string value)
    {
        var sectionStart = -1;
        var sectionEnd = lines.Count;
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (!trimmed.Equals($"[{section}]", StringComparison.OrdinalIgnoreCase)) continue;
            sectionStart = i;
            for (var j = i + 1; j < lines.Count; j++)
            {
                if (!lines[j].TrimStart().StartsWith("[", StringComparison.Ordinal)) continue;
                sectionEnd = j;
                break;
            }
            break;
        }

        if (sectionStart < 0)
        {
            if (lines.Count > 0 && lines[^1].Length != 0) lines.Add("");
            lines.Add($"[{section}]");
            lines.Add($"{key}={value}");
            return;
        }

        for (var i = sectionStart + 1; i < sectionEnd; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith(';') || trimmed.StartsWith('#')) continue;
            var eq = trimmed.IndexOf('=');
            if (eq <= 0 || !trimmed[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            lines[i] = $"{key}={value}";
            return;
        }

        lines.Insert(sectionEnd, $"{key}={value}");
    }

    private static void AtomicWrite(string path, string text)
    {
        var temp = path + $".rhi-tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }
}
