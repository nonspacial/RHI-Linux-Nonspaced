using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;

namespace RHI.Platform.Linux.Settings;

/// <summary>The notices the user ticked "Don't show this again" on (#285, #278), by key.</summary>
public sealed class DismissedNotices
{
    public List<string> Keys { get; set; } = [];
}

/// <summary>
/// w14/extras-fg (#285): remembers the install notices the user ticked "Don't show this again" on — the closed-source
/// notice of each closed-source mod (RTX Encore, 20/30 FG Unlock, ANR FG) and the Universal MFG ReShade warning (#278).
/// One key per notice, kept in <c>~/.config/rhi-linux/dismissed-notices.json</c> (upstream keeps one bool per notice in its
/// settings file). A missing or unreadable file means "show every notice"; a dismissal that cannot be saved is reported to
/// the caller and the notice shows again next time.
/// </summary>
public sealed class DismissedNoticeStore
{
    private readonly JsonFileStore<DismissedNotices> _file;

    public DismissedNoticeStore(string? path = null)
        => _file = new JsonFileStore<DismissedNotices>(path ?? Path.Combine(LinuxPaths.ConfigHome, "rhi-linux", "dismissed-notices.json"));

    public string FilePath => _file.FilePath;

    /// <summary>Whether <paramref name="key"/> was dismissed. Never throws: an unreadable file counts as nothing dismissed.</summary>
    public bool IsDismissed(string key)
        => _file.Load().Value?.Keys?.Contains(key, StringComparer.OrdinalIgnoreCase) == true;

    /// <summary>Remembers <paramref name="key"/>. Throws <see cref="JsonStoreUnavailableException"/> while an unreadable file is on disk.</summary>
    public void Dismiss(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var current = _file.Load().Value ?? new DismissedNotices();
        var keys = (current.Keys ?? []).Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
        if (keys.Contains(key, StringComparer.OrdinalIgnoreCase)) return;
        keys.Add(key);
        _file.Save(new DismissedNotices { Keys = keys });
    }

    /// <summary>Shows every notice again (Settings' reset). Throws as <see cref="Dismiss"/> does.</summary>
    public void Clear() => _file.Save(new DismissedNotices());
}
