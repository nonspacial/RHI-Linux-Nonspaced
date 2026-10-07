using RHI.Platform.Linux.Persistence;

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
    private readonly JsonFileStore<DetailSectionLayoutSettings> _file;

    public DetailSectionLayoutStore(string? path = null)
    {
        _path = path ?? System.IO.Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "ui-settings.json");
        _file = new JsonFileStore<DetailSectionLayoutSettings>(_path);
    }

    public string Path => _path;

    /// <summary>Never throws: a missing or unreadable file yields the default layout (an unreadable one is copied aside).</summary>
    public DetailSectionLayoutSettings Load() => Normalize(_file.Load().Value);

    /// <summary>Throws <see cref="JsonStoreUnavailableException"/> while an unreadable file is on disk.</summary>
    public void Save(DetailSectionLayoutSettings settings) => _file.Save(Normalize(settings));

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
