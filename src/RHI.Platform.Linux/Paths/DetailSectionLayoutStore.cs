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
    /// <summary>
    /// w14/layouts-a (#303/#304): upstream 2.9.0 Classic+'s seven sections in its default order: Components, Game Overrides,
    /// Neural Rendering, DLSS / Streamline, Driver Settings, Management, Extras (keys kept from before where a section was
    /// only renamed: ManagedComponents = Components, Runtime = Game Overrides).
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultSectionOrder =
    [
        "ManagedComponents",
        "Runtime",
        "NeuralRendering",
        "DlssStreamline",
        "DriverSettings",
        "Management",
        "Extras"
    ];

    /// <summary>The order every install had before wave 14 (never dragged): it opens in upstream's order instead.</summary>
    public static readonly IReadOnlyList<string> PreviousDefaultSectionOrder =
        ["Runtime", "DlssStreamline", "ReShadeProfile", "ManagedComponents", "Extras"];

    /// <summary>Sections renamed in wave 14: the ReShade profile section became Neural Rendering (its ReShade rows moved to Game Overrides).</summary>
    private static string Migrate(string section)
        => section.Equals("ReShadeProfile", StringComparison.OrdinalIgnoreCase) ? "NeuralRendering" : section;

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
        foreach (var saved in settings.CollapsedSections ?? [])
        {
            var section = saved is null ? null : Migrate(saved);
            if (string.IsNullOrWhiteSpace(section) || !KnownSections.Contains(section) || !collapsedSeen.Add(section))
                continue;
            collapsed.Add(Canonical(section));
        }

        var savedOrder = settings.SectionOrder ?? [];
        if (savedOrder.SequenceEqual(PreviousDefaultSectionOrder, StringComparer.OrdinalIgnoreCase))
            savedOrder = [.. DefaultSectionOrder]; // never dragged: upstream's order

        var order = new List<string>();
        var orderSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var saved in savedOrder)
        {
            var section = saved is null ? null : Migrate(saved);
            if (string.IsNullOrWhiteSpace(section) || !KnownSections.Contains(section) || !orderSeen.Add(section))
                continue;
            order.Add(Canonical(section));
        }

        // A section the saved order lacks goes where upstream's order puts it: after the nearest earlier section present.
        for (var i = 0; i < DefaultSectionOrder.Count; i++)
        {
            var section = DefaultSectionOrder[i];
            if (!orderSeen.Add(section)) continue;
            var at = 0;
            for (var j = i - 1; j >= 0; j--)
            {
                var before = order.IndexOf(DefaultSectionOrder[j]);
                if (before < 0) continue;
                at = before + 1;
                break;
            }
            order.Insert(at, section);
        }

        return new DetailSectionLayoutSettings
        {
            CollapsedSections = collapsed,
            SectionOrder = order
        };
    }

    private static string Canonical(string section)
        => DefaultSectionOrder.First(x => x.Equals(section, StringComparison.OrdinalIgnoreCase));
}
