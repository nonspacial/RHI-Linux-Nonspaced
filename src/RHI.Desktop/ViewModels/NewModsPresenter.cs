using CommunityToolkit.Mvvm.ComponentModel;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Persistence;

namespace RHI.Desktop.ViewModels;

/// <summary>Where the "New mods" notice reads its lists (#185); each source may fail or come back empty (then it is skipped).</summary>
public sealed record NewModsSources(
    SeenModsStore Store,
    Func<CancellationToken, Task<IReadOnlyList<string>>> RenoDx,
    Func<CancellationToken, Task<IReadOnlyList<string>>> Luma,
    Func<CancellationToken, Task<IReadOnlyList<string>>> UltraPlus);

/// <summary>One list section of the "New mods" dialog.</summary>
public sealed record NewModsSection(string Heading, IReadOnlyList<string> Names, string? More);

/// <summary>
/// The "New mods" toolbar button and list (#185; upstream <c>SeenWikiModsService</c>, <c>SeenLumaModsService</c>,
/// <c>SeenUltraPlusModsService</c>, <c>NewModsButton_Click</c>): RenoDX mods with a download or a Nexus page, Luma mods and
/// Ultra+ games that were not in their list the last time the user marked the lists as seen. The first time a list is read
/// it is only remembered (no notice on first start, as upstream); a list that cannot be read is skipped and never
/// remembered empty. The button exists only while something is new (owner rule: hidden, never disabled); "Mark as seen"
/// remembers them and hides it, "Close" keeps it.
/// </summary>
public sealed partial class NewModsPresenter : ObservableObject
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);
    private const int ShownPerSection = 30;

    private SeenModsStore? _store;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNewMods), nameof(IsHidden), nameof(ButtonTip))]
    private IReadOnlyList<string> newRenoDx = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNewMods), nameof(IsHidden), nameof(ButtonTip))]
    private IReadOnlyList<string> newLuma = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNewMods), nameof(IsHidden), nameof(ButtonTip))]
    private IReadOnlyList<string> newUltraPlus = [];

    public bool HasNewMods => NewRenoDx.Count + NewLuma.Count + NewUltraPlus.Count > 0;

    /// <summary>The toolbar button is left out while nothing is new.</summary>
    public bool IsHidden => !HasNewMods;

    public string ButtonText => T("Cat.NewModsButton");

    public string ButtonTip => TF("Cat.NewModsTip", NewRenoDx.Count + NewLuma.Count + NewUltraPlus.Count);

    /// <summary>Set by the window: shows the list; true when the user chose "Mark as seen".</summary>
    public Func<IReadOnlyList<NewModsSection>, Task<bool>>? Show { get; set; }

    /// <summary>
    /// Reads every list once the library has loaded and works out what is new; seeds a list the first time it is read.
    /// Never throws (a failed list is logged and skipped). Call on the UI thread.
    /// </summary>
    public async Task CheckAsync(Task initialization, NewModsSources sources, CancellationToken cancellationToken = default)
    {
        await initialization.ConfigureAwait(true);
        _store = sources.Store;
        var renoDx = await ReadAsync(sources.RenoDx, "RenoDX", cancellationToken).ConfigureAwait(true);
        var luma = await ReadAsync(sources.Luma, "Luma", cancellationToken).ConfigureAwait(true);
        var ultraPlus = await ReadAsync(sources.UltraPlus, "Ultra+", cancellationToken).ConfigureAwait(true);
        if (sources.Store.Load() is not { } seen)
        {
            AppLog.Warn("new-mods", $"Seen-mods file unreadable; no new-mods notice this session: {sources.Store.FilePath}");
            return;
        }
        var seeded = Seed(seen.RenoDx, renoDx) | Seed(seen.Luma, luma) | Seed(seen.UltraPlus, ultraPlus);
        if (seeded && ParityPersistence.TryPersist(() => sources.Store.Save(seen), "Cat.WhatSeenMods") is { } failed)
            AppLog.Warn("new-mods", failed);
        NewRenoDx = Unseen(renoDx, seen.RenoDx);
        NewLuma = Unseen(luma, seen.Luma);
        NewUltraPlus = Unseen(ultraPlus, seen.UltraPlus);
    }

    /// <summary>The dialog's sections (RenoDX, Ultra+, Luma as upstream), at most 30 names each plus "… and N more".</summary>
    public IReadOnlyList<NewModsSection> Sections()
    {
        var sections = new List<NewModsSection>();
        void Add(IReadOnlyList<string> names, string key)
        {
            if (names.Count == 0) return;
            sections.Add(new(TF(key, names.Count), names.Take(ShownPerSection).ToArray(),
                names.Count > ShownPerSection ? TF("Cat.NewModsMore", names.Count - ShownPerSection) : null));
        }
        Add(NewRenoDx, "Cat.NewModsRenoDx");
        Add(NewUltraPlus, "Cat.NewModsUltraPlus");
        Add(NewLuma, "Cat.NewModsLuma");
        return sections;
    }

    /// <summary>Opens the list; "Mark as seen" remembers every listed mod. Returns a status line when that could not be saved.</summary>
    public async Task<string?> OpenAsync()
    {
        if (!HasNewMods || Show is null) return null;
        return await Show(Sections()) ? MarkSeen() : null;
    }

    /// <summary>Remembers every mod now listed and hides the button; the localised failure line when the file could not be written.</summary>
    public string? MarkSeen()
    {
        if (_store is null || _store.Load() is not { } seen) return T("Cat.NewModsNotSaved");
        seen.RenoDx.AddRange(NewRenoDx);
        seen.Luma.AddRange(NewLuma);
        seen.UltraPlus.AddRange(NewUltraPlus);
        if (ParityPersistence.TryPersist(() => _store.Save(seen), "Cat.WhatSeenMods") is { } failed) return failed;
        NewRenoDx = [];
        NewLuma = [];
        NewUltraPlus = [];
        return null;
    }

    private static async Task<IReadOnlyList<string>> ReadAsync(Func<CancellationToken, Task<IReadOnlyList<string>>> read, string what,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await read(cancellationToken).ConfigureAwait(true))
                .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Warn("new-mods", $"{what} list unavailable for the new-mods notice", ex);
            return [];
        }
    }

    /// <summary>Upstream <c>SeedIfEmpty</c>: a list read for the first time is remembered whole, so nothing in it counts as new.</summary>
    private static bool Seed(List<string> seen, IReadOnlyList<string> current)
    {
        if (seen.Count > 0 || current.Count == 0) return false;
        seen.AddRange(current);
        return true;
    }

    private static IReadOnlyList<string> Unseen(IReadOnlyList<string> current, List<string> seen)
    {
        var known = new HashSet<string>(seen, StringComparer.OrdinalIgnoreCase);
        return current.Where(name => !known.Contains(name)).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
