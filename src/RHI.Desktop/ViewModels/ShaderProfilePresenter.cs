using RHI.Core.Deployment;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// Named shader-selection profiles (#93; upstream shader picker's profile panel): save the packs ticked in the
/// Shaders/Add-ons dialog with their per-pack file exclusions under a name, load one back (ticks and exclusions),
/// rename, delete, export a zip to share (written into RHI's own folder, which is then opened — Linux's "share") and
/// import a profile from such a zip. Profiles are global, as upstream's; loading one only ticks boxes — nothing reaches a
/// game until the dialog's Apply.
/// </summary>
public sealed class ShaderProfilePresenter
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    private readonly ShaderSelectionProfileStore _store;
    private readonly ReShadeShaderPackService _packs;
    private readonly string _exportFolder;
    private readonly Func<DateTime> _now;
    private readonly Func<string, (bool Opened, string? Error)> _openFolder;

    public ShaderProfilePresenter(ShaderSelectionProfileStore store, ReShadeShaderPackService packs, string? exportFolder = null,
        Func<DateTime>? now = null, Func<string, (bool Opened, string? Error)>? openFolder = null)
    {
        _store = store;
        _packs = packs;
        _exportFolder = exportFolder ?? ShaderProfileArchive.DefaultFolder;
        _now = now ?? (() => DateTime.Now);
        _openFolder = openFolder ?? (folder => (LinuxDesktopOpener.TryOpenDirectory(folder, out var error), error));
    }

    public IReadOnlyList<string> Names => _store.Load().Select(p => p.Name).ToList();

    private ShaderSelectionProfile? Find(string? name)
        => name is null ? null : _store.Load().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Saves <paramref name="selectedPacks"/> (with each pack's current file exclusions) under <paramref name="name"/>:
    /// an existing name is overwritten, a new one added; blank = "Profile N". Returns the status line and the name saved.
    /// </summary>
    public (string Status, string? Saved) Save(string? name, IReadOnlyCollection<string> selectedPacks)
    {
        var profiles = _store.Load().ToList();
        var target = string.IsNullOrWhiteSpace(name) ? NextName(profiles) : name.Trim();
        var profile = new ShaderSelectionProfile
        {
            Name = target,
            SelectedPacks = selectedPacks.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            FileExclusions = selectedPacks
                .Select(id => (id, excluded: _packs.GetExcludedFiles(id)))
                .Where(x => x.excluded.Count > 0)
                .ToDictionary(x => x.id, x => x.excluded.Order(StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase)
        };
        var index = profiles.FindIndex(p => p.Name.Equals(target, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) target = profiles[index].Name; // the saved spelling stays
        if (index >= 0) profiles[index] = profile with { Name = target };
        else profiles.Add(profile);
        return Persist(profiles) is { } failed
            ? (failed, null)
            : (TF(index >= 0 ? "Parity.ShaderProfileUpdated" : "Parity.ShaderProfileSaved", target, profile.SelectedPacks.Count), target);
    }

    /// <summary>The packs to tick for <paramref name="name"/>; the profile's file exclusions are restored for each of them.</summary>
    public (IReadOnlyCollection<string>? Selected, string Status) Load(string name)
    {
        if (Find(name) is not { } profile) return (null, TF("Parity.ShaderProfileMissing", name));
        try
        {
            var known = _packs.AvailablePacks.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var pack in profile.SelectedPacks.Where(known.Contains))
                _packs.SetExcludedFiles(pack, profile.FileExclusions.TryGetValue(pack, out var excluded) ? excluded : []);
            var unknown = profile.SelectedPacks.Count(id => !known.Contains(id));
            var line = TF("Parity.ShaderProfileLoaded", profile.Name, profile.SelectedPacks.Count - unknown);
            if (unknown > 0) line += " " + TF("Parity.ShaderProfileUnknownPacks", unknown);
            return (profile.SelectedPacks.Where(known.Contains).ToList(), line);
        }
        catch (IOException ex)
        {
            RHI.Desktop.Diagnostics.UiExceptionGuard.Log.TryWrite("save shader pack file selection", ex);
            return (null, MainWindowViewModel.SaveFailedText(ex, "Parity.WhatShaderProfiles", sessionOnly: false));
        }
    }

    public string Rename(string name, string? newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return T("Parity.ShaderProfileNameNeeded");
        var profiles = _store.Load().ToList();
        var index = profiles.FindIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return TF("Parity.ShaderProfileMissing", name);
        if (profiles.Any(p => p.Name.Equals(newName.Trim(), StringComparison.OrdinalIgnoreCase) && !ReferenceEquals(p, profiles[index])))
            return TF("Parity.ShaderProfileNameTaken", newName.Trim());
        profiles[index] = profiles[index] with { Name = newName.Trim() };
        return Persist(profiles) ?? TF("Parity.ShaderProfileRenamed", name, newName.Trim());
    }

    public string Delete(string name)
    {
        var profiles = _store.Load().ToList();
        if (profiles.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) == 0) return TF("Parity.ShaderProfileMissing", name);
        return Persist(profiles) ?? TF("Parity.ShaderProfileDeleted", name);
    }

    /// <summary>Exports <paramref name="name"/> (or, with none, the ticked packs) as a zip and opens its folder.</summary>
    public async Task<string> ExportAsync(string? name, IReadOnlyCollection<string> selectedPacks, CancellationToken cancellationToken = default)
    {
        var profile = Find(name) ?? new ShaderSelectionProfile
        {
            Name = T("Parity.ShaderProfileDefaultExportName"),
            SelectedPacks = selectedPacks.ToList(),
            FileExclusions = selectedPacks.Select(id => (id, excluded: _packs.GetExcludedFiles(id))).Where(x => x.excluded.Count > 0)
                .ToDictionary(x => x.id, x => x.excluded.ToList(), StringComparer.OrdinalIgnoreCase)
        };
        if (profile.SelectedPacks.Count == 0) return T("Parity.ShaderProfileNothingToExport");
        string path;
        try
        {
            path = await ShaderProfileArchive.ExportAsync(profile, _packs, _exportFolder, _now(), cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or UntrustedDownloadException or InvalidOperationException
                                       or UnauthorizedAccessException)
        {
            AppLog.Warn("shader-profiles", "The shader profile export failed", ex);
            return TF("Parity.ShaderProfileExportFailed", UiLocalizationService.Present(ex.Message));
        }
        var (opened, error) = _openFolder(_exportFolder);
        var folderNote = opened ? "" : " " + ErrorLineText.FolderNotOpened(error);
        return TF("Parity.ShaderProfileExported", path) + folderNote;
    }

    /// <summary>Adds the profile a shared zip carries (renamed "Name (2)" when the name is taken). Only the selection is read.</summary>
    public (string Status, string? Imported) Import(string zipPath)
    {
        ShaderSelectionProfile profile;
        try
        {
            profile = ShaderProfileArchive.ReadProfile(zipPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return (TF("Parity.ShaderProfileImportFailed", UiLocalizationService.Present(ex.Message)), null);
        }
        var profiles = _store.Load().ToList();
        var name = profile.Name;
        for (var n = 2; profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); n++) name = $"{profile.Name} ({n})";
        profiles.Add(profile with { Name = name });
        return Persist(profiles) is { } failed ? (failed, null) : (TF("Parity.ShaderProfileImported", name, profile.SelectedPacks.Count), name);
    }

    private static string NextName(IReadOnlyCollection<ShaderSelectionProfile> profiles)
    {
        for (var n = profiles.Count + 1; ; n++)
        {
            var name = UiLocalizationService.Format("Parity.ShaderProfileNumbered", n);
            if (!profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return name;
        }
    }

    /// <summary>Saves; null when saved, else the status line saying why not.</summary>
    private string? Persist(IReadOnlyList<ShaderSelectionProfile> profiles)
        => ParityPersistence.TryPersist(() => _store.Save(profiles), "Parity.WhatShaderProfiles");
}
