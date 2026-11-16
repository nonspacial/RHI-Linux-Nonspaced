using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

namespace RHI.Desktop.ViewModels;

/// <summary>One setting row of All Settings: a dropdown of the build's options, or a text box.</summary>
public sealed partial class OptiScalerAllSettingRow : ObservableObject
{
    private readonly OptiScalerAllSettingsPresenter _owner;
    private readonly string[] _values;
    private bool _loading = true;

    internal OptiScalerAllSettingRow(OptiScalerAllSettingsPresenter owner, OptiScalerIniEntry entry, string current, bool showSection)
    {
        _owner = owner;
        Entry = entry;
        Label = showSection ? $"{entry.Key} ({entry.Section})" : entry.Key;
        var help = entry.Help;
        if (!string.IsNullOrEmpty(entry.Default)) help += (help.Length > 0 ? "\n" : "") + UiLocalizationService.Format("Osa.DefaultIs", entry.Default);
        help += (help.Length > 0 ? "\n" : "") + $"[{entry.Section}] {entry.Key}" + (entry.InFile ? "" : " " + UiLocalizationService.Text("Osa.NotInFileYet"));
        Help = help;
        IsChoice = entry.Kind != OptiScalerIniKind.Text;
        var values = entry.Options.Select(option => option.Value).ToList();
        var labels = entry.Options.Select(option => option.Label).ToList();
        // A value the build does not list (typed by hand, or another build's) stays selectable (upstream).
        foreach (var value in new[] { entry.Value, current })
            if (IsChoice && !values.Any(known => known.Equals(value, StringComparison.OrdinalIgnoreCase)))
            {
                values.Add(value);
                labels.Add(value.Length == 0 ? UiLocalizationService.Text("Osa.Empty") : value);
            }
        _values = [.. values];
        Options = labels;
        selectedIndex = IsChoice ? values.FindIndex(value => value.Equals(current, StringComparison.OrdinalIgnoreCase)) : -1;
        text = current;
        _loading = false;
    }

    public OptiScalerIniEntry Entry { get; }
    public string Label { get; }
    public string Help { get; }
    public bool IsChoice { get; }
    public bool IsText => !IsChoice;
    public IReadOnlyList<string> Options { get; }
    [ObservableProperty] private int selectedIndex;
    [ObservableProperty] private string text;

    partial void OnSelectedIndexChanged(int value)
    {
        if (!_loading && value >= 0 && value < _values.Length) _owner.Change(Entry, _values[value]);
    }

    partial void OnTextChanged(string value)
    {
        if (!_loading) _owner.Change(Entry, (value ?? "").Trim());
    }
}

/// <summary>
/// #272 the ⚙'s All Settings (upstream 2.9.3): every setting of the game's OptiScaler.ini with the options the installed
/// build's own INI lists, a section picker, search (60 matches at most), and Apply (the old file backed up), Revert,
/// Forget saved edits and Open backups. Nothing is written before Apply; applied edits are written again after a
/// reinstall or a version switch.
/// </summary>
public sealed partial class OptiScalerAllSettingsPresenter : ObservableObject
{
    public const int SearchLimit = 60;

    private readonly MainWindowViewModel _shell;
    private readonly LinuxManagedComponentService _managed;
    private readonly DetectedGame _game;
    private readonly Dictionary<string, string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private List<OptiScalerIniEntry> _entries = [];
    private bool _loaded;

    internal OptiScalerAllSettingsPresenter(MainWindowViewModel shell, LinuxManagedComponentService managed, DetectedGame game)
    {
        _shell = shell;
        _managed = managed;
        _game = game;
    }

    /// <summary>Raised after Apply: the ⚙ reopens so the settings above show the new values (upstream).</summary>
    public event Action<string>? Applied;

    [ObservableProperty] private bool isOpen;
    [ObservableProperty] private string? selectedSection;
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string? note;
    [ObservableProperty] private string message = "";

    public ObservableCollection<string> Sections { get; } = [];
    public ObservableCollection<OptiScalerAllSettingRow> Rows { get; } = [];

    public string ToggleText => UiLocalizationService.Text(IsOpen ? "Osa.Hide" : "Osa.Show");
    public string ApplyText => _pending.Count == 0 ? UiLocalizationService.Text("Win.Apply") : UiLocalizationService.Format("Osa.ApplyCount", _pending.Count);
    public bool CanApply => _pending.Count > 0;
    public bool HasSavedEdits => _managed.GetOptiScalerIniChoices(_game).Edits.Count > 0;
    public bool CanChooseSection => SearchText.Trim().Length == 0;

    public string InfoText
    {
        get
        {
            var saved = _managed.GetOptiScalerIniChoices(_game).Edits.Count;
            return saved > 0 ? UiLocalizationService.Format("Osa.SavedInfo", saved) : UiLocalizationService.Text("Osa.Info");
        }
    }

    /// <summary>"Show all settings" / "Hide all settings"; the INI is read the first time.</summary>
    public void Toggle()
    {
        IsOpen = !IsOpen;
        if (IsOpen && !_loaded) Load();
    }

    partial void OnIsOpenChanged(bool value) => OnPropertyChanged(nameof(ToggleText));

    /// <summary>Reads the game's settings described by the installed build's INI (the section the user was on stays).</summary>
    public void Load()
    {
        try { _entries = [.. _managed.GetOptiScalerIniEntries(_game) ?? []]; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = UiLocalizationService.Format("Osa.ReadFailed", ErrorLineText.Of(ex));
            return;
        }
        var previous = SelectedSection;
        Sections.Clear();
        foreach (var section in _entries.Select(entry => entry.Section).Distinct(StringComparer.OrdinalIgnoreCase)) Sections.Add(section);
        _loaded = true;
        SelectedSection = Sections.FirstOrDefault(section => section.Equals(previous, StringComparison.OrdinalIgnoreCase)) ?? Sections.FirstOrDefault();
        Render();
    }

    partial void OnSelectedSectionChanged(string? value) { if (_loaded) Render(); }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(CanChooseSection));
        if (_loaded) Render();
    }

    private void Render()
    {
        Rows.Clear();
        var query = SearchText.Trim();
        IEnumerable<OptiScalerIniEntry> shown;
        if (query.Length > 0)
        {
            var all = _entries.Where(entry => entry.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
                                              || entry.Section.Contains(query, StringComparison.OrdinalIgnoreCase)
                                              || entry.Help.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            shown = all.Take(SearchLimit);
            Note = all.Count == 0 ? UiLocalizationService.Text("Osa.NoMatch")
                : all.Count > SearchLimit ? UiLocalizationService.Format("Osa.FirstMatches", SearchLimit, all.Count)
                : UiLocalizationService.Format("Osa.Matches", all.Count);
        }
        else
        {
            shown = _entries.Where(entry => entry.Section.Equals(SelectedSection, StringComparison.OrdinalIgnoreCase));
            Note = null;
        }
        foreach (var entry in shown)
            Rows.Add(new OptiScalerAllSettingRow(this, entry, _pending.TryGetValue(entry.Id, out var pending) ? pending : entry.Value, query.Length > 0));
    }

    /// <summary>A row changed: pending until Apply (back to the file's value = no change).</summary>
    internal void Change(OptiScalerIniEntry entry, string value)
    {
        if (string.Equals(value, entry.Value, StringComparison.Ordinal)) _pending.Remove(entry.Id);
        else _pending[entry.Id] = value;
        RaiseButtons();
    }

    public async Task ApplyAsync()
    {
        if (_pending.Count == 0) return;
        var edits = new Dictionary<string, string>(_pending, StringComparer.OrdinalIgnoreCase);
        var result = await _managed.ApplyOptiScalerIniEditsAsync(_game, edits);
        if (!result.Success)
        {
            Message = UiLocalizationService.Present(result.Message);
            return;
        }
        _pending.Clear();
        Load();
        RaiseButtons();
        Message = UiLocalizationService.Format("Osa.Applied", edits.Count);
        Applied?.Invoke(Message);
    }

    public void Revert()
    {
        _pending.Clear();
        Render();
        RaiseButtons();
    }

    public void ForgetSavedEdits()
    {
        _managed.ForgetOptiScalerIniEdits(_game);
        Message = UiLocalizationService.Text("Osa.Forgotten");
        RaiseButtons();
    }

    public void OpenBackups()
    {
        var folder = _managed.OptiScalerIniBackupFolder(_game);
        if (!Directory.Exists(folder)) { Message = UiLocalizationService.Text("Osa.NoBackups"); return; }
        if (!RHI.Platform.Linux.Paths.LinuxDesktopOpener.TryOpenDirectory(folder, out var error)) Message = error ?? "";
    }

    private void RaiseButtons()
    {
        OnPropertyChanged(nameof(ApplyText));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(HasSavedEdits));
        OnPropertyChanged(nameof(InfoText));
    }
}

/// <summary>
/// #272 "Save ini…" and "Load saved ini…" (upstream 2.9.3 <c>OsIniLibrary</c>): save the game's OptiScaler.ini under a
/// name, use a saved one for this game (remembered and put back after a reinstall or a version switch), add one from a
/// file, delete one, open the folder.
/// </summary>
public sealed partial class OptiScalerIniLibraryPresenter : ObservableObject
{
    private readonly LinuxManagedComponentService _managed;
    private readonly DetectedGame _game;

    internal OptiScalerIniLibraryPresenter(LinuxManagedComponentService managed, DetectedGame game)
    {
        _managed = managed;
        _game = game;
        saveName = managed.GetOptiScalerIniChoices(game).SavedIni ?? "";
        Refresh();
    }

    /// <summary>Raised after a saved INI was loaded into the game (the ⚙ reopens on the new values).</summary>
    public event Action<string>? Loaded;

    [ObservableProperty] private string saveName;
    [ObservableProperty] private string message = "";
    [ObservableProperty] private bool messageIsError;

    public ObservableCollection<string> Saved { get; } = [];

    /// <summary>The saved INI this game uses (null = RHI's template).</summary>
    public string? Current => _managed.GetOptiScalerIniChoices(_game).SavedIni;

    public string SaveNote
        => OptiScalerIniLibraryClean(SaveName) is { } name && _managed.OptiScalerIniLibrary.Exists(name) ? UiLocalizationService.Format("Osi.Replaces", name) : "";

    partial void OnSaveNameChanged(string value) => OnPropertyChanged(nameof(SaveNote));

    public void Save()
    {
        var replaced = OptiScalerIniLibraryClean(SaveName) is { } name && _managed.OptiScalerIniLibrary.Exists(name);
        try
        {
            var result = _managed.SaveOptiScalerIni(_game, SaveName);
            Say(result.Success
                ? UiLocalizationService.Format(replaced ? "Osi.Replaced" : "Osi.Saved", OptiScalerIniLibraryClean(SaveName))
                : UiLocalizationService.Present(result.Message), !result.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Say(UiLocalizationService.Format("Win.CouldNotSave", ErrorLineText.Of(ex)), true);
        }
        Refresh();
    }

    public async Task UseAsync(string name)
    {
        var result = await _managed.LoadSavedOptiScalerIniAsync(_game, name);
        if (!result.Success) { Say(UiLocalizationService.Present(result.Message), true); return; }
        Say(UiLocalizationService.Format("Osi.Loaded", name), false);
        OnPropertyChanged(nameof(Current));
        Loaded?.Invoke(Message);
    }

    public void Delete(string name)
    {
        try
        {
            _managed.DeleteSavedOptiScalerIni(name, _game);
            Say(UiLocalizationService.Format("Osi.Deleted", name), false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say(UiLocalizationService.Format("Osi.DeleteFailed", ErrorLineText.Of(ex)), true);
        }
        Refresh();
    }

    /// <summary>"Add from file…": the file goes into the library under a name that replaces none.</summary>
    public void AddFromFile(string path)
    {
        try
        {
            var name = _managed.OptiScalerIniLibrary.SuggestName(path);
            _managed.OptiScalerIniLibrary.Save(name, path);
            Say(UiLocalizationService.Format("Osi.Added", name), false);
        }
        catch (InvalidDataException) { Say(UiLocalizationService.Text("Osi.NotAnIni"), true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say(UiLocalizationService.Format("Osi.AddFailed", ErrorLineText.Of(ex)), true);
        }
        Refresh();
    }

    public void OpenFolder()
    {
        Directory.CreateDirectory(_managed.OptiScalerIniLibrary.Directory);
        if (!RHI.Platform.Linux.Paths.LinuxDesktopOpener.TryOpenDirectory(_managed.OptiScalerIniLibrary.Directory, out var error))
            Say(error ?? "", true);
    }

    private void Refresh()
    {
        Saved.Clear();
        foreach (var name in _managed.OptiScalerIniLibrary.List()) Saved.Add(name);
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(SaveNote));
    }

    private void Say(string text, bool error)
    {
        Message = text;
        MessageIsError = error;
    }

    private static string? OptiScalerIniLibraryClean(string? name) => RHI.Platform.Linux.Paths.OptiScalerIniLibrary.CleanName(name);
}
