using System.Text.Json;

namespace RHI.Platform.Linux.Paths;

/// <summary>
/// Persistent, Linux-native detail panel layout state. Mirrors the upstream RHI
/// collapsible/reorderable section concept without depending on WinUI settings APIs.
/// </summary>
public sealed class DetailSectionLayoutSettings
{
    public List<string> CollapsedSections { get; set; } = [];
    public List<string> SectionOrder { get; set; } = [];
}

public sealed class DetailSectionLayoutStore
{
    public static readonly IReadOnlyList<string> DefaultSectionOrder =
    [
        "Runtime",
        "DlssStreamline",
        "ReShadeProfile",
        "ManagedComponents",
        "Extras"
    ];

    private static readonly HashSet<string> KnownSections =
        new(DefaultSectionOrder, StringComparer.OrdinalIgnoreCase);

    private readonly string _path;

    public DetailSectionLayoutStore(string? path = null)
    {
        _path = path ?? System.IO.Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "ui-settings.json");
    }

    public string Path => _path;

    public DetailSectionLayoutSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
                return Normalize(new DetailSectionLayoutSettings());

            var json = File.ReadAllText(_path);
            var settings = JsonSerializer.Deserialize<DetailSectionLayoutSettings>(json)
                           ?? new DetailSectionLayoutSettings();
            return Normalize(settings);
        }
        catch
        {
            // A corrupt view-state file must never prevent the app from launching.
            return Normalize(new DetailSectionLayoutSettings());
        }
    }

    public void Save(DetailSectionLayoutSettings settings)
    {
        var normalized = Normalize(settings);
        var directory = System.IO.Path.GetDirectoryName(_path)
                        ?? throw new InvalidOperationException("UI settings path has no parent directory.");
        Directory.CreateDirectory(directory);

        var tempPath = _path + ".tmp";
        var json = JsonSerializer.Serialize(normalized, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _path, overwrite: true);
    }

    public static DetailSectionLayoutSettings Normalize(DetailSectionLayoutSettings? settings)
    {
        settings ??= new DetailSectionLayoutSettings();

        var collapsed = new List<string>();
        var collapsedSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in settings.CollapsedSections ?? [])
        {
            if (string.IsNullOrWhiteSpace(section) || !KnownSections.Contains(section) || !collapsedSeen.Add(section))
                continue;
            collapsed.Add(Canonical(section));
        }

        var order = new List<string>();
        var orderSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in settings.SectionOrder ?? [])
        {
            if (string.IsNullOrWhiteSpace(section) || !KnownSections.Contains(section) || !orderSeen.Add(section))
                continue;
            order.Add(Canonical(section));
        }

        foreach (var section in DefaultSectionOrder)
            if (orderSeen.Add(section))
                order.Add(section);

        return new DetailSectionLayoutSettings
        {
            CollapsedSections = collapsed,
            SectionOrder = order
        };
    }

    private static string Canonical(string section)
        => DefaultSectionOrder.First(x => x.Equals(section, StringComparison.OrdinalIgnoreCase));
}
