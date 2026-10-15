using System.Runtime.CompilerServices;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>
/// OptiScaler presets and the global overlay key (Refs #187, upstream <c>OsPresetService</c>, the ⚙ Presets row and
/// <c>ApplyOsHotkey_Click</c>). Four global slots: <see cref="SaveSlot"/> captures the selected game's current OptiScaler ⚙
/// settings (every ⚙ setting but the overlay key, which the global key row owns), <see cref="ApplyAsync"/> applies a slot to
/// the selected game through the ⚙'s own setting path, one setting at a time (the Streamline / DLSS Enabler stack and its
/// version first). A captured setting this game's ⚙ does not offer, or a value it does not list, is left out and counted.
/// The channel is not part of a preset on Linux (changing it reinstalls OptiScaler). Store writes go through
/// <see cref="ParityPersistence"/>.
/// </summary>
public sealed class OptiScalerPresetsPresenter
{
    private static readonly ConditionalWeakTable<MainWindowViewModel, OptiScalerPresetsPresenter> Presenters = new();

    /// <summary>Applied first and in this order: the stack decides which files the later settings act on.</summary>
    private static readonly string[] FirstSettings = [OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId, "StreamlineVersion"];

    private readonly MainWindowViewModel _shell;
    private readonly LinuxManagedComponentService _managed;
    private OptiScalerPresetSlots? _slots;

    public static OptiScalerPresetsPresenter For(MainWindowViewModel shell, LinuxManagedComponentService managed)
        => Presenters.GetValue(shell, s => new OptiScalerPresetsPresenter(s, managed));

    private OptiScalerPresetsPresenter(MainWindowViewModel shell, LinuxManagedComponentService managed)
    {
        _shell = shell;
        _managed = managed;
    }

    /// <summary>The preset file (test seam; <c>~/.config/rhi-linux/optiscaler-presets.json</c> by default).</summary>
    internal OptiScalerPresetStore Store
    {
        get => _store;
        set
        {
            _store = value;
            _slots = null; // read from the new file
        }
    }

    private OptiScalerPresetStore _store = new();

    public const int SlotCount = OptiScalerPresetSlots.Count;

    private OptiScalerPresetSlots Slots => _slots ??= Store.Load();

    /// <summary>Presets show in the OptiScaler ⚙ where upstream shows them: the Nightly, DLSS NR and F5 channels (their ⚙ has the Streamline row).</summary>
    public static bool AppliesTo(OptiScalerAdvancedSnapshot snapshot)
        => snapshot.ManagedByRhi && snapshot.Settings.Any(setting => setting.Id == OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId);

    /// <summary>The slot's preset, or null when nothing was saved into it.</summary>
    public OptiScalerPreset? Slot(int index) => Slots.Slots[index];

    /// <summary>The slot's button label: its name, else "Slot n".</summary>
    public string Label(int index) => Slot(index)?.Name ?? UiLocalizationService.Format("Shell.PresetSlot", index + 1);

    /// <summary>
    /// Saves the selected game's settings as they are now into the slot (its name is kept); the line to show. Read at the
    /// moment of saving: the ⚙ writes each change at once, so a snapshot taken when the dialog opened would be stale.
    /// </summary>
    public string SaveSlot(int index)
    {
        var captured = _shell.GetOptiScalerAdvancedSettings().Settings
            .Where(setting => setting.Id != OptiScalerDeploymentService.MenuShortcutKeySettingId)
            .ToDictionary(setting => setting.Id, setting => setting.CurrentValue, StringComparer.Ordinal);
        var name = Slot(index)?.Name;
        return Persist(index, new OptiScalerPreset { Name = name, Settings = captured })
               ?? UiLocalizationService.Format("Shell.PresetSaved", Label(index), captured.Count);
    }

    /// <summary>Renames the slot (blank = "Slot n"); null when saved, else the refusal line.</summary>
    public string? Rename(int index, string? name)
    {
        var current = Slot(index) ?? new OptiScalerPreset();
        var renamed = new OptiScalerPreset { Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(), Settings = new(current.Settings, StringComparer.Ordinal) };
        return Persist(index, renamed);
    }

    /// <summary>
    /// Applies the slot to the selected game through the ⚙ setting path; the line to show. Stops at the first setting
    /// that fails (the ones before it stay applied, as each is its own ⚙ change).
    /// </summary>
    public async Task<string> ApplyAsync(int index)
    {
        if (Slot(index) is not { } preset) return UiLocalizationService.Text("Shell.PresetEmpty");
        var changed = 0;
        var notOffered = 0;
        foreach (var (id, value) in ApplyOrder(preset))
        {
            // Read again after every change: the Streamline row changes which settings the ⚙ offers.
            var setting = _shell.GetOptiScalerAdvancedSettings().Settings.FirstOrDefault(s => s.Id == id);
            if (setting is null || !setting.Options.Any(option => option.Value.Equals(value, StringComparison.OrdinalIgnoreCase)))
            {
                notOffered++;
                continue;
            }
            if (setting.CurrentValue.Equals(value, StringComparison.OrdinalIgnoreCase)) continue;
            var result = await _shell.SetOptiScalerAdvancedSettingAsync(id, value);
            if (!result.Success)
                return UiLocalizationService.Format("Shell.PresetStopped", Label(index), changed, UiLocalizationService.Present(result.Message));
            changed++;
        }
        var line = UiLocalizationService.Format("Shell.PresetApplied", Label(index), changed);
        return notOffered == 0 ? line : line + UiLocalizationService.Format("Shell.PresetNotOffered", notOffered);
    }

    /// <summary>
    /// "Apply to all games" of the global overlay key: every INI template and every game's RHI OptiScaler.ini. Runs as one
    /// operation (refused while another runs); the line to show.
    /// </summary>
    public async Task<string> ApplyMenuKeyToAllAsync(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return UiLocalizationService.Text("Shell.MenuKeyChooseFirst");
        using var op = _shell.Operations.TryBegin("optiscaler-menu-key");
        if (op is null) return UiLocalizationService.Text("Vm.AnotherRHIOperationAlreadyRunning");
        var games = _shell.AllLibraryGames.ToArray();
        try
        {
            var result = await Task.Run(() => _managed.ApplyOptiScalerMenuKeyToAll(games, key, op.Token));
            var line = UiLocalizationService.Format("Shell.MenuKeyApplied", result.Games, result.Templates);
            return result.Failures.Count == 0
                ? line
                : line + UiLocalizationService.Format("Shell.MenuKeyFailed", result.Failures.Count, result.Failures[0].Game,
                    UiLocalizationService.Present(result.Failures[0].Reason));
        }
        catch (OperationCanceledException)
        {
            return UiLocalizationService.Text("Shell.MenuKeyCancelled");
        }
    }

    /// <summary>The preset's settings in apply order: the Streamline / DLSS Enabler stack, its version, then the rest.</summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> ApplyOrder(OptiScalerPreset preset)
        => preset.Settings
            .OrderBy(pair => Array.IndexOf(FirstSettings, pair.Key) is var at && at >= 0 ? at : FirstSettings.Length)
            .ToArray();

    private string? Persist(int index, OptiScalerPreset preset)
    {
        var next = new OptiScalerPresetSlots { Slots = [.. Slots.Slots] };
        next.Slots[index] = preset;
        var refused = ParityPersistence.TryPersist(() => Store.Save(next), "Shell.WhatOptiScalerPresets");
        if (refused is null) _slots = next.Normalize();
        return refused;
    }
}
