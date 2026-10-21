using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RHI.Core.Models;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

/// <summary>A line of the Shaders/Add-ons dialog's lists: a heading or a tick box.</summary>
public abstract class CatalogItem : ObservableObject;

/// <summary>A category heading (shader packs) or a Shaders / Textures heading (a pack's files).</summary>
public sealed class CatalogHeading(string text) : CatalogItem
{
    public string Text { get; } = text;
}

/// <summary>A shader pack: ticked when the game uses it; its Files button picks single shaders and textures.</summary>
public sealed partial class ShaderPackRow(ReShadeShaderPackInfo pack, bool isChecked, string tooltip, ShaderAddonsDialogViewModel dialog) : CatalogItem
{
    public ReShadeShaderPackInfo Pack { get; } = pack;
    public string Id => Pack.Id;
    public string DisplayName => Pack.DisplayName;
    public string Tooltip { get; } = tooltip;
    [ObservableProperty] private bool isChecked = isChecked;

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task Files() => dialog.ShowFilesAsync(Pack);
}

/// <summary>A ReShade add-on: ticked when the game uses it; locked when another component manages it.</summary>
public sealed partial class AddonRow(string id, string label, bool isChecked, bool isEnabled, string tooltip) : CatalogItem
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    public bool IsEnabled { get; } = isEnabled;
    public string Tooltip { get; } = tooltip;
    [ObservableProperty] private bool isChecked = isChecked;
}

/// <summary>One file of a shader pack (a shader or a texture): ticked = deployed.</summary>
public sealed partial class ShaderPackFileRow(ReShadeShaderPackFileInfo file) : CatalogItem
{
    public ReShadeShaderPackFileInfo File { get; } = file;
    public string DisplayPath => File.DisplayPath;
    [ObservableProperty] private bool isChecked = !file.IsExcluded;
}

/// <summary>
/// The Shaders/Add-ons dialog of the selected game (epic #89: was built in the window's code-behind): shader packs by
/// category and ReShade add-ons, Apply to the game, and the custom shader / add-on folders. The shader-profile panel
/// (#93) is the parity lane's control, hosted by the view through <see cref="TickedShaderIds"/> and
/// <see cref="TickShaders"/>.
/// </summary>
public sealed partial class ShaderAddonsDialogViewModel : ObservableObject
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    private readonly Func<IReadOnlyList<string>, IReadOnlyList<string>, Task<ComponentOperationResult>> _apply;
    private readonly Func<string, (bool Opened, string? Error)> _openFolder;
    private readonly Func<ReShadeShaderPackInfo, Task> _showFiles;
    private readonly Action<string> _showMainStatus;

    /// <summary>
    /// The selected game's dialog. #179 layout B: with the shader and add-on choices wired, each tab starts with “Use the
    /// global set / Choose for this game / None” and Apply saves that choice and brings the game to it.
    /// </summary>
    internal static ShaderAddonsDialogViewModel For(MainWindowViewModel shell, ShaderAddonManagerUiSnapshot snapshot,
        Func<string, (bool Opened, string? Error)> openFolder, Func<ReShadeShaderPackInfo, Task> showFiles)
        => new(snapshot, shell.SelectedGame?.Name ?? "", (shaders, addons) => shell.ReShadeProfile.ApplyShaderAddonManagerAsync(shaders, addons),
            openFolder, showFiles, line => shell.StatusText = line, shell.Parity,
            choices: shell.ReShadeProfile.SetChoicesFor(shell.SelectedGame),
            applyChoices: selection => shell.ReShadeProfile.ApplyShaderAddonChoicesAsync(selection));

    /// <summary>
    /// #179: the same dialog for the global shader set and add-ons (Settings → Shaders/Add-ons → Global shaders and
    /// add-ons…): no choice row; Save saves them and brings every game set to the global set up to date.
    /// </summary>
    internal static ShaderAddonsDialogViewModel ForGlobal(MainWindowViewModel shell, ShaderAddonManagerUiSnapshot snapshot,
        Func<string, (bool Opened, string? Error)> openFolder, Func<ReShadeShaderPackInfo, Task> showFiles)
        => new(snapshot, "", (shaders, addons) => shell.ReShadeProfile.ApplyGlobalSetsAsync(shaders, addons),
            openFolder, showFiles, line => shell.StatusText = line, shell.Parity, isGlobal: true);

    /// <param name="openFolder">Opens (creating) a folder; says whether it opened, and why not.</param>
    /// <param name="choices">#179: the game's choices (null: no choice row, Apply sends the ticked lists).</param>
    internal ShaderAddonsDialogViewModel(ShaderAddonManagerUiSnapshot snapshot, string gameName,
        Func<IReadOnlyList<string>, IReadOnlyList<string>, Task<ComponentOperationResult>> apply,
        Func<string, (bool Opened, string? Error)> openFolder, Func<ReShadeShaderPackInfo, Task> showFiles, Action<string> showMainStatus,
        ParityFeatures? parity = null, bool isGlobal = false, ShaderAddonSetChoices? choices = null,
        Func<ShaderAddonSetSelection, Task<ComponentOperationResult>>? applyChoices = null)
    {
        _apply = apply;
        _applyChoices = applyChoices;
        _choices = isGlobal || applyChoices is null ? null : choices;
        _openFolder = openFolder;
        _showFiles = showFiles;
        _showMainStatus = showMainStatus;
        Snapshot = snapshot;
        Parity = parity;
        IsGlobal = isGlobal;
        WindowTitle = isGlobal ? T("Sets.GlobalDialogTitle") : $"Shaders/Add-ons — {gameName}";
        ApplyText = T(isGlobal ? "Sets.ApplyGlobal" : "Win.ApplySelectedGame");

        var selectedShaders = snapshot.SelectedShaderPackIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shaderItems = new List<CatalogItem>();
        foreach (var group in snapshot.ShaderPacks.GroupBy(x => x.Category))
        {
            shaderItems.Add(new CatalogHeading(ShaderCategoryDisplay(group.Key).ToUpperInvariant()));
            foreach (var pack in group.OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                var dependency = pack.Requires is { Count: > 0 } ? TF("Win.Requires", string.Join(", ", pack.Requires)) : "";
                shaderItems.Add(new ShaderPackRow(pack, selectedShaders.Contains(pack.Id),
                    $"{CatalogDescription(pack.Description, pack.DisplayName, shaderPack: true)}{dependency}\nID: {pack.Id}", this));
            }
        }
        ShaderItems = shaderItems;

        var selectedAddons = snapshot.SelectedAddonIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Addons = snapshot.Addons.OrderBy(x => x.ManagedByComponent).ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(addon =>
            {
                var arch = $"{(addon.Supports32Bit ? "x86" : "")}{(addon.Supports32Bit && addon.Supports64Bit ? "/" : "")}{(addon.Supports64Bit ? "x64" : "")}";
                var label = addon.ManagedByComponent
                    ? TF("Win.ComponentManaged", addon.DisplayName)
                    : addon.IsCustom ? TF("Win.Custom2", addon.DisplayName) : addon.DisplayName;
                return new AddonRow(addon.Id, label, selectedAddons.Contains(addon.Id), !addon.ManagedByComponent,
                    TF("Win.ArchitecturesID", CatalogDescription(addon.Description, addon.DisplayName, shaderPack: false), arch, addon.Id));
            }).ToArray();

        status = TF("Win.CatalogManagedShaderPackSLocal", snapshot.ShaderPacks.Count(x => !x.IsCustom), snapshot.Addons.Count);

        if (_choices is { } c)
        {
            // Choose starts from the game's own list; a game that never had one starts from what it gets now (upstream).
            _ownShaders = c.OwnShaders.Count > 0 || c.ShaderMode == ShaderAddonMode.Select ? c.OwnShaders : c.GlobalShaders;
            _ownAddons = c.OwnAddons.Count > 0 || c.AddonMode == ShaderAddonMode.Select ? c.OwnAddons : c.GlobalAddons;
            shaderMode = c.ShaderMode;
            addonMode = c.AddonMode;
            ShowModeTicks(ShaderAddonSetPart.Shaders);
            ShowModeTicks(ShaderAddonSetPart.Addons);
        }
    }

    // ── #179 layout B: each tab's choice ──────────────────────────────────────────────────────────────────────

    private readonly Func<ShaderAddonSetSelection, Task<ComponentOperationResult>>? _applyChoices;
    private readonly ShaderAddonSetChoices? _choices;
    private IReadOnlyList<string> _ownShaders = [];
    private IReadOnlyList<string> _ownAddons = [];

    /// <summary>The Shader packs tab starts with the choice row (a game's dialog with the choices wired).</summary>
    public bool HasShaderChoice => _choices is not null;
    /// <summary>The ReShade add-ons tab starts with the choice row (not while the No Addons build holds the add-ons).</summary>
    public bool HasAddonChoice => _choices is { AddonsHeld: false };

    [ObservableProperty] private ShaderAddonMode shaderMode;
    [ObservableProperty] private ShaderAddonMode addonMode;

    public bool ShaderUseGlobal { get => ShaderMode == ShaderAddonMode.Global; set { if (value) ShaderMode = ShaderAddonMode.Global; } }
    public bool ShaderChoose { get => ShaderMode == ShaderAddonMode.Select; set { if (value) ShaderMode = ShaderAddonMode.Select; } }
    public bool ShaderNone { get => ShaderMode == ShaderAddonMode.Off; set { if (value) ShaderMode = ShaderAddonMode.Off; } }
    public bool AddonUseGlobal { get => AddonMode == ShaderAddonMode.Global; set { if (value) AddonMode = ShaderAddonMode.Global; } }
    public bool AddonChoose { get => AddonMode == ShaderAddonMode.Select; set { if (value) AddonMode = ShaderAddonMode.Select; } }
    public bool AddonNone { get => AddonMode == ShaderAddonMode.Off; set { if (value) AddonMode = ShaderAddonMode.Off; } }

    /// <summary>The pack list (and the shader profiles) can be ticked: always, except a game's tab on the global set or None.</summary>
    public bool ShadersEditable => !HasShaderChoice || ShaderMode == ShaderAddonMode.Select;
    /// <summary>As <see cref="ShadersEditable"/>, for the add-on list.</summary>
    public bool AddonsEditable => !HasAddonChoice || AddonMode == ShaderAddonMode.Select;

    /// <summary>The line under the choice (or, in the global dialog, what the list is).</summary>
    public string ShaderHint => IsGlobal ? T("Sets.GlobalShadersHint") : !HasShaderChoice ? "" : T(ShaderMode switch
    {
        ShaderAddonMode.Global => "Sets.ShadersGlobalHint",
        ShaderAddonMode.Select => "Sets.ShadersChooseHint",
        _ => "Sets.ShadersNoneHint",
    });
    public string AddonHint => IsGlobal ? T("Sets.GlobalAddonsHint") : !HasAddonChoice ? "" : T(AddonMode switch
    {
        ShaderAddonMode.Global => "Sets.AddonsGlobalHint",
        ShaderAddonMode.Select => "Sets.AddonsChooseHint",
        _ => "Sets.AddonsNoneHint",
    });
    public bool ShowShaderHint => IsGlobal || HasShaderChoice;
    public bool ShowAddonHint => IsGlobal || HasAddonChoice;

    /// <summary>Review minor 4: the game's files lag behind its choice (a cancelled or skipped update); Apply catches up.</summary>
    public bool ShadersBehind => _choices is { ShadersBehind: true };
    public bool AddonsBehind => HasAddonChoice && _choices!.AddonsBehind;

    partial void OnShaderModeChanged(ShaderAddonMode oldValue, ShaderAddonMode newValue)
    {
        if (oldValue == ShaderAddonMode.Select) _ownShaders = TickedShaderIds().ToArray();
        ShowModeTicks(ShaderAddonSetPart.Shaders);
        foreach (var name in new[] { nameof(ShaderUseGlobal), nameof(ShaderChoose), nameof(ShaderNone), nameof(ShadersEditable), nameof(ShaderHint) })
            OnPropertyChanged(name);
    }

    partial void OnAddonModeChanged(ShaderAddonMode oldValue, ShaderAddonMode newValue)
    {
        if (oldValue == ShaderAddonMode.Select) _ownAddons = TickedAddonIds();
        ShowModeTicks(ShaderAddonSetPart.Addons);
        foreach (var name in new[] { nameof(AddonUseGlobal), nameof(AddonChoose), nameof(AddonNone), nameof(AddonsEditable), nameof(AddonHint) })
            OnPropertyChanged(name);
    }

    // The list shows what the choice gives: the global set (read-only), the game's own list, or nothing.
    private void ShowModeTicks(ShaderAddonSetPart part)
    {
        if (_choices is not { } c) return;
        if (part == ShaderAddonSetPart.Shaders)
            TickShaders((ShaderMode switch { ShaderAddonMode.Global => c.GlobalShaders, ShaderAddonMode.Select => _ownShaders, _ => Array.Empty<string>() }).ToArray());
        else if (HasAddonChoice)
        {
            var ids = AddonMode switch { ShaderAddonMode.Global => c.GlobalAddons, ShaderAddonMode.Select => _ownAddons, _ => Array.Empty<string>() };
            foreach (var row in Addons.Where(row => row.IsEnabled)) row.IsChecked = ids.Contains(row.Id, StringComparer.OrdinalIgnoreCase);
        }
    }

    private IReadOnlyList<string> TickedAddonIds() => Addons.Where(row => row.IsEnabled && row.IsChecked).Select(row => row.Id).ToArray();

    public string WindowTitle { get; }
    /// <summary>#179: the global set and add-ons rather than a game's.</summary>
    public bool IsGlobal { get; }
    /// <summary>The Apply button's label (a game, or Save for the global set).</summary>
    public string ApplyText { get; }
    public ShaderAddonManagerUiSnapshot Snapshot { get; }
    /// <summary>The shader-profile panel's features (#93, hosted by the view); null hides the panel.</summary>
    public ParityFeatures? Parity { get; }
    public IReadOnlyList<CatalogItem> ShaderItems { get; }
    public bool HasShaderItems => ShaderItems.Count > 0;
    public IReadOnlyList<AddonRow> Addons { get; }
    [ObservableProperty] private string status;
    [ObservableProperty] private bool canApply = true;

    private IEnumerable<ShaderPackRow> Packs => ShaderItems.OfType<ShaderPackRow>();

    /// <summary>The ticked shader packs (the shader-profile panel saves these).</summary>
    public IReadOnlyCollection<string> TickedShaderIds() => Packs.Where(row => row.IsChecked).Select(row => row.Id).ToArray();

    /// <summary>Ticks exactly these packs (a shader profile was loaded).</summary>
    public void TickShaders(IReadOnlyCollection<string> ids)
    {
        foreach (var row in Packs) row.IsChecked = ids.Contains(row.Id, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Applies the ticked packs and add-ons (an add-on another component manages is never sent).</summary>
    [RelayCommand]
    private Task Apply() => UiExceptionGuard.RunAsync(async () =>
    {
        CanApply = false;
        try
        {
            var shaders = TickedShaderIds().ToArray();
            var addons = TickedAddonIds();
            Status = TF("Win.ApplyingShaderPackSAddS", shaders.Length, addons.Count);
            var result = _choices is not null && _applyChoices is not null
                ? await _applyChoices(new ShaderAddonSetSelection(ShaderMode, shaders, HasAddonChoice ? AddonMode : null, addons))
                : await _apply(shaders, addons);
            Status = UiLocalizationService.Present(result.Message);
        }
        finally
        {
            CanApply = true;
        }
    }, "ShadersAddons_Click");

    [RelayCommand]
    private void OpenCustomShaders()
    {
        if (_openFolder(Snapshot.CustomShaderRoot) is (false, var error)) Status = ErrorLineText.CustomShadersNotOpened(error);
    }

    [RelayCommand]
    private void OpenCustomAddons()
    {
        if (_openFolder(Snapshot.CustomAddonRoot) is (false, var error)) Status = ErrorLineText.CustomAddonsNotOpened(error);
    }

    internal async Task ShowFilesAsync(ReShadeShaderPackInfo pack)
    {
        try { await _showFiles(pack); }
        catch (Exception ex) { _showMainStatus(TF("Win.CouldNotOpenFiles", pack.DisplayName, UiLocalizationService.Present(ex.Message))); }
    }

    internal static string ShaderCategoryDisplay(string category) => category switch
    {
        "Essential" => T("Win.Essential"),
        "Recommended" => T("Win.Recommended"),
        "Custom" => T("Win.Custom"),
        _ => T("Win.Extra")
    };

    internal static string CatalogDescription(string? description, string displayName, bool shaderPack)
    {
        if (string.IsNullOrWhiteSpace(description))
            return shaderPack
                ? TF("Win.ReShadeShaderPack", displayName)
                : TF("Win.ReShadeAdd", displayName);

        var presented = UiLocalizationService.Present(description);
        if (UiLocalizationService.CurrentLanguage == "pt-BR" && string.Equals(presented, description, StringComparison.Ordinal))
            return shaderPack
                ? TF("Win.ReShadeShaderPack", displayName)
                : TF("Win.ReShadeAdd", displayName);
        return presented;
    }
}

/// <summary>
/// A shader pack's Files dialog (epic #89: was built in the window's code-behind): every shader and texture of the pack,
/// ticked = deployed; Save stores the unticked ones as excluded for future syncs and closes, a refused save stays open.
/// </summary>
public sealed partial class ShaderPackFilesDialogViewModel : ObservableObject
{
    private static string T(string key) => UiLocalizationService.Text(key);
    private static string TF(string key, params object?[] args) => UiLocalizationService.Format(key, args);

    private readonly string _packId;
    private readonly Func<string, IReadOnlyList<string>, string?> _save;

    /// <param name="save">Saves the excluded selectors; returns why it was not saved, or null.</param>
    internal ShaderPackFilesDialogViewModel(ReShadeShaderPackInfo pack, IReadOnlyList<ReShadeShaderPackFileInfo> files,
        Func<string, IReadOnlyList<string>, string?> save)
    {
        _packId = pack.Id;
        _save = save;
        DisplayName = pack.DisplayName;
        WindowTitle = TF("Win.ShaderTexturesTitle", pack.DisplayName);
        Note = files.Count == 0 ? T("Win.NoUsableShaderTextureWasFound") : T("Win.CheckedDeployedUncheckedRemainsExcludedFuture");
        var items = new List<CatalogItem>();
        foreach (var group in files.GroupBy(x => x.IsTexture ? T("Win.Textures") : "Shaders"))
        {
            items.Add(new CatalogHeading(group.Key));
            items.AddRange(group.OrderBy(x => x.DisplayPath, StringComparer.OrdinalIgnoreCase).Select(file => new ShaderPackFileRow(file)));
        }
        Items = items;
        CanSave = files.Count > 0;
    }

    public string WindowTitle { get; }
    public string DisplayName { get; }
    public string Note { get; }
    public IReadOnlyList<CatalogItem> Items { get; }
    public bool CanSave { get; }
    [ObservableProperty] private string? status;

    public event Action? CloseRequested;

    [RelayCommand]
    private void Save()
    {
        var rows = Items.OfType<ShaderPackFileRow>().ToArray();
        var excluded = rows.Where(row => !row.IsChecked).Select(row => row.File.Selector).ToArray();
        if (_save(_packId, excluded) is { } notSaved)
        {
            Status = notSaved; // keep the dialog open: nothing was saved
            return;
        }
        Status = TF("Win.SelectionSavedActiveFileS", rows.Length - excluded.Length, rows.Length);
        CloseRequested?.Invoke();
    }
}
