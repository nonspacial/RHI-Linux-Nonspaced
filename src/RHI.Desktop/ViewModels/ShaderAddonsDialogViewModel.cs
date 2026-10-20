using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RHI.Core.Models;
using RHI.Desktop.Diagnostics;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;

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

    /// <param name="selectPart">#179: opened by choosing Select for this part: Apply makes it the game's own list.</param>
    internal static ShaderAddonsDialogViewModel For(MainWindowViewModel shell, ShaderAddonManagerUiSnapshot snapshot,
        Func<string, (bool Opened, string? Error)> openFolder, Func<ReShadeShaderPackInfo, Task> showFiles,
        RHI.Platform.Linux.Deployment.ShaderAddonSetPart? selectPart = null)
        => new(snapshot, shell.SelectedGame?.Name ?? "", (shaders, addons) => shell.ReShadeProfile.ApplyShaderAddonManagerAsync(shaders, addons, default, selectPart),
            openFolder, showFiles, line => shell.StatusText = line, shell.Parity)
        {
            InitialTab = selectPart == RHI.Platform.Linux.Deployment.ShaderAddonSetPart.Addons ? 1 : 0,
        };

    /// <summary>
    /// #179: the same dialog for the global shader set and add-ons (Settings → Shaders/Add-ons): Apply saves them and brings every
    /// game set to Global up to date.
    /// </summary>
    internal static ShaderAddonsDialogViewModel ForGlobal(MainWindowViewModel shell, ShaderAddonManagerUiSnapshot snapshot,
        Func<string, (bool Opened, string? Error)> openFolder, Func<ReShadeShaderPackInfo, Task> showFiles,
        RHI.Platform.Linux.Deployment.ShaderAddonSetPart part)
        => new(snapshot, "", (shaders, addons) => shell.ReShadeProfile.ApplyGlobalSetsAsync(shaders, addons),
            openFolder, showFiles, line => shell.StatusText = line, shell.Parity, isGlobal: true)
        {
            InitialTab = part == RHI.Platform.Linux.Deployment.ShaderAddonSetPart.Addons ? 1 : 0,
        };

    /// <param name="openFolder">Opens (creating) a folder; says whether it opened, and why not.</param>
    internal ShaderAddonsDialogViewModel(ShaderAddonManagerUiSnapshot snapshot, string gameName,
        Func<IReadOnlyList<string>, IReadOnlyList<string>, Task<ComponentOperationResult>> apply,
        Func<string, (bool Opened, string? Error)> openFolder, Func<ReShadeShaderPackInfo, Task> showFiles, Action<string> showMainStatus,
        ParityFeatures? parity = null, bool isGlobal = false)
    {
        _apply = apply;
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
    }

    public string WindowTitle { get; }
    /// <summary>#179: the global set and add-ons rather than a game's.</summary>
    public bool IsGlobal { get; }
    /// <summary>The Apply button's label (a game, or the games set to Global).</summary>
    public string ApplyText { get; }
    /// <summary>The tab shown first: 0 shader packs, 1 add-ons.</summary>
    public int InitialTab { get; init; }
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
            var addons = Addons.Where(row => row.IsEnabled && row.IsChecked).Select(row => row.Id).ToArray();
            Status = TF("Win.ApplyingShaderPackSAddS", shaders.Length, addons.Length);
            var result = await _apply(shaders, addons);
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
