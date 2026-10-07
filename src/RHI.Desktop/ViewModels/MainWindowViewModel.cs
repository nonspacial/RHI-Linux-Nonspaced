using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RHI.Core;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Hdr;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Persistence;
using RHI.Platform.Linux.Settings;

namespace RHI.Desktop.ViewModels;

public sealed record ShaderAddonManagerUiSnapshot(
    IReadOnlyList<ReShadeShaderPackInfo> ShaderPacks,
    IReadOnlyList<string> SelectedShaderPackIds,
    IReadOnlyList<ReShadeAddonCatalogItem> Addons,
    IReadOnlyList<string> SelectedAddonIds,
    string CustomShaderRoot,
    string CustomAddonRoot);


public sealed record LinuxSettingsUiSnapshot(
    LinuxAppSettings Settings,
    string SettingsPath,
    string ConfigRoot,
    string CacheRoot,
    string DataRoot);

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private static string L(string ptBr, string enUs) => UiLocalizationService.Choose(ptBr, enUs);
    private static string LF(string ptBr, string enUs, params object?[] args) => UiLocalizationService.ChooseFormat(ptBr, enUs, args);

    private static string PresentOr(string? value, string ptBrFallback, string enUsFallback)
    {
        var presented = UiLocalizationService.Present(value);
        return string.IsNullOrWhiteSpace(presented) ? L(ptBrFallback, enUsFallback) : presented;
    }

    private sealed record ProbeCacheEntry(
        IReadOnlyList<string> Files,
        IReadOnlyList<ManagedComponent> Components,
        DlssDetectionResult Dlss);

    private readonly GameLibraryScanner _scanner;
    private readonly IGameLauncher _launcher;
    private readonly IGameRuntimeProbe _runtimeProbe;
    private readonly DlssManagerService _dlss;
    private readonly LinuxManagedComponentService _managedComponents;
    private readonly GraphicsApiOverrideStore _apiOverrideStore;
    private readonly OptiScalerVariantPreferenceStore _optiScalerVariantStore;
    private readonly LinuxSettingsStore _settingsStore;
    private readonly LinuxUpdateAllService _updateAll;
    private readonly LinuxDlssDefaultsService _dlssDefaults;
    private readonly LinuxGameLibraryStore _libraryStore;
    private readonly LinuxDeploymentRecordStore _deploymentRecords;
    private readonly LinuxHdrLaunchProfileService _hdrProfiles;
    private CancellationTokenSource? _backgroundUpdateCts;
    private LinuxAppSettings _settings;
    private LinuxGameLibraryState _libraryState;
    private bool _loadingGraphicsApiOverride;
    /// <summary>
    /// Raised while an option list bound as ItemsSource is being rebuilt. Avalonia writes null
    /// back to the TwoWay SelectedItem when the list is cleared; every persisting handler below
    /// must ignore that transient write or per-game preferences get deleted on a Settings save.
    /// </summary>
    private bool _rebuildingOptionLists;
    private bool _loadingOptiScalerVariant;
    private bool _loadingLumaModePreference;
    private bool _loadingHdrProfile;
    private readonly List<DetectedGame> _allGames = [];
    private readonly Dictionary<string, ProbeCacheEntry> _probeCache = new(StringComparer.Ordinal);
    private CancellationTokenSource? _selectionProbeCts;
    private int _selectionGeneration;
    private DlssDetectionResult _dlssDetection = new();
    private readonly HashSet<string> _selectedShaderPackIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<PeArchitecture, string> _ualLatestVersionCache = new();
    private readonly Dictionary<PeArchitecture, Task<string?>> _ualLatestVersionLoads = new();
    private string? _shortFuseLatestVersionCache;
    private Task<string?>? _shortFuseLatestVersionLoad;
    private bool _neuralVersionCatalogLoaded;
    private Task? _neuralVersionCatalogLoadTask;
    // GitHub token notice (Refs #133); the settle check runs after queued UI work.
    private readonly OneShotStatusNotice _statusNotice = new(action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background));

    public ObservableCollection<DetectedGame> Games { get; } = [];
    public ObservableCollection<ComponentRowViewModel> Components { get; } = [];
    public ObservableCollection<string> DlssSrVersions { get; } = [];
    public ObservableCollection<string> DlssRrVersions { get; } = [];
    public ObservableCollection<string> DlssFgVersions { get; } = [];
    public ObservableCollection<string> DlssNrVersions { get; } = [];
    public ObservableCollection<string> StreamlineVersions { get; } = [];
    public ObservableCollection<string> ReShadeChannels { get; } =
        [UiLocalizationService.Text("Ui.OptionStable"), UiLocalizationService.Text("Ui.OptionNightly"), UiLocalizationService.Text("Ui.OptionLegacy"), UiLocalizationService.Text("Ui.OptionCustom")];
    public ObservableCollection<string> GraphicsApiOverrideOptions { get; } =
        [UiLocalizationService.Text("Ui.OptionAutomatic"), "DirectX 8", "DirectX 9", "DirectX 10", "DirectX 11", "DirectX 12", "Vulkan", "OpenGL"];
    public ObservableCollection<string> OptiScalerVariantOptions { get; } =
        [UiLocalizationService.Text("Ui.OptionStable"), UiLocalizationService.Text("Ui.OptionNightly"), "DLSS NR"];
    public ObservableCollection<string> LumaModeOptions { get; } =
        [UiLocalizationService.Text("Ui.OptionAutomatic"), UiLocalizationService.Text("Ui.OptionEnabled"), UiLocalizationService.Text("Ui.OptionDisabled")];
    public ObservableCollection<string> HdrModeOptions { get; } =
        [UiLocalizationService.Text("Ui.HdrOff"), UiLocalizationService.Text("Ui.HdrNativeWayland"), UiLocalizationService.Text("Ui.HdrGamescope")];
    public ObservableCollection<string> Dlss5FeederConsumerOptions { get; } = ["RenoDX DLSS5", "Deep Fried Chicken"];
    public ObservableCollection<string> Dlss5FeederVersionOptions { get; } = [UiLocalizationService.Text("Ui.OptionLatest")];
    public ObservableCollection<string> Dlss5BridgeVersionOptions { get; } = [UiLocalizationService.Text("Ui.OptionLatest")];
    public ObservableCollection<string> ShortFuseVersionOptions { get; } = [UiLocalizationService.Text("Ui.OptionLatest")];
    public ObservableCollection<string> NeuralRenderingDllVersionOptions { get; } = [UiLocalizationService.Text("Ui.OptionLatest")];
    public ObservableCollection<string> UalProxyNames { get; } = [];
    public string ProductVersionText => $"RHI-Linux v{RhiProductInfo.Version} · {UiLocalizationService.Text("Ui.ProductRuntime")}";

    [ObservableProperty] private DetectedGame? selectedGame;
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string libraryFilterMode = "All Games";
    [ObservableProperty] private string viewMode = "Detail";
    [ObservableProperty] private string statusText = UiLocalizationService.Text("Ui.Starting");
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isSelectionProbeBusy;
    [ObservableProperty] private string? selectedDlssSrVersion;
    [ObservableProperty] private string? selectedDlssRrVersion;
    [ObservableProperty] private string? selectedDlssFgVersion;
    [ObservableProperty] private string? selectedDlssNrVersion;
    [ObservableProperty] private string? selectedStreamlineVersion;
    [ObservableProperty] private string selectedReShadeChannel = UiLocalizationService.Text("Ui.OptionStable");
    [ObservableProperty] private string selectedGraphicsApiOverride = UiLocalizationService.Text("Ui.OptionAutomatic");
    [ObservableProperty] private string selectedOptiScalerVariant = UiLocalizationService.Text("Ui.OptionStable");
    [ObservableProperty] private string selectedLumaModePreference = UiLocalizationService.Text("Ui.OptionAutomatic");
    [ObservableProperty] private string selectedHdrMode = UiLocalizationService.Text("Ui.HdrOff");
    [ObservableProperty] private bool hdrLegacyWsi;
    [ObservableProperty] private string reShadeLegacyVersion = "6.4.1";
    [ObservableProperty] private string reShadeCustomDllPath = "";
    [ObservableProperty] private string reShadeD3DCompiler47Path = "";
    [ObservableProperty] private string dlss5FeederConsumerPackagePath = "";
    [ObservableProperty] private string selectedDlss5FeederConsumer = "RenoDX DLSS5";
    [ObservableProperty] private string selectedDlss5FeederVersion = UiLocalizationService.Text("Ui.OptionLatest");
    [ObservableProperty] private string selectedDlss5BridgeVersion = UiLocalizationService.Text("Ui.OptionLatest");
    [ObservableProperty] private string selectedShortFuseVersion = UiLocalizationService.Text("Ui.OptionLatest");
    [ObservableProperty] private string selectedNeuralRenderingDllVersion = UiLocalizationService.Text("Ui.OptionLatest");
    public bool IsDeepFriedChickenFeederConsumer => SelectedDlss5FeederConsumer == "Deep Fried Chicken";
    public bool IsRenoDxDlss5FeederConsumer => !IsDeepFriedChickenFeederConsumer;
    [ObservableProperty] private bool reShadeManageIni = true;
    [ObservableProperty] private bool shaderCrosireMaster;
    [ObservableProperty] private bool shaderCrosireLegacy;
    [ObservableProperty] private bool shaderClshortfuse;
    [ObservableProperty] private bool shaderMaxG2D;
    [ObservableProperty] private string reShadeUpdateText = UiLocalizationService.Text("Ui.ReShadeApplyHint");
    [ObservableProperty] private string selectedUalProxyName = "";
    [ObservableProperty] private string ualLatestVersionText = UiLocalizationService.Text("Ui.NotCheckedYet");
    [ObservableProperty] private bool shortFuseAutoConfigEnabled = false;
    [ObservableProperty] private bool shortFuseZzzLoadOrder;
    private bool _loadingShortFuseZzzLoadOrder;
    [ObservableProperty] private string shortFuseLatestVersionText = UiLocalizationService.Text("Ui.NotCheckedYet");

    private static string LibraryFilterDisplay(string value) => value switch
    {
        "Favourites" => UiLocalizationService.Text("Ui.Favourites"),
        "Installed" => UiLocalizationService.Text("Ui.Installed"),
        "Other" => UiLocalizationService.Text("Ui.Other"),
        "Hidden" => UiLocalizationService.Text("Ui.Hidden"),
        _ => UiLocalizationService.Text("Ui.All")
    };

    public string GameCountText => $"{Games.Count} / {_allGames.Count} {UiLocalizationService.Text("Ui.Games")} · {LibraryFilterDisplay(LibraryFilterMode)}";
    public bool IsCompactView => string.Equals(ViewMode, "Compact", StringComparison.OrdinalIgnoreCase);
    public string ViewToggleText => IsCompactView ? UiLocalizationService.Text("Ui.ViewDetail") : UiLocalizationService.Text("Ui.ViewCompact");
    public bool TrayIconEnabled => _settings.TrayIconEnabled;
    public bool CloseToTray => _settings.CloseToTray;
    public bool AddonWatcherEnabled => _settings.AddonWatcherEnabled;
    public string AddonWatchFolder => _settings.AddonWatchFolder;
    public bool ShowMotdOnStartup => _settings.ShowMotdOnStartup;
    public string FavouriteActionText => SelectedGame?.IsFavourite == true
        ? UiLocalizationService.Text("Ui.FavouriteOn")
        : UiLocalizationService.Text("Ui.FavouriteOff");
    public string HideActionText => SelectedGame?.IsHidden == true
        ? UiLocalizationService.Text("Ui.Show")
        : UiLocalizationService.Text("Ui.Hide");
    public bool CanRemoveManualGame => SelectedGame?.IsManuallyAdded == true && !IsBusy;

    /// <summary>
    /// The selected game was moved and RHI's records still point to its old folder (Refs #38).
    /// Cached per selection and refreshed after scans and repairs: the check reads the ledger and
    /// resolves paths, so it runs off the UI thread (<see cref="CheckRepairAfterMove"/>).
    /// </summary>
    public bool ShowRepairAfterMove => SelectedGame is { } game && ReferenceEquals(_repairCheckedFor, game) && _repairNeeded;

    private DetectedGame? _repairCheckStartedFor;
    private DetectedGame? _repairCheckedFor;
    private bool _repairNeeded;
    private int _repairCheckGeneration;

    /// <summary>Re-checks, in the background, whether the selected game needs "Repair after move".</summary>
    private void CheckRepairAfterMove(bool force)
    {
        var game = SelectedGame;
        if (!force && ReferenceEquals(game, _repairCheckStartedFor)) return;
        _repairCheckStartedFor = game;
        var generation = ++_repairCheckGeneration;
        _repairCheckedFor = null;
        _repairNeeded = false;
        OnPropertyChanged(nameof(ShowRepairAfterMove));
        if (game is null) return;
        _ = CheckAsync();

        async Task CheckAsync()
        {
            bool needed;
            try { needed = await Task.Run(() => _managedComponents.NeedsRelocationRepair(game)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                needed = false; // an unreadable ledger is reported by every operation that needs it
            }
            if (generation != _repairCheckGeneration) return;
            _repairCheckedFor = game;
            _repairNeeded = needed;
            OnPropertyChanged(nameof(ShowRepairAfterMove));
        }
    }

    private bool CanRepairAfterMove() => !IsBusy && SelectedGame is not null;
    public bool CanUpdateAll => !IsBusy && _updateAll.HasManagedTargets(_allGames);
    public string SelectedTitle => SelectedGame?.Name ?? UiLocalizationService.Text("Ui.SelectGame");
    public string SelectedSource => SelectedGame is null ? "" : $"{SelectedGame.Source}  •  {SelectedGame.Runner ?? UiLocalizationService.Text("Ui.UnknownRunner")}";

    public string SelectedLibraryProvider => SelectedGame?.LibraryProvider ?? "";
    public string SelectedLaunchProvider => SelectedGame?.LaunchProvider ?? "";
    public Bitmap? SelectedLibraryProviderIcon => ProviderIconCatalog.Get(SelectedLibraryProvider);
    public Bitmap? SelectedLaunchProviderIcon => ProviderIconCatalog.Get(SelectedLaunchProvider);
    public bool ShowLibraryProviderBadge => SelectedGame is not null && SelectedLibraryProviderIcon is not null;
    public bool ShowLaunchProviderBadge => SelectedGame is not null &&
        SelectedLaunchProviderIcon is not null &&
        !string.Equals(SelectedLaunchProvider, SelectedLibraryProvider, StringComparison.OrdinalIgnoreCase);
    public string LibraryProviderToolTip => string.IsNullOrWhiteSpace(SelectedLibraryProvider)
        ? L("Origem da biblioteca não identificada", "Library source not identified")
        : LF("Origem da biblioteca: {0}", "Library source: {0}", SelectedLibraryProvider);
    public string LaunchProviderToolTip => string.IsNullOrWhiteSpace(SelectedLaunchProvider)
        ? L("Launcher/plataforma não identificado", "Launcher/platform not identified")
        : LF("Launcher/plataforma: {0}", "Launcher/platform: {0}", SelectedLaunchProvider);

    public bool HasConfirmedPatchTarget => SelectedGame?.HasConfirmedPatchTarget == true;
    public string PatchTargetStatusText => SelectedGame switch
    {
        null => L("Nenhum jogo selecionado", "No game selected"),
        { HasConfirmedPatchTarget: true, PatchTargetAuthority: "Manual", ExecutablePath: { } manualExe } when !File.Exists(manualExe)
            => L("Alvo de modificações: Manual ⚠ executável ausente", "Modification target: Manual ⚠ executable missing"),
        { HasConfirmedPatchTarget: true, PatchTargetAuthority: "Manifest", ExecutablePath: { } manifestExe } when !File.Exists(manifestExe)
            => L("Alvo de modificações: Manifesto ⚠ executável ausente", "Modification target: Manifest ⚠ executable missing"),
        { HasConfirmedPatchTarget: true, PatchTargetAuthority: "Manual" } => L("Alvo de modificações: Manual ✓", "Modification target: Manual ✓"),
        { HasConfirmedPatchTarget: true, PatchTargetAuthority: "Manifest" } => L("Alvo de modificações: Manifesto ✓", "Modification target: Manifest ✓"),
        { HasConfirmedPatchTarget: true } => L("Alvo de modificações: Automático ✓", "Modification target: Automatic ✓"),
        _ => L("Alvo de modificações não definido — use Editar jogo antes de instalar ou aplicar componentes", "Modification target is not defined — use Edit game before installing or applying components")
    };
    public string SelectedLaunchExecutable => SelectedGame?.LaunchExecutablePath ??
        (SelectedGame is null ? "" : L("O launcher gerencia a inicialização sem expor um executável local.", "The launcher manages startup without exposing a local executable."));
    public string SelectedInstallPath => SelectedGame switch
    {
        null => "",
        { HasConfirmedPatchTarget: false } => L("Não definido — selecione o executável real do jogo em Editar jogo", "Not defined — select the real game executable in Edit game"),
        _ => SelectedGame.InstallPath
    };
    public string SelectedPrefix => SelectedGame is null
        ? L("Nenhum prefixo Wine/Proton detectado", "No Wine/Proton prefix detected")
        : string.Equals(SelectedGame.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase)
            ? L("Jogo Linux nativo — sem prefixo Wine/Proton", "Native Linux game — no Wine/Proton prefix")
            : SelectedGame.PrefixPath ?? L("Prefixo Wine/Proton não criado/detectado", "Wine/Proton prefix not created/detected");
    public string SelectedExecutable => SelectedGame switch
    {
        null => L("Executável ainda não detectado", "Executable not detected yet"),
        { HasConfirmedPatchTarget: false } => L("Não definido — não será usado o executável do launcher como alvo", "Not defined — the launcher executable will not be used as the target"),
        _ => SelectedGame.ExecutablePath ?? L("Executável ainda não detectado", "Executable not detected yet")
    };
    public string SelectedBinaryDirectory => SelectedGame switch
    {
        null => "",
        { HasConfirmedPatchTarget: false } => L("Não definido — patches e add-ons estão bloqueados", "Not defined — patches and add-ons are blocked"),
        _ => SelectedGame.BinaryDirectory ?? SelectedGame.InstallPath
    };
    public string SelectedApi
    {
        get
        {
            if (SelectedGame is null) return L("Desconhecida", "Unknown");
            if (SelectedGame.UserGraphicsApiOverride is GraphicsApi explicitApi && explicitApi != GraphicsApi.Unknown)
            {
                var detected = IsSelectionProbeBusy
                    ? L("detector ainda em execução", "detector still running")
                    : SelectedGame.GraphicsApi != GraphicsApi.Unknown
                        ? LF("detectada: {0}", "detected: {0}", FormatApi(SelectedGame.GraphicsApi))
                        : L("API detectada desconhecida", "detected API unknown");
                return LF("{0} · substituição explícita · {1}", "{0} · explicit override · {1}", FormatApi(explicitApi), detected);
            }
            if (SelectedGame.ManifestGraphicsApiOverride is GraphicsApi manifestApi && manifestApi != GraphicsApi.Unknown)
            {
                var detected = IsSelectionProbeBusy
                    ? L("detector ainda em execução", "detector still running")
                    : SelectedGame.GraphicsApi != GraphicsApi.Unknown
                        ? LF("detectada: {0}", "detected: {0}", FormatApi(SelectedGame.GraphicsApi))
                        : L("API detectada desconhecida", "detected API unknown");
                var manifestApis = SelectedGame.ManifestGraphicsApis.Count > 0
                    ? string.Join(" / ", SelectedGame.ManifestGraphicsApis.Select(FormatApi))
                    : FormatApi(manifestApi);
                return LF("{0} · dados de compatibilidade do RHI upstream · {1}", "{0} · upstream RHI compatibility data · {1}", manifestApis, detected);
            }
            if (IsSelectionProbeBusy) return L("Detectando…", "Detecting…");
            if (SelectedGame.GraphicsApi != GraphicsApi.Unknown)
                return $"{FormatApi(SelectedGame.GraphicsApi)} · {FormatGraphicsApiConfidence(SelectedGame.GraphicsApiConfidence)}";
            return SelectedGame.GraphicsApis.Count > 0
                ? $"{string.Join(" / ", SelectedGame.GraphicsApis.Select(FormatApi))} · {FormatGraphicsApiConfidence(SelectedGame.GraphicsApiConfidence)}"
                : L("Desconhecida", "Unknown");
        }
    }
    public string SelectedApiEvidence
    {
        get
        {
            if (SelectedGame is null) return L("Nenhuma assinatura confiável de API gráfica foi detectada", "No reliable graphics API signature was detected");
            var detected = IsSelectionProbeBusy
                ? L("Analisando executável, configuração do motor e arquivos locais de runtime em segundo plano…", "Analyzing executable, engine configuration and local runtime files in the background…")
                : PresentOr(SelectedGame.GraphicsApiEvidence, "Nenhuma assinatura confiável de API gráfica foi detectada", "No reliable graphics API signature was detected");
            if (SelectedGame.UserGraphicsApiOverride is GraphicsApi explicitApi && explicitApi != GraphicsApi.Unknown)
                return LF("Autoridade explícita por jogo: {0}. A evidência do detector é preservada separadamente: {1}", "Explicit per-game authority: {0}. Detector evidence is preserved separately: {1}", FormatApi(explicitApi), detected);
            if (SelectedGame.ManifestGraphicsApiOverride is GraphicsApi manifestApi && manifestApi != GraphicsApi.Unknown)
            {
                var manifestApis = SelectedGame.ManifestGraphicsApis.Count > 0
                    ? string.Join(" / ", SelectedGame.ManifestGraphicsApis.Select(FormatApi))
                    : FormatApi(manifestApi);
                return LF("Compatibilidade informada pelo RHI upstream: {0}. A detecção local é preservada separadamente: {1}", "Upstream RHI compatibility: {0}. Local detection is preserved separately: {1}", manifestApis, detected);
            }
            return detected;
        }
    }
    public bool CanSelectGraphicsApiOverride => SelectedGame is not null
        && HasConfirmedPatchTarget
        && !IsBusy
        && !string.Equals(SelectedGame.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase);
    public string GraphicsApiOverrideHint
    {
        get
        {
            if (SelectedGame is null) return L("Selecione um jogo Windows.", "Select a Windows game.");
            if (string.Equals(SelectedGame.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
                return L("Alvos Linux nativos não usam substituições de injeção da API gráfica do Windows.", "Native Linux targets do not use Windows graphics API injection overrides.");
            if (SelectedGame.UserGraphicsApiOverride == GraphicsApi.DirectX11 &&
                SelectedGame.EngineHint?.Contains("Unreal", StringComparison.OrdinalIgnoreCase) == true)
                return SelectedGame.EngineHint.Contains("Unreal Engine 5", StringComparison.OrdinalIgnoreCase)
                    ? L("A autoridade explícita DX11 habilita a exceção upstream de Luma genérico para UE5. Use somente quando o jogo realmente oferecer um caminho de inicialização DX11.", "Explicit DX11 authority enables the upstream generic Luma exception for UE5. Use it only when the game actually offers a DX11 startup path.")
                    : L("A autoridade explícita DX11 pode habilitar Luma genérico para jogos Unreal detectados localmente cuja geração do motor seja desconhecida. A evidência do detector continua visível acima.", "Explicit DX11 authority can enable generic Luma for locally detected Unreal games whose engine generation is unknown. Detector evidence remains visible above.");
            if (SelectedGame.HasExplicitGraphicsApiOverride)
                return L("A substituição explícita do usuário tem prioridade na elegibilidade dos componentes; as evidências do manifesto e do detector continuam visíveis acima.", "The explicit user override takes priority for component eligibility; manifest and detector evidence remain visible above.");
            if (SelectedGame.ManifestGraphicsApiOverride is GraphicsApi manifestApi)
            {
                var manifestApis = SelectedGame.ManifestGraphicsApis.Count > 0
                    ? string.Join(" / ", SelectedGame.ManifestGraphicsApis.Select(FormatApi))
                    : FormatApi(manifestApi);
                return LF("Automático usa primeiro os dados de compatibilidade do RHI upstream ({0}) e depois a detecção local. Escolha uma substituição explícita apenas quando quiser definir a API manualmente.", "Automatic uses upstream RHI compatibility data first ({0}) and then local detection. Choose an explicit override only when you want to set the API manually.", manifestApis);
            }
            return L("Automático usa detecção por runtime/arquivos. A autoridade explícita DX11 no estilo upstream é necessária para a exceção de Luma genérico no UE5.", "Automatic uses runtime/file detection. Upstream-style explicit DX11 authority is required for the generic Luma exception on UE5.");
        }
    }
    public bool CanSelectLumaMode => SelectedGame is not null && HasConfirmedPatchTarget && !IsBusy && !IsSelectionProbeBusy
        && !string.Equals(SelectedGame.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase);
    public string LumaPolicyText
    {
        get
        {
            if (SelectedGame is null) return L("Selecione um jogo Windows.", "Select a Windows game.");
            var policy = _managedComponents.GetLumaPolicy(SelectedGame);
            return UiLocalizationService.Present(policy.Summary);
        }
    }
    public string LumaCoexistenceText
    {
        get
        {
            if (SelectedGame is null) return "";
            var policy = _managedComponents.GetLumaPolicy(SelectedGame);
            return policy.RenoDxCompatible
                ? L("Os dados de compatibilidade do RHI upstream permitem RenoDX + Luma neste jogo.", "Upstream RHI compatibility data allows RenoDX + Luma for this game.")
                : L("RenoDX + Luma permanecem mutuamente exclusivos, salvo quando lumaRenodxCompat autoriza este jogo.", "RenoDX + Luma remain mutually exclusive unless lumaRenodxCompat authorizes this game.");
        }
    }

    public bool CanEditHdrProfile => SelectedGame is not null && !IsBusy;
    public LinuxHdrLaunchPlan SelectedHdrPlan => SelectedGame is null
        ? new LinuxHdrLaunchPlan(false, false, new Dictionary<string, string>(), null, "%command%", L("Selecione um jogo para configurar a orquestração de inicialização HDR.", "Select a game to configure HDR launch orchestration."))
        : _hdrProfiles.BuildPlan(SelectedGame, new LinuxHdrGameProfile { Mode = ParseHdrMode(SelectedHdrMode), EnableLegacyWsi = HdrLegacyWsi });
    public string HdrProfileHint
    {
        get
        {
            var plan = SelectedHdrPlan;
            if (SelectedGame is not null && plan.Enabled && LinuxHdrLaunchProfileService.IsLauncherMediated(SelectedGame))
                return UiLocalizationService.Present(plan.Summary) + L(" Inicialização mediada pela loja detectada: configure este perfil no Steam/Heroic/Lutris; o RHI não envolverá o processo do launcher.", " Store-mediated launch detected: configure this profile in Steam/Heroic/Lutris; RHI will not wrap the launcher process.");
            return UiLocalizationService.Present(plan.Summary);
        }
    }
    public string HdrLaunchOptions => SelectedHdrPlan.LaunchOptions;
    public bool CanCopyHdrLaunchOptions => SelectedGame is not null && SelectedHdrPlan.Enabled && SelectedHdrPlan.CanApply;

    public string SelectedEngineHint
    {
        get
        {
            if (IsSelectionProbeBusy) return L("Carregando dados de compatibilidade do RHI upstream…", "Loading upstream RHI compatibility data…");
            if (SelectedGame is null) return L("Nenhum jogo selecionado", "No game selected");

            var engine = string.IsNullOrWhiteSpace(SelectedGame.EngineHint)
                ? L("Sem substituição de motor pelo upstream", "No upstream engine override")
                : SelectedGame.EngineHint;
            var architecture = SelectedGame.ForcedArchitecture switch
            {
                PeArchitecture.X64 => L(" · x64 forçado", " · forced x64"),
                PeArchitecture.X86 => L(" · x86 forçado", " · forced x86"),
                _ => ""
            };
            return engine + architecture;
        }
    }
    public bool HasSelectedGame => SelectedGame is not null;
    public bool CanLaunch => SelectedGame is not null && _launcher.CanLaunch(SelectedGame);
    public string? LaunchToolTip => SelectedGame?.ParentLauncherName is { Length: > 0 } parent
        ? LF("Abre {0}. Inicie o jogo a partir do launcher.", "Opens {0}. Start the game from the launcher.", parent)
        : null;
    public bool CanEditSelectedGame => SelectedGame is not null && !IsBusy;
    public bool CanInstallD3DCompilerWithProtontricks => SelectedGame?.UsesProton == true && HasConfirmedPatchTarget;
    public bool CanInstallD3DCompilerWithWinetricks
        => HasConfirmedPatchTarget && SelectedGame is { } game
           && D3DCompilerToolInstaller.CanInstallWithWinetricks(game);
    public string D3DCompilerWinetricksButtonText
        => SelectedGame is { } game && D3DCompilerToolInstaller.IsLauncherManagedPrefix(game)
            ? LF("Instalar via Winetricks ({0})", "Install via Winetricks ({0})", game.CompatibilityTool ?? game.Runner ?? game.Source)
            : L("Instalar via Winetricks", "Install via Winetricks");
    public string D3DCompilerToolHint
        => SelectedGame switch
        {
            null => L("Selecione um jogo para determinar o auxiliar D3DCompiler correto.", "Select a game to determine the correct D3DCompiler helper."),
            { UsesProton: true } => L("Alvo Steam/Proton: Protontricks é usado com o Steam AppID.", "Steam/Proton target: Protontricks is used with the Steam AppID."),
            { SteamAppId: null } game when D3DCompilerToolInstaller.IsLauncherManagedPrefix(game)
                => LF("Alvo {0}: Winetricks usa o {1} ativo e o prefixo correspondente; executores Proton são normalizados para o Wine embutido em vez do Wine do sistema.", "{0} target: Winetricks uses the active {1} and matching prefix; Proton runners are normalized to their embedded Wine instead of system Wine.", game.Source, game.CompatibilityTool ?? game.Runner ?? L("executor do launcher", "launcher runner")),
            { SteamAppId: null, PrefixPath: not null } => L("Alvo Wine independente/do sistema: Winetricks é executado contra o WINEPREFIX detectado.", "Standalone/system Wine target: Winetricks runs against the detected WINEPREFIX."),
            _ => L("Nenhum prefixo Wine/Proton inicializado foi detectado para este jogo.", "No initialized Wine/Proton prefix was detected for this game.")
        };
    private static string ReShadeChannelDisplay(string value) => value switch
    {
        "Nightly" => UiLocalizationService.Text("Ui.OptionNightly"),
        "Legacy" => UiLocalizationService.Text("Ui.OptionLegacy"),
        "Custom" => UiLocalizationService.Text("Ui.OptionCustom"),
        _ => UiLocalizationService.Text("Ui.OptionStable")
    };

    private static string ReShadeChannelValue(string? display)
    {
        if (string.Equals(display, UiLocalizationService.Text("Ui.OptionNightly"), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(display, "Nightly", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(display, "Desenvolvimento", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(display, "Desenvolvimento (Nightly)", StringComparison.OrdinalIgnoreCase)) return "Nightly";
        if (string.Equals(display, UiLocalizationService.Text("Ui.OptionLegacy"), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(display, "Legacy", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(display, "Legado", StringComparison.OrdinalIgnoreCase)) return "Legacy";
        if (string.Equals(display, UiLocalizationService.Text("Ui.OptionCustom"), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(display, "Custom", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(display, "Personalizado", StringComparison.OrdinalIgnoreCase)) return "Custom";
        return "Stable";
    }

    public bool IsReShadeLegacy => ReShadeChannelValue(SelectedReShadeChannel).Equals("Legacy", StringComparison.OrdinalIgnoreCase);
    public bool IsReShadeCustom => ReShadeChannelValue(SelectedReShadeChannel).Equals("Custom", StringComparison.OrdinalIgnoreCase);

    public bool UalInstalled => SelectedGame is not null && _managedComponents.GetUltimateAsiLoaderRecord(SelectedGame) is not null;
    public string UalInstalledProxy => SelectedGame is null
        ? ""
        : _managedComponents.GetUltimateAsiLoaderRecord(SelectedGame)?.Metadata.GetValueOrDefault("UAL.ProxyName") ?? "";
    public string UalInstalledVersion => SelectedGame is null
        ? ""
        : _managedComponents.GetUltimateAsiLoaderRecord(SelectedGame)?.Version ?? "";
    public string UalStatusText => SelectedGame switch
    {
        null => L("Nenhum jogo selecionado", "No game selected"),
        { Runner: "Native Linux" } => L("Não compatível com alvos Linux nativos", "Unsupported for native Linux targets"),
        _ when UalInstalled => LF("Instalado {0} como {1}", "Installed {0} as {1}", UalInstalledVersion, UalInstalledProxy),
        _ when UalProxyNames.Count == 0 => L("Exige um executável Windows x86/x64 detectado e um prefixo Wine/Proton inicializado", "Requires a detected x86/x64 Windows executable and an initialized Wine/Proton prefix"),
        _ => L("Pronto", "Ready")
    };
    public bool CanInstallUal => SelectedGame is not null && !IsBusy && !IsSelectionProbeBusy && !UalInstalled
                                 && UalProxyNames.Count > 0 && !string.IsNullOrWhiteSpace(SelectedUalProxyName);
    public bool CanUpdateUal => SelectedGame is not null && !IsBusy && !IsSelectionProbeBusy && UalInstalled;
    public bool CanInstallOrUpdateUal => UalInstalled ? CanUpdateUal : CanInstallUal;
    public bool CanRemoveUal => CanUpdateUal && !ShortFuseInstalled;
    public bool CanSelectUalProxy => SelectedGame is not null && !IsBusy && !IsSelectionProbeBusy && !UalInstalled && UalProxyNames.Count > 0;
    public string UalActionText => UalInstalled ? L("Atualizar", "Update") : L("Instalar", "Install");
    public string UalProxyHint
    {
        get
        {
            if (SelectedGame is null) return L("Selecione um jogo Windows.", "Select a Windows game.");
            if (UalInstalled) return LF("O RHI gerencia {0}. Remova-o primeiro para escolher outro nome de proxy.", "RHI manages {0}. Remove it first to choose another proxy name.", UalInstalledProxy);
            if (string.IsNullOrWhiteSpace(SelectedUalProxyName)) return L("Escolha um nome de DLL proxy para o ASI Loader.", "Choose a proxy DLL name for the ASI Loader.");
            var notes = new List<string>();
            if (UltimateAsiLoaderDeploymentService.RecommendedNames.Contains(SelectedUalProxyName, StringComparer.OrdinalIgnoreCase))
                notes.Add(L("Recomendado pelo RHI", "Recommended by RHI"));
            if (UltimateAsiLoaderDeploymentService.RhiConflictNames.Contains(SelectedUalProxyName, StringComparer.OrdinalIgnoreCase))
                notes.Add(L("pode conflitar com ReShade/OptiScaler", "may conflict with ReShade/OptiScaler"));
            var dir = SelectedGame.BinaryDirectory ?? Path.GetDirectoryName(SelectedGame.ExecutablePath ?? "");
            if (!string.IsNullOrWhiteSpace(dir))
            {
                var target = Path.Combine(dir, SelectedUalProxyName);
                if (File.Exists(target))
                    notes.Add(LF("{0} existente será preservado como {1}, salvo se for gerenciado pelo RHI", "Existing {0} will be preserved as {1} unless it is managed by RHI", SelectedUalProxyName, UltimateAsiLoaderDeploymentService.HookedName(SelectedUalProxyName)));
            }
            return notes.Count == 0
                ? L("O override do Wine é limitado ao jogo selecionado: native,builtin.", "The Wine override is scoped to the selected game: native,builtin.")
                : string.Join(" · ", notes) + ".";
        }
    }

    public bool ShortFuseInstalled => SelectedGame is not null && _managedComponents.IsShortFuseManaged(SelectedGame);
    public string ShortFuseInstalledVersion => SelectedGame is null ? "" : _managedComponents.GetShortFuseRecord(SelectedGame)?.Version ?? "";
    private ManagedComponent? CurrentShortFuseComponent
        => SelectedGame is not null && _probeCache.TryGetValue(Key(SelectedGame), out var probe)
            ? probe.Components.FirstOrDefault(c => c.Kind == ManagedComponentKind.ShortFuse)
            : null;

    public string ShortFuseStatusText => SelectedGame switch
    {
        null => L("Nenhum jogo selecionado", "No game selected"),
        { Runner: "Native Linux" } => L("Não compatível com alvos Linux nativos", "Unsupported for native Linux targets"),
        _ when ShortFuseInstalled => LF("ATIVADO · {0} · {1}", "ENABLED · {0} · {1}", ShortFuseInstalledVersion, ShortFuseAutoConfigEnabled ? L("Configuração ASI automática", "Automatic ASI configuration") : L("Configuração ASI manual", "Manual ASI configuration")),
        _ => LF("DESATIVADO · Configuração ASI automática {0}", "DISABLED · Automatic ASI configuration {0}", ShortFuseAutoConfigEnabled ? L("ativada", "enabled") : L("opcional / desativada", "optional / disabled"))
    };
    public string ShortFuseActionText => ShortFuseInstalled ? L("Remover", "Remove") : L("Instalar", "Install");
    public bool ShortFuseNotInstalled => !ShortFuseInstalled;
    public bool CanInstallOrRemoveShortFuse
        => SelectedGame is not null && !IsBusy && !IsSelectionProbeBusy
           && (ShortFuseInstalled || (CurrentShortFuseComponent?.Availability == ComponentAvailability.Available
                                      && CurrentShortFuseComponent.ActionAllowed));
    public string ShortFuseActionHint
        => ShortFuseInstalled
            ? L("Remove o ShortFuse e restaura transacionalmente o estado anterior de DLSS/Streamline/ReShade/UAL.", "Removes ShortFuse and transactionally restores the previous DLSS/Streamline/ReShade/UAL state.")
            : CurrentShortFuseComponent?.ActionAllowed == true
                ? PresentOr(CurrentShortFuseComponent.Note, "ShortFuse está disponível para este jogo.", "ShortFuse is available for this game.")
                : PresentOr(CurrentShortFuseComponent?.Note, "ShortFuse ficará disponível após a conclusão da análise do jogo.", "ShortFuse will become available after game analysis completes.");
    public bool CanConfigureShortFuse => SelectedGame is not null && !IsBusy && !IsSelectionProbeBusy && !ShortFuseInstalled;
    // Pins stay editable while installed: a change swaps the add-on in place (upstream 2.7.6).
    public bool CanConfigureFeederVersionPin => SelectedGame is not null && !IsBusy && !IsSelectionProbeBusy;
    public bool CanConfigureBridgeVersionPin => SelectedGame is not null && !IsBusy && !IsSelectionProbeBusy;
    public bool CanConfigureShortFuseVersionPin => SelectedGame is not null && !IsBusy && !IsSelectionProbeBusy;
    public bool CanConfigureNeuralRenderingDllVersion => SelectedGame is not null && !IsBusy && !IsSelectionProbeBusy;
    public bool CanLoadNeuralVersionHistory => !IsBusy;
    public bool OrchestratedDlssStackManaged => Dlss5FeederFullManaged || ShortFuseInstalled;

    public bool Dlss5FeederFullManaged => SelectedGame is not null && _managedComponents.IsDlss5FeederFullManaged(SelectedGame);
    public bool CanSwapDlssSr => HasConfirmedPatchTarget && DlssSrDetected && !OrchestratedDlssStackManaged;
    public bool CanSwapDlssRr => HasConfirmedPatchTarget && DlssRrDetected && !OrchestratedDlssStackManaged;
    public bool CanSwapDlssFg => HasConfirmedPatchTarget && DlssFgDetected && !OrchestratedDlssStackManaged;
    public bool CanSwapDlssNr => HasConfirmedPatchTarget && DlssNrDetected && !OrchestratedDlssStackManaged;
    public bool CanSwapStreamline => HasConfirmedPatchTarget && StreamlineDetected && !OrchestratedDlssStackManaged;

    public string NeuralRenderingRecommendation
    {
        get
        {
            if (SelectedGame is null) return L("Nenhum jogo selecionado", "No game selected");
            if (!SelectedGame.HasConfirmedPatchTarget) return L("Aguardando executável alvo — nenhuma recomendação será calculada a partir do launcher", "Waiting for target executable — no recommendation will be calculated from the launcher");
            if (string.Equals(SelectedGame.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
                return L("Métodos Windows de Renderização Neural não estão disponíveis para alvos Linux nativos.", "Windows Neural Rendering methods are not available for native Linux targets.");

            var hasNativeDlss = _managedComponents.HasNativeDlssForNeuralRendering(
                SelectedGame, _dlssDetection.DlssPaths.Count > 0);
            var recommended = LinuxManagedComponentService.RecommendedNeuralRenderingMethod(SelectedGame, hasNativeDlss);
            var architecture = SelectedGame.ForcedArchitecture ?? PeArchitectureDetector.Detect(SelectedGame.ExecutablePath);
            if (architecture == PeArchitecture.X86 && recommended == ManagedComponentKind.Dlss5Feeder)
                return L("DLSS5 Feeder · 32-bit suportado via addon32 + host64 com consumidor RenoDX DLSS5", "DLSS5 Feeder · 32-bit supported via addon32 + host64 with RenoDX DLSS5 consumer");

            return recommended switch
            {
                ManagedComponentKind.ShortFuse => L("DLSS Tool (ShortFuse) · recomendado para jogos 64-bit com DLSS nativo em DX12/outros", "DLSS Tool (ShortFuse) · recommended for 64-bit games with native DLSS on DX12/other APIs"),
                ManagedComponentKind.Dlss5Dx11Bridge => L("DLSS5 Tool + DX11 Bridge · recomendado para jogos DX11/Vulkan com DLSS nativo", "DLSS5 Tool + DX11 Bridge · recommended for DX11/Vulkan games with native DLSS"),
                ManagedComponentKind.Dlss5Feeder => L("DLSS5 Feeder · recomendado quando o jogo não possui DLSS nativo", "DLSS5 Feeder · recommended when the game has no native DLSS"),
                _ => L("Nenhuma recomendação de Renderização Neural", "No Neural Rendering recommendation")
            };
        }
    }

    public bool DlssSrDetected => _dlssDetection.DlssPaths.Count > 0;
    public bool DlssRrDetected => _dlssDetection.DlssdPaths.Count > 0;
    public bool DlssFgDetected => _dlssDetection.DlssgPaths.Count > 0;
    public bool DlssNrDetected => _dlssDetection.DlssnrPaths.Count > 0;
    public bool StreamlineDetected => _dlssDetection.StreamlineFolders.Count > 0;

    // A restore button is meaningful only after RHI actually created an .original backup.
    // This also doubles as a persistent visual signal that survives switching games/restarting RHI.
    public bool DlssSrRestoreAvailable => HasConfirmedPatchTarget && !OrchestratedDlssStackManaged && HasRhiBackup(_dlssDetection.DlssPaths);
    public bool DlssRrRestoreAvailable => HasConfirmedPatchTarget && !OrchestratedDlssStackManaged && HasRhiBackup(_dlssDetection.DlssdPaths);
    public bool DlssFgRestoreAvailable => HasConfirmedPatchTarget && !OrchestratedDlssStackManaged && HasRhiBackup(_dlssDetection.DlssgPaths);
    public bool DlssNrRestoreAvailable => HasConfirmedPatchTarget && !OrchestratedDlssStackManaged && HasRhiBackup(_dlssDetection.DlssnrPaths);
    public bool StreamlineRestoreAvailable => HasConfirmedPatchTarget && !OrchestratedDlssStackManaged && CountStreamlineBackups() > 0;

    public string DlssSrDeploymentState => ShortFuseInstalled && DlssSrDetected
        ? L("✓ ShortFuse · gerenciado", "✓ ShortFuse · managed")
        : Dlss5FeederFullManaged && DlssSrDetected ? L("✓ Feeder Full · privado", "✓ Feeder Full · private") : DlssDeploymentState(_dlssDetection.DlssPaths);
    public string DlssRrDeploymentState => ShortFuseInstalled && DlssRrDetected ? L("✓ ShortFuse · gerenciado", "✓ ShortFuse · managed") : DlssDeploymentState(_dlssDetection.DlssdPaths);
    public string DlssFgDeploymentState => ShortFuseInstalled && DlssFgDetected ? L("✓ ShortFuse · gerenciado", "✓ ShortFuse · managed") : DlssDeploymentState(_dlssDetection.DlssgPaths);
    public string DlssNrDeploymentState => ShortFuseInstalled && DlssNrDetected
        ? L("✓ ShortFuse · gerenciado", "✓ ShortFuse · managed")
        : Dlss5FeederFullManaged && DlssNrDetected ? L("✓ Feeder Full · privado", "✓ Feeder Full · private") : DlssDeploymentState(_dlssDetection.DlssnrPaths);
    public string StreamlineDeploymentState
    {
        get
        {
            if (_dlssDetection.StreamlineFolders.Count == 0) return L("— Ausente", "— Missing");
            if (ShortFuseInstalled) return L("✓ ShortFuse · gerenciado", "✓ ShortFuse · managed");
            var backups = CountStreamlineBackups();
            return backups > 0 ? LF("✓ RHI aplicado · {0} DLL(s)", "✓ RHI applied · {0} DLL(s)", backups) : L("• DLLs do jogo", "• Game DLLs");
        }
    }

    public string DlssSrPath => FirstOrNotFound(_dlssDetection.DlssPaths, "nvngx_dlss.dll");
    public string DlssRrPath => FirstOrNotFound(_dlssDetection.DlssdPaths, "nvngx_dlssd.dll");
    public string DlssFgPath => FirstOrNotFound(_dlssDetection.DlssgPaths, "nvngx_dlssg.dll");
    public string DlssNrPath => FirstOrNotFound(_dlssDetection.DlssnrPaths, "nvngx_dlssnr.dll");
    public string StreamlinePath => _dlssDetection.StreamlineFolders.FirstOrDefault()
        ?? L("Streamline não detectado — atualização indisponível", "Streamline not detected — update unavailable");
    public string DlssStatusSummary => IsSelectionProbeBusy
        ? L("Analisando arquivos do jogo em segundo plano…", "Analyzing game files in the background…")
        : ShortFuseInstalled
            ? L("ShortFuse gerenciado — a pilha DLSS/Streamline possui controle transacional; a troca direta de DLLs fica bloqueada até remover o ShortFuse", "Managed ShortFuse — the DLSS/Streamline stack is transactionally controlled; direct DLL swaps are blocked until ShortFuse is removed")
        : Dlss5FeederFullManaged
            ? L("DLSS5 Feeder Full gerenciado — pilha privada de Super-resolução / Renderização Neural; a troca direta de DLLs fica bloqueada até remover o Feeder", "Managed DLSS5 Feeder Full — private Super Resolution / Neural Rendering stack; direct DLL swaps are blocked until Feeder is removed")
            : _dlssDetection.HasAny
                ? LF("Detectado — Super-resolução: {0} · Reconstrução de raios: {1} · Geração de quadros: {2} · Renderização neural: {3} · Streamline: {4}", "Detected — Super Resolution: {0} · Ray Reconstruction: {1} · Frame Generation: {2} · Neural Rendering: {3} · Streamline: {4}", _dlssDetection.DlssPaths.Count, _dlssDetection.DlssdPaths.Count, _dlssDetection.DlssgPaths.Count, _dlssDetection.DlssnrPaths.Count, _dlssDetection.StreamlineFolders.Count)
                : L("Nenhuma DLL local de DLSS/Streamline detectada — a troca direta de DLLs está desativada para este jogo", "No local DLSS/Streamline DLL detected — direct DLL swapping is disabled for this game");

    public LinuxDlssVersionDefaults DlssVersionDefaults => new(
        _settings.DefaultDlssVersion,
        _settings.DefaultDlssdVersion,
        _settings.DefaultDlssgVersion,
        _settings.DefaultDlssnrVersion,
        _settings.DefaultStreamlineVersion);

    public bool HasDlssVersionDefaults => DlssVersionDefaults.HasAny;
    public string DlssDefaultsSummary
    {
        get
        {
            static string Show(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
            var d = DlssVersionDefaults;
            return LF("Padrões · SR {0} · RR {1} · FG {2} · NR {3} · SL {4}", "Defaults · SR {0} · RR {1} · FG {2} · NR {3} · SL {4}", Show(d.SuperResolution), Show(d.RayReconstruction), Show(d.FrameGeneration), Show(d.NeuralRendering), Show(d.Streamline));
        }
    }

    public bool CanQuickApplyDlssDefaults =>
        SelectedGame is not null && HasConfirmedPatchTarget && !IsBusy && !IsSelectionProbeBusy && HasDlssVersionDefaults &&
        _dlssDetection.HasAny && !OrchestratedDlssStackManaged &&
        !string.Equals(SelectedGame.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase);

    public bool CanRestoreAllDlss =>
        SelectedGame is not null && HasConfirmedPatchTarget && !IsBusy && !IsSelectionProbeBusy && !OrchestratedDlssStackManaged &&
        (DlssSrRestoreAvailable || DlssRrRestoreAvailable || DlssFgRestoreAvailable || DlssNrRestoreAvailable || StreamlineRestoreAvailable);

    public bool CanOpenDlssBatch => !IsBusy && _allGames.Any(game =>
        game.HasConfirmedPatchTarget &&
        !string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase) &&
        !game.DlssScanSkippedByManifest && Directory.Exists(game.InstallPath));

    public MainWindowViewModel(
        GameLibraryScanner scanner,
        IGameLauncher launcher,
        IGameRuntimeProbe runtimeProbe,
        DlssManagerService dlss,
        LinuxManagedComponentService managedComponents,
        GraphicsApiOverrideStore? apiOverrideStore = null,
        OptiScalerVariantPreferenceStore? optiScalerVariantStore = null,
        LinuxSettingsStore? settingsStore = null,
        LinuxUpdateAllService? updateAllService = null,
        LinuxDlssDefaultsService? dlssDefaultsService = null,
        LinuxGameLibraryStore? libraryStore = null,
        LinuxDeploymentRecordStore? deploymentRecords = null,
        LinuxHdrLaunchProfileService? hdrProfiles = null)
    {
        _scanner = scanner;
        _launcher = launcher;
        _runtimeProbe = runtimeProbe;
        _dlss = dlss;
        _managedComponents = managedComponents;
        _apiOverrideStore = apiOverrideStore ?? new GraphicsApiOverrideStore();
        _optiScalerVariantStore = optiScalerVariantStore ?? new OptiScalerVariantPreferenceStore();
        _settingsStore = settingsStore ?? new LinuxSettingsStore();
        _updateAll = updateAllService ?? new LinuxUpdateAllService(new LinuxDeploymentRecordStore(), managedComponents);
        _dlssDefaults = dlssDefaultsService ?? new LinuxDlssDefaultsService(dlss, new LinuxDeploymentRecordStore());
        _libraryStore = libraryStore ?? new LinuxGameLibraryStore();
        _deploymentRecords = deploymentRecords ?? new LinuxDeploymentRecordStore();
        _hdrProfiles = hdrProfiles ?? new LinuxHdrLaunchProfileService();
        // Attached before the first load and detached by Dispose; it also receives any store set
        // aside before a listener existed (e.g. settings read at startup).
        _quarantineSubscription = JsonStoreQuarantine.Listen(notice => Dispatcher.UIThread.Post(() => ReportStoreQuarantined(notice)));
        _settings = _settingsStore.Load();
        _libraryState = _libraryStore.Load();
        LibraryFilterMode = _libraryState.FilterMode;
        ViewMode = _settings.ViewMode;
        SelectedReShadeChannel = ReShadeChannelDisplay(_settings.ReShadeChannel);
        ReShadeManageIni = _settings.ReShadeManageIni;
        ReShadeUpdateText = UiLocalizationService.Format("Ui.ReShadeGlobalDefault", ReShadeChannelDisplay(_settings.ReShadeChannel));
        _ = InitializeAsync();
    }

    private readonly IDisposable _quarantineSubscription;
    private bool _disposed;

    /// <summary>Detaches the process-wide store notices and stops the background update loop.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _quarantineSubscription.Dispose();
        try { _backgroundUpdateCts?.Cancel(); }
        catch (ObjectDisposedException) { /* already stopped */ }
        _backgroundUpdateCts?.Dispose();
        _backgroundUpdateCts = null;
        GC.SuppressFinalize(this);
    }

    public LinuxSettingsUiSnapshot GetLinuxSettingsSnapshot()
    {
        var copy = new LinuxAppSettings
        {
            UiLanguage = _settings.UiLanguage,
            ReShadeChannel = _settings.ReShadeChannel,
            ReShadeManageIni = _settings.ReShadeManageIni,
            ScreenshotPath = _settings.ScreenshotPath,
            OverlayHotkey = _settings.OverlayHotkey,
            ScreenshotHotkey = _settings.ScreenshotHotkey,
            PerGameScreenshotFolders = _settings.PerGameScreenshotFolders,
            AddonWatchFolder = _settings.AddonWatchFolder,
            AddonWatcherEnabled = _settings.AddonWatcherEnabled,
            ViewMode = _settings.ViewMode,
            TrayIconEnabled = _settings.TrayIconEnabled,
            CloseToTray = _settings.CloseToTray,
            ShowMotdOnStartup = _settings.ShowMotdOnStartup,
            CacheAllShaders = _settings.CacheAllShaders,
            VerboseLogging = _settings.VerboseLogging,
            AutoUpdateComponents = _settings.AutoUpdateComponents,
            UpdateCheckIntervalHours = _settings.UpdateCheckIntervalHours,
            DefaultDlssVersion = _settings.DefaultDlssVersion,
            DefaultDlssdVersion = _settings.DefaultDlssdVersion,
            DefaultDlssgVersion = _settings.DefaultDlssgVersion,
            DefaultDlssnrVersion = _settings.DefaultDlssnrVersion,
            DefaultStreamlineVersion = _settings.DefaultStreamlineVersion
        };
        return new(copy, _settingsStore.SettingsPath, _settingsStore.ConfigRoot, _settingsStore.CacheRoot, _settingsStore.DataRoot);
    }

    public string SaveLinuxSettings(LinuxAppSettings settings)
    {
        var previousAutoUpdate = _settings.AutoUpdateComponents;
        var previousInterval = _settings.UpdateCheckIntervalHours;
        if (TrySaveStore(() => _settingsStore.Save(settings)) is { } notSaved) return notSaved;
        _settings = _settingsStore.Load();
        UiLocalizationService.Apply(_settings.UiLanguage);
        ViewMode = _settings.ViewMode;
        ReShadeManageIni = _settings.ReShadeManageIni;
        RefreshLocalizedPresentation();
        NotifyUxSettings();
        if (previousAutoUpdate != _settings.AutoUpdateComponents || previousInterval != _settings.UpdateCheckIntervalHours)
            RestartBackgroundUpdateLoop(runImmediate: _settings.AutoUpdateComponents);
        NotifyDlssDefaults();
        return UiLocalizationService.ChooseFormat("Configurações salvas em {0}.", "Settings saved to {0}.", _settingsStore.SettingsPath);
    }

    public string ResetLinuxSettings()
    {
        LinuxAppSettings? reset = null;
        if (TrySaveStore(() => reset = _settingsStore.Reset()) is { } notReset) return notReset;
        _settings = reset!;
        UiLocalizationService.Apply(_settings.UiLanguage);
        ViewMode = _settings.ViewMode;
        ReShadeManageIni = _settings.ReShadeManageIni;
        RefreshLocalizedPresentation();
        NotifyUxSettings();
        RestartBackgroundUpdateLoop(runImmediate: false);
        NotifyDlssDefaults();
        return UiLocalizationService.Choose("Configurações restauradas para os padrões do Linux.", "Settings restored to Linux defaults.");
    }

    public string PurgeLinuxCache()
    {
        var removed = _settingsStore.PurgeCache();
        return removed == 0 ? UiLocalizationService.Choose("O cache do RHI-Linux já está vazio.", "The RHI-Linux cache is already empty.") : UiLocalizationService.ChooseFormat("{0} arquivo(s) removido(s) do cache.", "{0} file(s) removed from cache.", removed);
    }

    private void RefreshLocalizedPresentation()
    {
        _rebuildingOptionLists = true;
        try { RefreshLocalizedPresentationCore(); }
        finally { _rebuildingOptionLists = false; }
    }

    private void RefreshLocalizedPresentationCore()
    {
        var channel = ReShadeChannelValue(SelectedReShadeChannel);
        var apiOverride = ParseGraphicsApiOverride(SelectedGraphicsApiOverride);
        var optiScalerVariant = ParseOptiScalerVariant(SelectedOptiScalerVariant);
        var lumaPreference = ParseLumaModePreference(SelectedLumaModePreference);
        var hdrMode = ParseHdrMode(SelectedHdrMode);
        var feederPin = IsLatestSelection(SelectedDlss5FeederVersion) ? null : SelectedDlss5FeederVersion;
        var bridgePin = IsLatestSelection(SelectedDlss5BridgeVersion) ? null : SelectedDlss5BridgeVersion;
        var shortFusePin = IsLatestSelection(SelectedShortFuseVersion) ? null : SelectedShortFuseVersion;
        var nrDllPin = IsLatestSelection(SelectedNeuralRenderingDllVersion) ? null : SelectedNeuralRenderingDllVersion;

        ReShadeChannels.Clear();
        foreach (var item in new[] { "Ui.OptionStable", "Ui.OptionNightly", "Ui.OptionLegacy", "Ui.OptionCustom" })
            ReShadeChannels.Add(UiLocalizationService.Text(item));
        GraphicsApiOverrideOptions[0] = UiLocalizationService.Text("Ui.OptionAutomatic");
        OptiScalerVariantOptions.Clear();
        OptiScalerVariantOptions.Add(UiLocalizationService.Text("Ui.OptionStable"));
        OptiScalerVariantOptions.Add(UiLocalizationService.Text("Ui.OptionNightly"));
        OptiScalerVariantOptions.Add("DLSS NR");
        HdrModeOptions.Clear();
        HdrModeOptions.Add(UiLocalizationService.Text("Ui.HdrOff"));
        HdrModeOptions.Add(UiLocalizationService.Text("Ui.HdrNativeWayland"));
        HdrModeOptions.Add(UiLocalizationService.Text("Ui.HdrGamescope"));
        LumaModeOptions.Clear();
        foreach (var item in new[] { "Ui.OptionAutomatic", "Ui.OptionEnabled", "Ui.OptionDisabled" })
            LumaModeOptions.Add(UiLocalizationService.Text(item));

        SelectedReShadeChannel = ReShadeChannelDisplay(channel);
        _loadingGraphicsApiOverride = true;
        _loadingOptiScalerVariant = true;
        _loadingLumaModePreference = true;
        _loadingHdrProfile = true;
        try
        {
            SelectedGraphicsApiOverride = FormatGraphicsApiOverride(apiOverride);
            SelectedOptiScalerVariant = FormatOptiScalerVariant(optiScalerVariant);
            SelectedLumaModePreference = FormatLumaModePreference(lumaPreference);
            SelectedHdrMode = FormatHdrMode(hdrMode);
        }
        finally
        {
            _loadingGraphicsApiOverride = false;
            _loadingOptiScalerVariant = false;
            _loadingLumaModePreference = false;
            _loadingHdrProfile = false;
        }
        SelectedDlss5FeederVersion = feederPin ?? LatestDisplay();
        SelectedDlss5BridgeVersion = bridgePin ?? LatestDisplay();
        SelectedShortFuseVersion = shortFusePin ?? LatestDisplay();
        SelectedNeuralRenderingDllVersion = nrDllPin ?? LatestDisplay();
        ReplaceLatestLabel(Dlss5FeederVersionOptions, SelectedDlss5FeederVersion);
        ReplaceLatestLabel(Dlss5BridgeVersionOptions, SelectedDlss5BridgeVersion);
        ReplaceLatestLabel(ShortFuseVersionOptions, SelectedShortFuseVersion);
        ReplaceLatestLabel(NeuralRenderingDllVersionOptions, SelectedNeuralRenderingDllVersion);

        ReShadeUpdateText = UiLocalizationService.Format("Ui.ReShadeGlobalDefault", ReShadeChannelDisplay(_settings.ReShadeChannel));
        if (SelectedGame is { } game)
        {
            RefreshUalState(game);
            RefreshShortFuseState(game);
            if (_probeCache.TryGetValue(Key(game), out var cached))
            {
                Components.Clear();
                foreach (var item in cached.Components) Components.Add(new ComponentRowViewModel(item, ManageComponentCommand));
            }
        }
        else
        {
            UalLatestVersionText = UiLocalizationService.Text("Ui.NotCheckedYet");
            ShortFuseLatestVersionText = UiLocalizationService.Text("Ui.NotCheckedYet");
        }

        NotifySelected();
        OnPropertyChanged(nameof(ProductVersionText));
        OnPropertyChanged(nameof(GameCountText));
        OnPropertyChanged(nameof(ViewToggleText));
        OnPropertyChanged(nameof(D3DCompilerWinetricksButtonText));
        OnPropertyChanged(nameof(D3DCompilerToolHint));
        NotifyDlss();
    }

    private static void ReplaceLatestLabel(ObservableCollection<string> options, string selected)
    {
        var tags = options.Where(x => !IsLatestSelection(x)).ToArray();
        options.Clear();
        options.Add(LatestDisplay());
        foreach (var tag in tags)
            if (!options.Contains(tag, StringComparer.OrdinalIgnoreCase)) options.Add(tag);
        EnsureOption(options, selected);
    }

    public string ApplyGlobalReShadeSettingsToSelectedGame(LinuxAppSettings settings)
    {
        if (SelectedGame is null) return L("Selecione um jogo primeiro.", "Select a game first.");
        if (string.Equals(SelectedGame.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
            return L("Configurações Windows do ReShade não são aplicadas a alvos Linux nativos.", "Windows ReShade settings are not applied to native Linux targets.");
        if (!SelectedGame.HasConfirmedPatchTarget)
            return L("Defina o executável alvo do jogo em Editar jogo antes de aplicar configurações do ReShade.", "Set the target game executable in Edit game before applying ReShade settings.");
        if (!_probeCache.TryGetValue(Key(SelectedGame), out var probe) ||
            !probe.Components.Any(component => component.Kind == ManagedComponentKind.ReShade &&
                component.Availability is ComponentAvailability.Installed or ComponentAvailability.UpdateAvailable))
            return L("Instale ou detecte o ReShade no jogo selecionado antes de aplicar os padrões globais de captura/atalhos.", "Install or detect ReShade in the selected game before applying global capture/hotkey defaults.");
        var path = ReShadeGlobalSettingsApplier.ApplyToGame(settings, SelectedGame);
        return LF("Padrões de captura/atalhos do ReShade aplicados em {0}.", "ReShade capture/hotkey defaults applied at {0}.", path);
    }

    private async Task InitializeAsync()
    {
        var neuralVersionCatalogTask = EnsureNeuralVersionCatalogAsync();
        IsBusy = true;
        try
        {
            StatusText = L("Carregando o manifesto de DLSS…", "Loading DLSS manifest…");
            var manifest = await _dlss.FetchManifestAsync();
            FillVersions(DlssSrVersions, manifest.Dlss.Select(x => x.Version));
            FillVersions(DlssRrVersions, manifest.Dlssd.Select(x => x.Version));
            FillVersions(DlssFgVersions, manifest.Dlssg.Select(x => x.Version));
            FillVersions(DlssNrVersions, manifest.Dlssnr.Select(x => x.Version));
            var nrDllSelected = SelectedNeuralRenderingDllVersion;
            _rebuildingOptionLists = true;
            try
            {
                FillVersions(NeuralRenderingDllVersionOptions, manifest.Dlssnr.Select(x => x.Version).Prepend(LatestDisplay()));
                EnsureOption(NeuralRenderingDllVersionOptions, nrDllSelected);
                SelectedNeuralRenderingDllVersion = nrDllSelected ?? LatestDisplay();
            }
            finally { _rebuildingOptionLists = false; }
            FillVersions(StreamlineVersions, manifest.Streamline.Select(x => x.Version));
            SelectedDlssSrVersion = DlssSrVersions.FirstOrDefault();
            SelectedDlssRrVersion = DlssRrVersions.FirstOrDefault();
            SelectedDlssFgVersion = DlssFgVersions.FirstOrDefault();
            SelectedDlssNrVersion = DlssNrVersions.FirstOrDefault();
            SelectedStreamlineVersion = StreamlineVersions.FirstOrDefault();
        }
        catch (Exception ex)
        {
            StatusText = LF("Manifesto de DLSS indisponível: {0}", "DLSS manifest unavailable: {0}", ex.Message);
        }
        finally { IsBusy = false; }

        await RefreshAsync();
        await neuralVersionCatalogTask;
        RestartBackgroundUpdateLoop(runImmediate: _settings.AutoUpdateComponents);
    }

    // Computed action properties in Extras depend on IsBusy, but the initial full-library
    // Refresh can finish the per-game probe before the outer scan clears IsBusy. The managed
    // picker rows already have their decorated ActionAllowed state at that point, while UAL /
    // ShortFuse can otherwise remain visually disabled until another manual Refresh. Re-notify
    // those computed states on every busy transition so both surfaces converge automatically.
    partial void OnIsBusyChanged(bool value)
    {
        _statusNotice.SetBusy(value);
        OnPropertyChanged(nameof(CanUpdateAll));
        UpdateAllCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanRemoveManualGame));
        RemoveManualGameCommand.NotifyCanExecuteChanged();
        RepairAfterMoveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanEditSelectedGame));
        NotifyHdrProfile();
        OnPropertyChanged(nameof(CanSelectGraphicsApiOverride));
        OnPropertyChanged(nameof(CanSelectLumaMode));
        NotifyUal();
        NotifyShortFuse();
        NotifyDlssDefaults();
        NotifyDlss();
    }

    /// <summary>
    /// GitHub answered 401 to the saved API token (Refs #133): say so once, appended to the
    /// current status line (an operation's result stays visible); the token handler already
    /// continues anonymously. Must run on the UI thread.
    /// </summary>
    public void ReportGitHubTokenRejected() => StatusText = _statusNotice.Raise(L(
        "O GitHub rejeitou o token salvo em github_api.txt (revogado ou expirado); usando acesso anônimo (60 requisições/hora) até o arquivo ser substituído.",
        "GitHub rejected the token saved in github_api.txt (revoked or expired); using anonymous access (60 requests/hour) until the file is replaced."),
        StatusText);

    /// <summary>
    /// Start-up deployment recovery (#23) undid, could not undo, or deferred interrupted installs, or
    /// moved leftover copies aside: say so once in the status line. Must run on the UI thread.
    /// </summary>
    public void ReportDeploymentRecovery(DeploymentRecoverySummary summary)
    {
        var parts = new List<string>();
        if (summary.RolledBack > 0)
            parts.Add(L($"RHI desfez {summary.RolledBack} instalação(ões) interrompida(s) por um encerramento inesperado; os arquivos voltaram ao estado anterior.",
                $"RHI undid {summary.RolledBack} install(s) interrupted by an unexpected shutdown; the files are back as they were."));
        if (summary.KeptLaterChanges > 0)
            parts.Add(L($"RHI desfez {summary.KeptLaterChanges} instalação(ões) interrompida(s), mas manteve arquivos ou ajustes alterados depois do encerramento; as cópias anteriores do RHI estão em .rhi-recovered ao lado deles. Detalhes: {summary.LogPath}",
                $"RHI undid {summary.KeptLaterChanges} interrupted install(s) but kept files or settings changed after the shutdown; RHI's earlier copies are in .rhi-recovered next to them. Details: {summary.LogPath}"));
        if (summary.RollbackFailed > 0)
            parts.Add(L($"RHI não conseguiu desfazer {summary.RollbackFailed} instalação(ões) interrompida(s) e tentará de novo na próxima abertura. Detalhes: {summary.LogPath}",
                $"RHI could not undo {summary.RollbackFailed} interrupted install(s) and will try again at the next start. Details: {summary.LogPath}"));
        if (summary.Deferred > 0)
            parts.Add(L($"{summary.Deferred} instalação(ões) interrompida(s) serão concluídas numa próxima abertura (registro de implantações ilegível ou Wine em execução no prefixo). Detalhes: {summary.LogPath}",
                $"{summary.Deferred} interrupted install(s) will be finished at a later start (deployment ledger unreadable, or Wine running in the prefix). Details: {summary.LogPath}"));
        if (summary.Quarantined > 0)
            parts.Add(L($"RHI guardou {summary.Quarantined} cópia(s) antiga(s) que não pôde provar redundantes em .rhi-recovered, na mesma pasta. Detalhes: {summary.LogPath}",
                $"RHI set aside {summary.Quarantined} old copy(ies) it could not prove redundant in .rhi-recovered in the same folder. Details: {summary.LogPath}"));
        if (parts.Count > 0)
            StatusText = _statusNotice.Raise(string.Join(" ", parts), StatusText);
    }

    /// <summary>
    /// A JSON store file could not be read and was set aside (Refs #57): say once which file and
    /// where its copy is, appended to the current status line. Must run on the UI thread.
    /// </summary>
    public void ReportStoreQuarantined(JsonStoreQuarantineNotice notice)
        => StatusText = _statusNotice.Raise(StoreQuarantinedText(notice), StatusText);

    public static string StoreQuarantinedText(JsonStoreQuarantineNotice notice)
    {
        var name = Path.GetFileName(notice.FilePath);
        if (!notice.WritesRefused)
            return notice.QuarantinePath is null
                ? LF("O cache {0} não pôde ser lido e será recriado.", "The cache {0} could not be read and will be rebuilt.", name)
                : LF("O cache {0} não pôde ser lido; uma cópia foi guardada em {1} e ele será recriado.",
                    "The cache {0} could not be read; a copy was kept at {1} and it will be rebuilt.", name, notice.QuarantinePath);
        return notice.QuarantinePath is null
            ? LF("{0} não pôde ser lido nem copiado ({1}). O RHI não vai salvar por cima dele: corrija ou apague o arquivo para voltar a salvar.",
                "{0} could not be read or copied ({1}). RHI will not save over it: fix or delete the file to save again.", name, notice.FilePath)
            : LF("{0} não pôde ser lido; o conteúdo foi copiado para {1}. O RHI não vai salvar por cima dele: corrija ou apague o arquivo para voltar a salvar.",
                "{0} could not be read; its contents were copied to {1}. RHI will not save over it: fix or delete the file to save again.", name, notice.QuarantinePath);
    }

    /// <summary>Localised status for a store save that failed (refused unreadable file, or an I/O error).</summary>
    public static string StoreSaveFailedText(Exception error) => error switch
    {
        JsonStoreUnavailableException refused when refused.QuarantinePath is not null => LF(
            "Não salvo: {0} não pôde ser lido (há uma cópia em {1}). Corrija ou apague o arquivo para voltar a salvar.",
            "Not saved: {0} could not be read (a copy is at {1}). Fix or delete the file to save again.",
            Path.GetFileName(refused.FilePath), refused.QuarantinePath),
        JsonStoreUnavailableException refused => LF(
            "Não salvo: {0} não pôde ser lido. Corrija ou apague o arquivo para voltar a salvar.",
            "Not saved: {0} could not be read. Fix or delete the file to save again.", refused.FilePath),
        _ => LF("Não foi possível salvar: {0}", "Could not save: {0}", error.Message)
    };

    /// <summary>
    /// Runs one store save. A refused (unreadable) store or an I/O failure is shown on the status
    /// line and returned as the message; null means saved.
    /// </summary>
    private string? TrySaveStore(Action save)
    {
        try
        {
            save();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var message = StoreSaveFailedText(ex);
            StatusText = message;
            return message;
        }
    }

    /// <summary>
    /// github_api.txt could be read by other users (Refs #36): say once whether RHI restricted it
    /// to 0600 or is ignoring the token. Must run on the UI thread.
    /// </summary>
    public void ReportGitHubTokenFileExposed(bool restricted) => StatusText = _statusNotice.Raise(GitHubTokenFileExposedText(restricted), StatusText);

    public static string GitHubTokenFileExposedText(bool restricted) => restricted
        ? L("O github_api.txt podia ser lido por outros usuários deste computador; o RHI restringiu o acesso só à sua conta (chmod 600).",
            "github_api.txt could be read by other users on this computer; RHI restricted it to your account only (chmod 600).")
        : L("O github_api.txt pode ser lido por outros usuários e o RHI não conseguiu restringi-lo; o token não será usado até você executar chmod 600 nele.",
            "github_api.txt can be read by other users and RHI could not restrict it; the token is not used until you run chmod 600 on it.");

    partial void OnStatusTextChanged(string value)
    {
        var decorated = _statusNotice.Decorate(value);
        if (!ReferenceEquals(decorated, value))
            StatusText = decorated;
    }

    /// <summary>
    /// Shows <paramref name="message"/> in the status bar and returns it undecorated, for callers
    /// that also hand it to a panel or dialog (the status bar may append a pending notice).
    /// </summary>
    private string ShowStatus(string message)
    {
        StatusText = message;
        return message;
    }

    partial void OnIsSelectionProbeBusyChanged(bool value)
    {
        _statusNotice.SetSelectionProbeBusy(value);
        OnPropertyChanged(nameof(CanSelectLumaMode));
        NotifyUal();
        NotifyShortFuse();
        NotifyDlssDefaults();
        NotifyDlss();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnLibraryFilterModeChanged(string value)
    {
        if (_libraryState is null) return;
        _libraryState.FilterMode = LinuxGameLibraryStore.NormalizeFilterMode(value);
        if (!string.Equals(value, _libraryState.FilterMode, StringComparison.Ordinal))
            LibraryFilterMode = _libraryState.FilterMode;
        TrySaveStore(() => _libraryStore.Save(_libraryState));
        ApplyFilter();
    }

    partial void OnViewModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsCompactView));
        OnPropertyChanged(nameof(ViewToggleText));
    }

    partial void OnSelectedDlss5FeederConsumerChanged(string value)
    {
        OnPropertyChanged(nameof(IsDeepFriedChickenFeederConsumer));
        OnPropertyChanged(nameof(IsRenoDxDlss5FeederConsumer));
        if (!IsDeepFriedChickenFeederConsumer)
            StatusText = UiLocalizationService.Choose("DLSS5 Feeder: RenoDX DLSS5 selecionado como consumidor padrão. Deep Fried Chicken não será instalado.", "DLSS5 Feeder: RenoDX DLSS5 selected as the default consumer. Deep Fried Chicken will not be installed.");
    }

    partial void OnSelectedDlss5FeederVersionChanged(string value)
        => SaveNeuralVersionPin(ManagedComponentKind.Dlss5Feeder, value);

    partial void OnSelectedDlss5BridgeVersionChanged(string value)
        => SaveNeuralVersionPin(ManagedComponentKind.Dlss5Dx11Bridge, value);

    partial void OnSelectedShortFuseVersionChanged(string value)
        => SaveNeuralVersionPin(ManagedComponentKind.ShortFuse, value);

    partial void OnSelectedNeuralRenderingDllVersionChanged(string value)
        => SaveNeuralRenderingDllPin(value);

    partial void OnSelectedReShadeChannelChanged(string value)
    {
        OnPropertyChanged(nameof(IsReShadeLegacy));
        OnPropertyChanged(nameof(IsReShadeCustom));
    }

    partial void OnSelectedGraphicsApiOverrideChanged(string value)
    {
        if (_rebuildingOptionLists || _loadingGraphicsApiOverride || SelectedGame is null) return;
        var api = ParseGraphicsApiOverride(value);
        var game = SelectedGame;
        // Refused: the dropdown and the game go back to what is saved (the store is the authority).
        if (TrySaveStore(() => _apiOverrideStore.Set(game, api)) is not null)
        {
            LoadGraphicsApiOverride(game);
            return;
        }
        game.UserGraphicsApiOverride = api;
        ReevaluateCurrentGameForApiOverride(game);
    }

    partial void OnSelectedOptiScalerVariantChanged(string value)
    {
        if (_rebuildingOptionLists || _loadingOptiScalerVariant || SelectedGame is null) return;
        var installed = _managedComponents.GetInstalledOptiScalerVariant(SelectedGame);
        if (installed is not null)
        {
            _loadingOptiScalerVariant = true;
            try { SelectedOptiScalerVariant = FormatOptiScalerVariant(installed.Value); }
            finally { _loadingOptiScalerVariant = false; }
            StatusText = L("Remova o OptiScaler atual antes de trocar Estável/Desenvolvimento/DLSS NR.", "Remove the current OptiScaler before switching Stable/Nightly/DLSS NR.");
            return;
        }
        var game = SelectedGame;
        if (TrySaveStore(() => _optiScalerVariantStore.Set(game, ParseOptiScalerVariant(value))) is not null)
        {
            LoadOptiScalerVariant(game);
            return;
        }
        StatusText = LF("OptiScaler: variante {0} selecionada para {1}.", "OptiScaler: variant {0} selected for {1}.", value, SelectedGame.Name);
    }

    partial void OnSelectedLumaModePreferenceChanged(string value)
    {
        if (_rebuildingOptionLists || _loadingLumaModePreference || SelectedGame is null) return;
        var preference = ParseLumaModePreference(value);
        var game = SelectedGame;
        if (TrySaveStore(() => _managedComponents.SetLumaModePreference(game, preference)) is not null)
        {
            LoadLumaModePreference(game);
            return;
        }
        ReevaluateCurrentGameForLumaPolicy(game);
    }

    partial void OnSelectedHdrModeChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedHdrPlan));
        OnPropertyChanged(nameof(HdrProfileHint));
        OnPropertyChanged(nameof(HdrLaunchOptions));
        OnPropertyChanged(nameof(CanCopyHdrLaunchOptions));
        if (_rebuildingOptionLists || _loadingHdrProfile || SelectedGame is null) return;
        SaveHdrProfile(SelectedGame);
    }

    partial void OnHdrLegacyWsiChanged(bool value)
    {
        OnPropertyChanged(nameof(SelectedHdrPlan));
        OnPropertyChanged(nameof(HdrProfileHint));
        OnPropertyChanged(nameof(HdrLaunchOptions));
        OnPropertyChanged(nameof(CanCopyHdrLaunchOptions));
        if (_rebuildingOptionLists || _loadingHdrProfile || SelectedGame is null) return;
        SaveHdrProfile(SelectedGame);
    }

    partial void OnSelectedGameChanged(DetectedGame? value)
    {
        CancelSelectionProbe();
        Components.Clear();
        _dlssDetection = new();
        IsSelectionProbeBusy = false;
        CheckRepairAfterMove(force: false);

        if (value is null)
        {
            ResetGraphicsApiOverride();
            ResetOptiScalerVariant();
            ResetHdrProfile();
            ResetReShadeProfile();
            ResetUalState();
            ResetShortFuseState();
            ResetNeuralVersionPins();
            NotifySelected();
            return;
        }

        LoadGraphicsApiOverride(value);
        LoadOptiScalerVariant(value);
        LoadLumaModePreference(value);
        LoadHdrProfile(value);
        LoadReShadeProfile(value);
        RefreshUalState(value);
        RefreshShortFuseState(value);
        LoadNeuralVersionPins(value);
        _ = RefreshSelectedLatestVersionsAsync(value);

        if (_probeCache.TryGetValue(Key(value), out var cached))
        {
            ApplyProbeResult(cached);
            StatusText = LF("Pronto — {0} (em cache)", "Ready — {0} (cached)", value.Name);
            NotifySelected();
            return;
        }

        IsSelectionProbeBusy = true;
        NotifySelected();
        var generation = ++_selectionGeneration;
        _selectionProbeCts = new CancellationTokenSource();
        _ = AnalyzeSelectedGameAsync(value, generation, _selectionProbeCts.Token);
    }

    private async Task AnalyzeSelectedGameAsync(DetectedGame game, int generation, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            StatusText = LF("Analisando {0} em segundo plano…", "Analyzing {0} in the background…", game.Name);

            if (!game.HasConfirmedPatchTarget)
            {
                var emptyFiles = Array.Empty<string>();
                var components = ComponentProbe.Probe(game, emptyFiles, cancellationToken);
                _managedComponents.Decorate(game, components);
                var pending = new ProbeCacheEntry(emptyFiles, components, new DlssDetectionResult());

                cancellationToken.ThrowIfCancellationRequested();
                if (generation != _selectionGeneration || !ReferenceEquals(SelectedGame, game)) return;

                _probeCache[Key(game)] = pending;
                ApplyProbeResult(pending);
                StatusText = LF("{0}: inicialização preservada, mas o alvo de modificações ainda não foi definido. Use Editar jogo.", "{0}: launch configuration was preserved, but the modification target is not defined yet. Use Edit game.", game.Name);
                return;
            }

            var files = await Task.Run(
                () => GameFileIndexer.Build(game.InstallPath, 8, cancellationToken),
                cancellationToken);

            await _runtimeProbe.ProbeAsync(game, files, cancellationToken);
            await _managedComponents.PrepareGameAsync(game, files, cancellationToken);

            var derived = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var components = ComponentProbe.Probe(game, files, cancellationToken);
                _managedComponents.Decorate(game, components);
                var dlss = game.DlssScanSkippedByManifest
                    ? new DlssDetectionResult()
                    : _dlssDefaults.DetectTargets(game, files, cancellationToken);
                return new ProbeCacheEntry(files, components, dlss);
            }, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _selectionGeneration || !ReferenceEquals(SelectedGame, game)) return;

            _probeCache[Key(game)] = derived;
            ApplyProbeResult(derived);
            timer.Stop();
            StatusText = LF("Pronto — {0}: {1} arquivos indexados em {2} ms", "Ready — {0}: {1} files indexed in {2} ms", game.Name, files.Count, timer.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            // Switching games is expected to cancel the previous probe. Do not surface it as an error.
        }
        catch (Exception ex)
        {
            if (generation == _selectionGeneration && ReferenceEquals(SelectedGame, game))
                StatusText = LF("Falha ao analisar {0}: {1}", "Failed to analyze {0}: {1}", game.Name, ex.Message);
        }
        finally
        {
            if (generation == _selectionGeneration && ReferenceEquals(SelectedGame, game))
            {
                IsSelectionProbeBusy = false;
                NotifySelected();
            }
        }
    }

    private void ApplyProbeResult(ProbeCacheEntry result)
    {
        Components.Clear();
        foreach (var item in result.Components) Components.Add(new ComponentRowViewModel(item, ManageComponentCommand));
        _dlssDetection = result.Dlss;
        if (SelectedGame is { } game)
        {
            RefreshUalState(game);
            RefreshShortFuseState(game);
        }
        NotifySelected();
    }

    private static readonly IReadOnlySet<string> ManualOnly = new HashSet<string>(StringComparer.Ordinal) { "Manual" };
    private static readonly IReadOnlySet<string> NoDetectors = new HashSet<string>(StringComparer.Ordinal);

    [RelayCommand]
    private Task RefreshAsync() => RefreshAsync(null);

    /// <summary>
    /// Full library refresh when <paramref name="onlyDetectors"/> is null; otherwise only the named
    /// detectors are re-run and the rest of the library is reused from the previous scan.
    /// </summary>
    private async Task RefreshAsync(IReadOnlySet<string>? onlyDetectors)
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = onlyDetectors is null
            ? L("Verificando Steam, Heroic, Lutris e prefixos Wine…", "Scanning Steam, Heroic, Lutris and Wine prefixes…")
            : L("Atualizando a biblioteca…", "Updating library…");
        try
        {
            var currentKey = SelectedGame is null ? null : Key(SelectedGame);
            CancelSelectionProbe();
            if (onlyDetectors is null) _probeCache.Clear();
            // Full Refresh bypasses the session NR version caches (upstream 2.7.9).
            Task? neuralCatalogRefresh = null;
            if (onlyDetectors is null)
            {
                _shortFuseLatestVersionCache = null;
                neuralCatalogRefresh = EnsureNeuralVersionCatalogAsync(forceRefresh: true);
            }
            SelectedGame = null;

            var found = await _scanner.ScanAsync(onlyDetectors);
            // Re-key records older builds stored under a Heroic game's folder while the game is still
            // there, so they keep matching after it moves (Refs #38).
            TrySaveStore(() => _deploymentRecords.MigrateGameKeys(found));
            _libraryState = _libraryStore.Load();
            LibraryFilterMode = _libraryState.FilterMode;

            // Partial refresh: the scanner hands back fresh detector-level instances, but the
            // runtime probe (binary directory, graphics API, engine hint) only ever ran on the old
            // instances. Keep any old instance that still has a valid probe cache entry so a cache
            // hit is never applied to an un-probed game. Everything else takes the normal path.
            var previous = onlyDetectors is null
                ? null
                : _allGames.Where(g => _probeCache.ContainsKey(Key(g))).ToDictionary(Key, StringComparer.Ordinal);
            var merged = new List<DetectedGame>(found.Count);
            foreach (var game in found)
            {
                game.LibraryIdentityPath ??= game.InstallPath;
                var libraryKey = LinuxGameLibraryStore.GameKey(game);
                if (previous is not null && previous.TryGetValue(libraryKey, out var kept))
                {
                    kept.IsFavourite = _libraryState.FavouriteGames.Contains(libraryKey);
                    kept.IsHidden = _libraryState.HiddenGames.Contains(libraryKey);
                    merged.Add(kept);
                    continue;
                }
                _probeCache.Remove(libraryKey);
                _libraryState.GameEdits.TryGetValue(libraryKey, out var edit);
                LinuxGameIdentityService.ApplyDetectedIdentity(game, edit);
                game.IsFavourite = _libraryState.FavouriteGames.Contains(libraryKey);
                game.IsHidden = _libraryState.HiddenGames.Contains(libraryKey);
                merged.Add(game);
            }
            _allGames.Clear();
            _allGames.AddRange(merged);
            ApplyFilter();
            SelectedGame = currentKey is null
                ? Games.FirstOrDefault()
                : Games.FirstOrDefault(g => Key(g) == currentKey) ?? Games.FirstOrDefault();
            CheckRepairAfterMove(force: true); // the scan may have found the game in a new folder
            if (neuralCatalogRefresh is not null) await neuralCatalogRefresh;
            StatusText = LF("Verificação concluída — {0} jogos detectados", "Scan complete — {0} games detected", _allGames.Count);
        }
        catch (Exception ex)
        {
            StatusText = LF("Falha na verificação: {0}", "Scan failed: {0}", ex.Message);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanUpdateAll));
            UpdateAllCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanUpdateAll))]
    private async Task UpdateAllAsync()
    {
        if (!CanUpdateAll) return;
        var games = _allGames.ToArray();
        IsBusy = true;
        try
        {
            StatusText = L("Atualizar tudo: preparando jogos gerenciados pelo RHI…", "Update all: preparing RHI-managed games…");
            var indexes = await PrepareUpdateIndexesAsync(games, CancellationToken.None);
            var result = await _updateAll.RunAsync(
                games,
                game => indexes.TryGetValue(Key(game), out var files) ? files : Array.Empty<string>(),
                LinuxUpdateRunMode.Manual);
            _probeCache.Clear();
            if (SelectedGame is { } selected && result.ChangedCount > 0)
                await ReprobeGameAsync(selected);
            StatusText = UiLocalizationService.UpdateAllSummary(result);
        }
        catch (Exception ex)
        {
            StatusText = LF("Falha em Atualizar tudo: {0}", "Update all failed: {0}", ex.Message);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanUpdateAll));
            UpdateAllCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task<Dictionary<string, IReadOnlyList<string>>> PrepareUpdateIndexesAsync(
        IReadOnlyList<DetectedGame> games,
        CancellationToken cancellationToken)
    {
        var indexes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var game in games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!game.HasConfirmedPatchTarget || !_updateAll.HasManagedTargets([game])) continue;
            var key = Key(game);
            if (_probeCache.TryGetValue(key, out var cached))
            {
                indexes[key] = cached.Files;
                continue;
            }
            if (string.IsNullOrWhiteSpace(game.InstallPath) || !Directory.Exists(game.InstallPath))
            {
                indexes[key] = Array.Empty<string>();
                continue;
            }
            indexes[key] = await Task.Run(() => GameFileIndexer.Build(game.InstallPath, 8, cancellationToken), cancellationToken);
        }
        return indexes;
    }

    private void RestartBackgroundUpdateLoop(bool runImmediate)
    {
        try { _backgroundUpdateCts?.Cancel(); } catch { }
        _backgroundUpdateCts?.Dispose();
        _backgroundUpdateCts = null;
        if (!_settings.AutoUpdateComponents) return;
        _backgroundUpdateCts = new CancellationTokenSource();
        _ = BackgroundUpdateLoopAsync(_backgroundUpdateCts.Token, runImmediate);
    }

    private async Task BackgroundUpdateLoopAsync(CancellationToken cancellationToken, bool runImmediate)
    {
        try
        {
            if (runImmediate)
                await RunBackgroundUpdatePassAsync(cancellationToken);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(_settings.UpdateCheckIntervalHours));
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await RunBackgroundUpdatePassAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            StatusText = LF("A atualização em segundo plano foi interrompida: {0}", "Background update was interrupted: {0}", ex.Message);
        }
    }

    private async Task RunBackgroundUpdatePassAsync(CancellationToken cancellationToken)
    {
        if (!_settings.AutoUpdateComponents || IsBusy) return;
        var games = _allGames.ToArray();
        if (!_updateAll.HasManagedTargets(games)) return;
        IsBusy = true;
        try
        {
            var indexes = await PrepareUpdateIndexesAsync(games, cancellationToken);
            var result = await _updateAll.RunAsync(
                games,
                game => indexes.TryGetValue(Key(game), out var files) ? files : Array.Empty<string>(),
                LinuxUpdateRunMode.Background,
                cancellationToken);
            if (result.ChangedCount > 0)
            {
                _probeCache.Clear();
                StatusText = UiLocalizationService.UpdateAllSummary(result) + L(" (segundo plano)", " (background)");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task LaunchAsync()
    {
        if (SelectedGame is null) return;
        var plan = SelectedHdrPlan;
        if (plan.Enabled && !plan.CanApply)
        {
            StatusText = LF("Inicialização HDR bloqueada: {0}", "HDR launch blocked: {0}", plan.Summary);
            return;
        }

        if (plan.Enabled)
        {
            if (LinuxHdrLaunchProfileService.IsLauncherMediated(SelectedGame))
            {
                StatusText = L("O perfil HDR está pronto, mas este jogo é iniciado pelo Steam/Heroic/Lutris. Aplique o perfil gerado nesse inicializador; o RHI não envolverá o processo do launcher nem alegará que o HDR foi aplicado.", "The HDR profile is ready, but this game is launched by Steam/Heroic/Lutris. Apply the generated profile in that launcher; RHI will not wrap the launcher process or claim HDR was applied.");
                return;
            }
            if (_launcher is not IGameLaunchOptionsLauncher profiledLauncher)
            {
                StatusText = L("O perfil de inicialização HDR está ativado, mas este launcher não consegue aplicar opções de inicialização do Linux.", "The HDR launch profile is enabled, but this launcher cannot apply Linux launch options.");
                return;
            }
            await profiledLauncher.LaunchAsync(SelectedGame, new GameLaunchOptions(plan.Environment, plan.CommandPrefix));
            StatusText = LF("Iniciando {0} com perfil HDR {1}", "Launching {0} with HDR profile {1}", SelectedGame.Name, SelectedHdrMode);
            return;
        }

        await _launcher.LaunchAsync(SelectedGame);
        StatusText = SelectedGame.ParentLauncherName is { Length: > 0 } parent
            ? LF("Abrindo {0}. Inicie {1} a partir do launcher.", "Opening {0}. Start {1} from the launcher.", parent, SelectedGame.Name)
            : LF("Iniciando {0}", "Launching {0}", SelectedGame.Name);
    }


    public async Task<RenoDxCompatibilitySnapshot> GetRenoDxCompatibilitySettingsAsync(
        CancellationToken cancellationToken = default)
    {
        if (SelectedGame is null)
            return new(null, false, Array.Empty<RenoDxCompatibilitySetting>(), L("Selecione um jogo primeiro.", "Select a game first."));
        if (!SelectedGame.HasConfirmedPatchTarget)
            return new(null, false, Array.Empty<RenoDxCompatibilitySetting>(), L("Defina o executável alvo em Editar jogo primeiro.", "Set the target executable in Edit game first."));

        try
        {
            var snapshot = await _managedComponents
                .GetRenoDxCompatibilitySettingsAsync(SelectedGame, cancellationToken)
                .ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(snapshot.Warning))
                StatusText = UiLocalizationService.Present(snapshot.Warning);
            return snapshot;
        }
        catch (Exception ex)
        {
            return new(null, false, Array.Empty<RenoDxCompatibilitySetting>(), ShowStatus(LF("Configurações do RenoDX: {0}", "RenoDX settings: {0}", ex.Message)));
        }
    }

    public ComponentOperationResult SetRenoDxCompatibilitySetting(string key, string value)
    {
        if (SelectedGame is null)
            return ComponentOperationResult.Fail(L("Selecione um jogo primeiro.", "Select a game first."));
        var result = _managedComponents.SetRenoDxCompatibilitySetting(SelectedGame, key, value);
        StatusText = UiLocalizationService.Present(result.Message);
        return result;
    }

    public OptiScalerAdvancedSnapshot GetOptiScalerAdvancedSettings()
    {
        if (SelectedGame is null)
            return new(null, false, Array.Empty<OptiScalerAdvancedSetting>(), L("Selecione um jogo primeiro.", "Select a game first."));
        if (!SelectedGame.HasConfirmedPatchTarget)
            return new(null, false, Array.Empty<OptiScalerAdvancedSetting>(), L("Defina o executável alvo em Editar jogo primeiro.", "Set the target executable in Edit game first."));
        try
        {
            var snapshot = _managedComponents.GetOptiScalerAdvancedSettings(SelectedGame);
            if (!string.IsNullOrWhiteSpace(snapshot.Warning))
                StatusText = UiLocalizationService.Present(snapshot.Warning);
            return snapshot;
        }
        catch (Exception ex)
        {
            return new(null, false, Array.Empty<OptiScalerAdvancedSetting>(), ShowStatus(LF("Configurações do OptiScaler: {0}", "OptiScaler settings: {0}", ex.Message)));
        }
    }

    public ComponentOperationResult SetOptiScalerAdvancedSetting(string id, string value)
    {
        if (SelectedGame is null)
            return ComponentOperationResult.Fail(L("Selecione um jogo primeiro.", "Select a game first."));
        var result = _managedComponents.SetOptiScalerAdvancedSetting(SelectedGame, id, value);
        StatusText = UiLocalizationService.Present(result.Message);
        return result;
    }

    public async Task<ComponentOperationResult> SetOptiScalerAdvancedSettingAsync(
        string id,
        string value,
        CancellationToken cancellationToken = default)
    {
        if (SelectedGame is null)
            return ComponentOperationResult.Fail(L("Selecione um jogo primeiro.", "Select a game first."));
        var game = SelectedGame;
        var result = await _managedComponents
            .SetOptiScalerAdvancedSettingAsync(game, id, value, cancellationToken)
            .ConfigureAwait(true);
        if (result.Success)
            ReevaluateCurrentGameComponents(game);
        StatusText = UiLocalizationService.Present(result.Message);
        return result;
    }

    public Dlssg2030Preferences GetDlssg2030Preferences()
        => SelectedGame is null ? new Dlssg2030Preferences() : _managedComponents.GetDlssg2030Preferences(SelectedGame);

    public LinuxDeploymentRecord? GetDlssg2030Record()
        => SelectedGame is null ? null : _managedComponents.GetDlssg2030Record(SelectedGame);

    public ComponentOperationResult SetDlssg2030Preferences(string gpuGeneration, string proxyName)
    {
        if (SelectedGame is null)
            return ComponentOperationResult.Fail(L("Selecione um jogo primeiro.", "Select a game first."));
        var result = _managedComponents.SetDlssg2030Preferences(SelectedGame, gpuGeneration, proxyName);
        StatusText = UiLocalizationService.Present(result.Message);
        return result;
    }

    public async Task<Dlssg2030UpdateStatus> CheckDlssg2030UpdateAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedGame is null)
            return new(false, null, null, false, L("Selecione um jogo primeiro.", "Select a game first."));
        var status = await _managedComponents.CheckDlssg2030UpdateAsync(SelectedGame, cancellationToken).ConfigureAwait(true);
        StatusText = UiLocalizationService.Present(status.Message);
        return status;
    }

    public async Task<ComponentOperationResult> UpdateDlssg2030Async(CancellationToken cancellationToken = default)
    {
        if (SelectedGame is null)
            return ComponentOperationResult.Fail(L("Selecione um jogo primeiro.", "Select a game first."));
        var game = SelectedGame;
        var result = await _managedComponents.UpdateDlssg2030Async(game, cancellationToken).ConfigureAwait(true);
        StatusText = UiLocalizationService.Present(result.Message);
        if (result.Success) await ReprobeGameAsync(game);
        return result;
    }

    public StandaloneDlssEnablerPreferences GetStandaloneDlssEnablerPreferences()
        => SelectedGame is null ? new StandaloneDlssEnablerPreferences() : _managedComponents.GetStandaloneDlssEnablerPreferences(SelectedGame);

    public LinuxDeploymentRecord? GetStandaloneDlssEnablerRecord()
        => SelectedGame is null ? null : _managedComponents.GetStandaloneDlssEnablerRecord(SelectedGame);

    public ComponentOperationResult SetStandaloneDlssEnablerPreferences(string proxyName)
    {
        if (SelectedGame is null)
            return ComponentOperationResult.Fail(L("Selecione um jogo primeiro.", "Select a game first."));
        var result = _managedComponents.SetStandaloneDlssEnablerPreferences(SelectedGame, proxyName);
        StatusText = UiLocalizationService.Present(result.Message);
        return result;
    }

    public async Task<DlssEnablerUpdateStatus> CheckStandaloneDlssEnablerUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedGame is null)
            return new(false, null, null, false, L("Selecione um jogo primeiro.", "Select a game first."));
        var status = await _managedComponents.CheckStandaloneDlssEnablerUpdateAsync(SelectedGame, cancellationToken).ConfigureAwait(true);
        StatusText = UiLocalizationService.Present(status.Message);
        return status;
    }

    public async Task<ComponentOperationResult> UpdateStandaloneDlssEnablerAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedGame is null)
            return ComponentOperationResult.Fail(L("Selecione um jogo primeiro.", "Select a game first."));
        var game = SelectedGame;
        var result = await _managedComponents.UpdateStandaloneDlssEnablerAsync(game, cancellationToken).ConfigureAwait(true);
        StatusText = UiLocalizationService.Present(result.Message);
        if (result.Success) await ReprobeGameAsync(game);
        return result;
    }

    public Rtx40MfgPreferences GetRtx40MfgPreferences()
        => SelectedGame is null ? new Rtx40MfgPreferences() : _managedComponents.GetRtx40MfgPreferences(SelectedGame);

    public LinuxDeploymentRecord? GetRtx40MfgRecord()
        => SelectedGame is null ? null : _managedComponents.GetRtx40MfgRecord(SelectedGame);

    public ComponentOperationResult SetRtx40MfgPreferences(string proxyName)
    {
        if (SelectedGame is null)
            return ComponentOperationResult.Fail(L("Selecione um jogo primeiro.", "Select a game first."));
        var result = _managedComponents.SetRtx40MfgPreferences(SelectedGame, proxyName);
        StatusText = UiLocalizationService.Present(result.Message);
        return result;
    }

    public ComponentOperationResult SetRtx40MfgPreferences(string gpuGeneration, string proxyName)
    {
        if (SelectedGame is null)
            return ComponentOperationResult.Fail(L("Selecione um jogo primeiro.", "Select a game first."));
        var result = _managedComponents.SetRtx40MfgPreferences(SelectedGame, gpuGeneration, proxyName);
        StatusText = UiLocalizationService.Present(result.Message);
        return result;
    }

    public async Task<Rtx40MfgUpdateStatus> CheckRtx40MfgUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedGame is null)
            return new(false, null, null, false, L("Selecione um jogo primeiro.", "Select a game first."));
        var status = await _managedComponents.CheckRtx40MfgUpdateAsync(SelectedGame, cancellationToken).ConfigureAwait(true);
        StatusText = UiLocalizationService.Present(status.Message);
        return status;
    }

    public async Task<ComponentOperationResult> UpdateRtx40MfgAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedGame is null)
            return ComponentOperationResult.Fail(L("Selecione um jogo primeiro.", "Select a game first."));
        var game = SelectedGame;
        var result = await _managedComponents.UpdateRtx40MfgAsync(game, cancellationToken).ConfigureAwait(true);
        StatusText = UiLocalizationService.Present(result.Message);
        if (result.Success) await ReprobeGameAsync(game);
        return result;
    }

    /// <summary>Set by the window: shows a confirmation (title, message, accept text) and returns true to proceed.</summary>
    public Func<string, string, string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>
    /// Set by the window: shows the manifest install warnings (title, one section per component with its links, accept
    /// text) in one dialog; true to proceed.
    /// </summary>
    public Func<string, IReadOnlyList<InstallWarningSection>, string, Task<bool>>? ConfirmInstallWarningAsync { get; set; }

    /// <summary>Dialog sections for <paramref name="warnings"/>; a lone warning for the requested component has no heading, as upstream.</summary>
    public static IReadOnlyList<InstallWarningSection> InstallWarningSections(IReadOnlyList<ComponentInstallWarning> warnings, string targetName)
    {
        if (warnings is [{ AddedAutomatically: false } only]) return [new InstallWarningSection(null, only.Warning)];
        return warnings.Select(warning => new InstallWarningSection(
            warning.AddedAutomatically
                ? LF("{0} — instalado automaticamente com {1}", "{0} — installed automatically with {1}",
                    LinuxManagedComponentService.DisplayDependencyName(warning.Kind), targetName)
                : targetName,
            warning.Warning)).ToList();
    }

    // Upstream 2.8.0: user-initiated installs show the manifest's per-game warning first. On Linux an install can also add
    // ReShade (add-ons, Luma, ShortFuse) or RE Framework (ReShade on RE Engine) automatically; their warnings are shown in
    // the same dialog. Update All and the background pass go through LinuxUpdateAllService and never reach this prompt.
    private async Task<bool> AcceptManifestInstallWarningsAsync(
        DetectedGame game,
        ManagedComponentKind target,
        string displayName,
        bool installsTarget,
        IReadOnlyList<string>? indexedFiles)
    {
        if (ConfirmInstallWarningAsync is not { } confirm) return true;
        // The dependency plan may index the game folder (RE Framework check on RE Engine games): keep it off the UI thread.
        var pending = await Task.Run(() => _managedComponents.PendingAutoDependencies(game, target, indexedFiles));
        var warnings = ManifestInstallWarning.ForInstall(game, target, installsTarget, pending);
        if (warnings.Count == 0) return true;
        if (await confirm(LF("⚠ Nota de instalação — {0}", "⚠ Install note — {0}", game.Name), InstallWarningSections(warnings, displayName), L("Continuar", "Continue")))
            return true;
        StatusText = LF("Instalação de {0} cancelada.", "{0} installation cancelled.", displayName);
        return false;
    }

    private IReadOnlyList<string>? ProbedFiles(DetectedGame game)
        => _probeCache.TryGetValue(Key(game), out var probe) ? probe.Files : null;

    [RelayCommand]
    private async Task ManageComponentAsync(ManagedComponentKind kind)
    {
        if (SelectedGame is null || IsBusy || IsSelectionProbeBusy) return;
        var game = SelectedGame;
        if (!_probeCache.TryGetValue(Key(game), out var probe))
        {
            StatusText = L("Aguarde a análise do jogo terminar antes de alterar componentes.", "Wait for game analysis to finish before changing components.");
            return;
        }

        var component = probe.Components.FirstOrDefault(c => c.Kind == kind);
        if (component is null || !component.ActionAllowed)
        {
            StatusText = LF("{0}: a ação está bloqueada para proteger esta instalação.", "{0}: the action is blocked to protect this installation.", kind);
            return;
        }

        IsBusy = true;
        try
        {
            var removing = component.Availability is ComponentAvailability.Installed or ComponentAvailability.UpdateAvailable;
            if (!removing && kind == ManagedComponentKind.Dlss5Feeder && IsDeepFriedChickenFeederConsumer && string.IsNullOrWhiteSpace(Dlss5FeederConsumerPackagePath))
            {
                StatusText = L("DLSS5 Feeder Full com Deep Fried Chicken exige o pacote oficial (.zip ou .7z). Selecione-o primeiro em Perfil do ReShade → Consumidor do Feeder.", "DLSS5 Feeder Full with Deep Fried Chicken requires the official package (.zip or .7z). Select it first under ReShade Profile → Feeder consumer.");
                return;
            }

            if (!removing && !await AcceptManifestInstallWarningsAsync(game, kind, component.DisplayName, installsTarget: true, probe.Files)) return;

            // Upstream 2.7.9: Control Ultimate Edition's RenoDX mod is an RR fix with extra steps; confirm first.
            if (!removing && kind == ManagedComponentKind.RenoDX && ControlUePostInstallService.IsControlUltimateEdition(game) &&
                ConfirmAsync is { } confirm &&
                !await confirm(L("Control Ultimate Edition — mod RenoDX", "Control Ultimate Edition — RenoDX mod"), L(
                    "⚠ Este NÃO é um mod HDR.\n\n" +
                    "Ele corrige o ruído do RT com Ray Reconstruction. Duas estratégias (escolha uma; são mutuamente exclusivas):\n" +
                    "• Desative o denoiser de RT do jogo e use o preset M ou L do DLSS Super Resolution\n" +
                    "• Use Ray Reconstruction com entradas extras derivadas dos shaders do jogo (o denoiser do jogo também fica desativado — o RR exige isso)\n\n" +
                    "DLSS, Ray Tracing e SSAO precisam estar ativados no jogo para o Ray Reconstruction funcionar corretamente.\n\n" +
                    "Instalar também vai:\n" +
                    "• Atualizar nvngx_dlss.dll para a versão mais recente disponível\n" +
                    "• Instalar nvngx_dlssd.dll (runtime do DLSS Ray Reconstruction)\n" +
                    "• Definir o preset HDR do renderer.ini (m_eHDRPreset) como 2\n\n" +
                    "O passo do preset SR no perfil do driver NVIDIA é ignorado no Linux. Remover o RenoDX restaura as DLLs e o renderer.ini anteriores.",
                    "⚠ This is NOT an HDR mod.\n\n" +
                    "It fixes RT noise using Ray Reconstruction. Two strategies (pick one; they are mutually exclusive):\n" +
                    "• Turn off the in-game RT denoiser and use DLSS Super Resolution preset M or L\n" +
                    "• Use Ray Reconstruction with extra inputs derived from the game's shaders (the game denoiser is turned off here too — RR needs that)\n\n" +
                    "DLSS, Ray Tracing and SSAO must all be enabled in-game for Ray Reconstruction to work correctly.\n\n" +
                    "Installing will also:\n" +
                    "• Upgrade nvngx_dlss.dll to the newest available version\n" +
                    "• Deploy nvngx_dlssd.dll (DLSS Ray Reconstruction runtime)\n" +
                    "• Set the renderer.ini HDR preset (m_eHDRPreset) to 2\n\n" +
                    "The NVIDIA driver-profile SR preset step is skipped on Linux. Removing RenoDX restores the previous DLLs and renderer.ini."),
                    L("Instalar", "Install")))
            {
                StatusText = L("Instalação do RenoDX cancelada.", "RenoDX installation cancelled.");
                return;
            }

            StatusText = removing
                ? LF("Removendo {0} gerenciado pelo RHI…", "Removing RHI-managed {0}…", component.DisplayName)
                : LF("Instalando {0}…", "Installing {0}…", component.DisplayName);
            var result = removing
                ? await _managedComponents.RemoveAsync(game, kind)
                : kind == ManagedComponentKind.Dlss5Feeder
                    ? await _managedComponents.InstallDlss5FeederAsync(
                        game,
                        IsDeepFriedChickenFeederConsumer ? Dlss5FeederConsumer.DeepFriedChicken : Dlss5FeederConsumer.RenoDxDlss5,
                        IsDeepFriedChickenFeederConsumer ? Dlss5FeederConsumerPackagePath : null,
                        ReShadeD3DCompiler47Path)
                    : kind == ManagedComponentKind.OptiScaler
                        ? await _managedComponents.InstallOptiScalerAsync(game, ParseOptiScalerVariant(SelectedOptiScalerVariant))
                        : await _managedComponents.InstallAsync(game, kind, probe.Files);
            StatusText = UiLocalizationService.Present(result.Message);
            if (result.Success)
                await ReprobeGameAsync(game);
        }
        catch (Exception ex)
        {
            StatusText = LF("{0}: {1}", "{0}: {1}", component.DisplayName, ex.Message);
        }
        finally { IsBusy = false; }
    }

    private async Task ReprobeGameAsync(DetectedGame game)
    {
        _probeCache.Remove(Key(game));
        CancelSelectionProbe();
        Components.Clear();
        _dlssDetection = new();
        IsSelectionProbeBusy = true;
        NotifySelected();
        var generation = ++_selectionGeneration;
        _selectionProbeCts = new CancellationTokenSource();
        await AnalyzeSelectedGameAsync(game, generation, _selectionProbeCts.Token);
        LoadReShadeProfile(game);
    }

    [RelayCommand]
    private async Task CheckUalUpdateAsync()
    {
        if (SelectedGame is null || IsBusy || IsSelectionProbeBusy) return;
        if (!SelectedGame.HasConfirmedPatchTarget)
        {
            StatusText = L("Defina o executável alvo em Editar jogo antes de verificar o Ultimate ASI Loader.", "Set the target executable in Edit game before checking Ultimate ASI Loader.");
            return;
        }
        IsBusy = true;
        NotifyUalCommands();
        try
        {
            StatusText = L("Atualizando versão mais recente do Ultimate ASI Loader…", "Refreshing latest Ultimate ASI Loader version…");
            var game = SelectedGame;
            var status = await _managedComponents.CheckUltimateAsiLoaderUpdateAsync(game);
            if (status.Success && !string.IsNullOrWhiteSpace(status.LatestVersion) && GetUalArchitecture(game) is { } architecture)
                _ualLatestVersionCache[architecture] = status.LatestVersion;
            UalLatestVersionText = status.Success
                ? FormatLatestVersionSummary(_managedComponents.GetUltimateAsiLoaderRecord(game)?.Version, status.LatestVersion)
                : LF("Versão mais recente indisponível: {0}", "Latest version unavailable: {0}", UiLocalizationService.Present(status.Message));
            StatusText = UiLocalizationService.Present(status.Message);
        }
        catch (Exception ex)
        {
            UalLatestVersionText = LF("Falha ao verificar atualização: {0}", "Update check failed: {0}", ex.Message);
            StatusText = UalLatestVersionText;
        }
        finally
        {
            IsBusy = false;
            NotifyUal();
        }
    }

    [RelayCommand]
    private async Task InstallOrUpdateUalAsync()
    {
        if (SelectedGame is null || IsBusy || IsSelectionProbeBusy) return;
        var game = SelectedGame;
        IsBusy = true;
        NotifyUalCommands();
        try
        {
            ComponentOperationResult result;
            if (_managedComponents.GetUltimateAsiLoaderRecord(game) is not null)
            {
                StatusText = L("Atualizando Ultimate ASI Loader…", "Updating Ultimate ASI Loader…");
                result = await _managedComponents.UpdateUltimateAsiLoaderAsync(game);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(SelectedUalProxyName))
                {
                    StatusText = L("Escolha primeiro um nome de proxy para o Ultimate ASI Loader.", "Choose a proxy name for Ultimate ASI Loader first.");
                    return;
                }
                StatusText = LF("Instalando Ultimate ASI Loader como {0}…", "Installing Ultimate ASI Loader as {0}…", SelectedUalProxyName);
                result = await _managedComponents.InstallUltimateAsiLoaderAsync(game, SelectedUalProxyName);
            }

            StatusText = UiLocalizationService.Present(result.Message);
            if (result.Success)
            {
                RefreshUalState(game);
                var update = await _managedComponents.CheckUltimateAsiLoaderUpdateAsync(game);
                if (update.Success && !string.IsNullOrWhiteSpace(update.LatestVersion) && GetUalArchitecture(game) is { } architecture)
                    _ualLatestVersionCache[architecture] = update.LatestVersion;
                UalLatestVersionText = update.Success
                    ? FormatLatestVersionSummary(_managedComponents.GetUltimateAsiLoaderRecord(game)?.Version, update.LatestVersion)
                    : UalLatestVersionText;
            }
        }
        catch (Exception ex)
        {
            StatusText = LF("Ultimate ASI Loader: {0}", "Ultimate ASI Loader: {0}", ex.Message);
        }
        finally
        {
            IsBusy = false;
            NotifyUal();
        }
    }

    [RelayCommand]
    private async Task RemoveUalAsync()
    {
        if (SelectedGame is null || IsBusy || IsSelectionProbeBusy) return;
        var game = SelectedGame;
        IsBusy = true;
        NotifyUalCommands();
        try
        {
            StatusText = L("Removendo Ultimate ASI Loader e restaurando DLL encadeada / override do Wine…", "Removing Ultimate ASI Loader and restoring chained DLL / Wine override…");
            var result = await _managedComponents.RemoveUltimateAsiLoaderAsync(game);
            StatusText = UiLocalizationService.Present(result.Message);
            if (result.Success)
            {
                RefreshUalState(game);
                UalLatestVersionText = UiLocalizationService.Text("Ui.NotCheckedYet");
            }
        }
        catch (Exception ex)
        {
            StatusText = LF("Falha ao remover Ultimate ASI Loader: {0}", "Ultimate ASI Loader removal failed: {0}", ex.Message);
        }
        finally
        {
            IsBusy = false;
            NotifyUal();
        }
    }

    [RelayCommand]
    private async Task CheckShortFuseUpdateAsync()
    {
        if (SelectedGame is null || IsBusy || IsSelectionProbeBusy) return;
        if (!SelectedGame.HasConfirmedPatchTarget)
        {
            StatusText = L("Defina o executável alvo em Editar jogo antes de verificar o ShortFuse.", "Set the target executable in Edit game before checking ShortFuse.");
            return;
        }
        IsBusy = true;
        try
        {
            var game = SelectedGame;
            var status = await _managedComponents.CheckShortFuseLatestReleaseAsync(game);
            if (status.Success && !string.IsNullOrWhiteSpace(status.LatestVersion))
                _shortFuseLatestVersionCache = status.LatestVersion;
            ShortFuseLatestVersionText = status.Success
                ? FormatLatestVersionSummary(_managedComponents.GetShortFuseRecord(game)?.Version, status.LatestVersion)
                : LF("Versão mais recente indisponível: {0}", "Latest version unavailable: {0}", UiLocalizationService.Present(status.Message));
            StatusText = UiLocalizationService.Present(status.Message);
        }
        catch (Exception ex) { ShortFuseLatestVersionText = LF("Falha ao verificar atualização do ShortFuse: {0}", "ShortFuse update check failed: {0}", ex.Message); StatusText = ShortFuseLatestVersionText; }
        finally { IsBusy = false; NotifyShortFuse(); }
    }

    [RelayCommand]
    private async Task InstallOrRemoveShortFuseAsync()
    {
        if (SelectedGame is null || IsBusy || IsSelectionProbeBusy) return;
        var game = SelectedGame;
        IsBusy = true;
        try
        {
            var removing = _managedComponents.IsShortFuseManaged(game);
            // ShortFuse has no manifest key of its own, but installing it adds RHI-owned ReShade when absent.
            if (!removing && !await AcceptManifestInstallWarningsAsync(game, ManagedComponentKind.ShortFuse, "ShortFuse", installsTarget: true, ProbedFiles(game))) return;
            StatusText = removing
                ? L("Removendo ShortFuse e restaurando a pilha anterior…", "Removing ShortFuse and restoring the previous stack…")
                : L("Instalando a orquestração de Geração de Quadros DLSS do ShortFuse…", "Installing ShortFuse DLSS Frame Generation orchestration…");
            var result = removing
                ? await _managedComponents.RemoveShortFuseAsync(game)
                : await _managedComponents.InstallShortFuseAsync(game, ShortFuseAutoConfigEnabled);
            StatusText = UiLocalizationService.Present(result.Message);
            if (result.Success)
            {
                await ReprobeGameAsync(game);
                RefreshShortFuseState(game);
            }
        }
        catch (Exception ex) { StatusText = LF("ShortFuse: {0}", "ShortFuse: {0}", ex.Message); }
        finally { IsBusy = false; NotifyShortFuse(); NotifyUal(); NotifyDlss(); }
    }

    [RelayCommand]
    private async Task ApplyReShadeProfileAsync()
    {
        if (SelectedGame is null || IsBusy || IsSelectionProbeBusy) return;
        var game = SelectedGame;
        if (!_probeCache.TryGetValue(Key(game), out var probe))
        {
            StatusText = L("Aguarde a análise do jogo terminar antes de alterar o ReShade.", "Wait for game analysis to finish before changing ReShade.");
            return;
        }

        var component = probe.Components.FirstOrDefault(c => c.Kind == ManagedComponentKind.ReShade);
        if (component is null)
        {
            StatusText = L("O estado do componente ReShade está indisponível para este jogo.", "ReShade component state is unavailable for this game.");
            return;
        }
        if ((component.Availability is ComponentAvailability.Installed or ComponentAvailability.UpdateAvailable) && !component.ManagedByRhi)
        {
            StatusText = L("ReShade externo detectado. O RHI não irá sobrescrevê-lo nem adotá-lo.", "External ReShade detected. RHI will not overwrite or adopt it.");
            return;
        }
        if (component.Availability == ComponentAvailability.Unsupported)
        {
            var note = UiLocalizationService.Present(component.Note);
            StatusText = string.IsNullOrWhiteSpace(note)
                ? L("ReShade não é compatível com este alvo.", "ReShade is not compatible with this target.")
                : note;
            return;
        }

        if (!Enum.TryParse<ReShadeChannel>(ReShadeChannelValue(SelectedReShadeChannel), true, out var channel))
        {
            StatusText = L("Canal do ReShade desconhecido.", "Unknown ReShade channel.");
            return;
        }

        var packSet = new HashSet<string>(_selectedShaderPackIds, StringComparer.OrdinalIgnoreCase);
        SetPack(packSet, "CrosireMaster", ShaderCrosireMaster);
        SetPack(packSet, "CrosireLegacy", ShaderCrosireLegacy);
        SetPack(packSet, "ClshortfuseShaders", ShaderClshortfuse);
        SetPack(packSet, "MaxG2DSimpleHDR", ShaderMaxG2D);
        var packs = packSet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var options = new ReShadeInstallOptions
        {
            Channel = channel,
            LegacyVersion = ReShadeLegacyVersion,
            CustomDllPath = ReShadeCustomDllPath,
            D3DCompiler47Path = ReShadeD3DCompiler47Path,
            ManageIni = ReShadeManageIni,
            ShaderPackIds = packs
        };

        IsBusy = true;
        try
        {
            // Applying a profile installs ReShade when it is not there yet (Available or Unknown), and RE Framework on an
            // RE Engine game when that is missing; those are the installs the manifest warns about.
            if (!await AcceptManifestInstallWarningsAsync(game, ManagedComponentKind.ReShade, component.DisplayName,
                    installsTarget: ManifestInstallWarning.IsFirstInstall(component.Availability), probe.Files)) return;
            StatusText = LF("Aplicando perfil {0} do ReShade…", "Applying ReShade {0} profile…", channel);
            var result = await _managedComponents.ApplyReShadeAsync(game, options);
            StatusText = UiLocalizationService.Present(result.Message);
            ReShadeUpdateText = UiLocalizationService.Present(result.Message);
            if (result.Success)
            {
                _selectedShaderPackIds.Clear();
                _selectedShaderPackIds.UnionWith(options.ShaderPackIds);
                await ReprobeGameAsync(game);
            }
        }
        catch (Exception ex)
        {
            ReShadeUpdateText = ShowStatus(LF("Falha no perfil do ReShade: {0}", "ReShade profile failed: {0}", ex.Message));
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task InstallD3DCompilerWithProtontricksAsync()
    {
        if (SelectedGame is null || IsBusy || !CanInstallD3DCompilerWithProtontricks) return;
        var game = SelectedGame;
        IsBusy = true;
        try
        {
            StatusText = LF("Instalando D3DCompiler_47 nativo para {0} com Protontricks…", "Installing native D3DCompiler_47 for {0} with Protontricks…", game.Name);
            var result = await _managedComponents.InstallD3DCompilerWithProtontricksAsync(game);
            StatusText = UiLocalizationService.Present(result.Message);
            ReShadeUpdateText = UiLocalizationService.Present(result.Message);
        }
        catch (Exception ex)
        {
            ReShadeUpdateText = ShowStatus(LF("Falha ao instalar D3DCompiler com Protontricks: {0}", "Failed to install D3DCompiler with Protontricks: {0}", ex.Message));
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task InstallD3DCompilerWithWinetricksAsync()
    {
        if (SelectedGame is null || IsBusy || !CanInstallD3DCompilerWithWinetricks) return;
        var game = SelectedGame;
        IsBusy = true;
        try
        {
            StatusText = LF("Instalando D3DCompiler_47 nativo para {0} com {1}…", "Installing native D3DCompiler_47 for {0} with {1}…", game.Name, game.CompatibilityTool ?? game.Runner ?? "Winetricks");
            var result = await _managedComponents.InstallD3DCompilerWithWinetricksAsync(game);
            StatusText = UiLocalizationService.Present(result.Message);
            ReShadeUpdateText = UiLocalizationService.Present(result.Message);
        }
        catch (Exception ex)
        {
            ReShadeUpdateText = ShowStatus(LF("Falha ao instalar D3DCompiler com Winetricks: {0}", "Failed to install D3DCompiler with Winetricks: {0}", ex.Message));
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task CheckReShadeUpdateAsync()
    {
        if (SelectedGame is null || IsBusy) return;
        if (!SelectedGame.HasConfirmedPatchTarget)
        {
            ReShadeUpdateText = L("Defina o executável alvo em Editar jogo antes de verificar o ReShade.", "Set the target executable in Edit game before checking ReShade.");
            StatusText = ReShadeUpdateText;
            return;
        }
        IsBusy = true;
        try
        {
            var result = await _managedComponents.CheckReShadeUpdateAsync(SelectedGame);
            ReShadeUpdateText = UiLocalizationService.Present(result.Message);
            StatusText = UiLocalizationService.Present(result.Message);
        }
        catch (Exception ex)
        {
            ReShadeUpdateText = LF("Falha ao verificar atualização: {0}", "Update check failed: {0}", ex.Message);
            StatusText = ReShadeUpdateText;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanQuickApplyDlssDefaults))]
    private async Task QuickApplyDlssDefaultsAsync()
    {
        if (SelectedGame is null || !CanQuickApplyDlssDefaults) return;
        var game = SelectedGame;
        IsBusy = true;
        try
        {
            StatusText = LF("Aplicação rápida: aplicando padrões de DLSS/Streamline em {0}…", "Quick apply: applying DLSS/Streamline defaults to {0}…", game.Name);
            IReadOnlyList<string>? files = _probeCache.TryGetValue(Key(game), out var cached)
                ? cached.Files
                : await Task.Run(() => GameFileIndexer.Build(game.InstallPath, 8));
            var result = await _dlssDefaults.ApplyDefaultsAsync(game, DlssVersionDefaults, files);
            StatusText = UiLocalizationService.Present(result.Summary);
            if (result.ChangedCount > 0)
                await ReprobeGameAsync(game);
        }
        catch (Exception ex)
        {
            StatusText = LF("Falha na aplicação rápida: {0}", "Quick apply failed: {0}", ex.Message);
        }
        finally
        {
            IsBusy = false;
            NotifyDlssDefaults();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRestoreAllDlss))]
    private async Task RestoreAllDlssAsync()
    {
        if (SelectedGame is null || !CanRestoreAllDlss) return;
        var game = SelectedGame;
        IsBusy = true;
        try
        {
            StatusText = LF("Restaurando todos os originais de DLSS/Streamline do RHI em {0}…", "Restoring all RHI DLSS/Streamline originals in {0}…", game.Name);
            IReadOnlyList<string>? files = _probeCache.TryGetValue(Key(game), out var cached)
                ? cached.Files
                : await Task.Run(() => GameFileIndexer.Build(game.InstallPath, 8));
            var result = await _dlssDefaults.RestoreAllAsync(game, files);
            StatusText = UiLocalizationService.Present(result.Summary);
            if (result.ChangedCount > 0)
                await ReprobeGameAsync(game);
        }
        catch (Exception ex)
        {
            StatusText = LF("Falha em Restaurar tudo: {0}", "Restore all failed: {0}", ex.Message);
        }
        finally
        {
            IsBusy = false;
            NotifyDlssDefaults();
        }
    }

    public async Task<IReadOnlyList<LinuxDlssBatchCandidate>> GetDlssBatchCandidatesAsync(CancellationToken cancellationToken = default)
    {
        var games = _allGames.ToArray();
        var indexes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var game in games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!game.HasConfirmedPatchTarget ||
                string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase) ||
                game.DlssScanSkippedByManifest || string.IsNullOrWhiteSpace(game.InstallPath) || !Directory.Exists(game.InstallPath))
                continue;
            var key = Key(game);
            if (_probeCache.TryGetValue(key, out var cached))
                indexes[key] = cached.Files;
            else
                indexes[key] = await Task.Run(() => GameFileIndexer.Build(game.InstallPath, 8, cancellationToken), cancellationToken);
        }

        return _dlssDefaults.InspectBatch(
            games,
            game => indexes.TryGetValue(Key(game), out var files) ? files : null,
            cancellationToken);
    }

    public async Task<LinuxDlssBatchResult> ApplyDlssBatchAsync(
        IReadOnlyList<DetectedGame> games,
        LinuxDlssVersionDefaults defaults,
        CancellationToken cancellationToken = default)
    {
        if (games.Count == 0) return new LinuxDlssBatchResult([]);
        IsBusy = true;
        try
        {
            StatusText = LF("DLSS/Streamline em lote: preparando {0} jogo(s)…", "DLSS/Streamline batch: preparing {0} game(s)…", games.Count);
            var indexes = await PrepareDlssBatchIndexesAsync(games, cancellationToken);
            var result = await _dlssDefaults.ApplyBatchAsync(
                games, defaults,
                game => indexes.TryGetValue(Key(game), out var files) ? files : null,
                cancellationToken);
            _probeCache.Clear();
            if (SelectedGame is { } selected && result.ChangedCount > 0)
                await ReprobeGameAsync(selected);
            StatusText = UiLocalizationService.Present(result.Summary);
            return result;
        }
        finally
        {
            IsBusy = false;
            NotifyDlssDefaults();
        }
    }

    public async Task<LinuxDlssBatchResult> RestoreDlssBatchAsync(
        IReadOnlyList<DetectedGame> games,
        CancellationToken cancellationToken = default)
    {
        if (games.Count == 0) return new LinuxDlssBatchResult([]);
        IsBusy = true;
        try
        {
            StatusText = LF("Restauração DLSS/Streamline em lote: preparando {0} jogo(s)…", "DLSS/Streamline batch restore: preparing {0} game(s)…", games.Count);
            var indexes = await PrepareDlssBatchIndexesAsync(games, cancellationToken);
            var result = await _dlssDefaults.RestoreBatchAsync(
                games,
                game => indexes.TryGetValue(Key(game), out var files) ? files : null,
                cancellationToken);
            _probeCache.Clear();
            if (SelectedGame is { } selected && result.ChangedCount > 0)
                await ReprobeGameAsync(selected);
            StatusText = UiLocalizationService.Present(result.Summary);
            return result;
        }
        finally
        {
            IsBusy = false;
            NotifyDlssDefaults();
        }
    }

    private async Task<Dictionary<string, IReadOnlyList<string>>> PrepareDlssBatchIndexesAsync(
        IReadOnlyList<DetectedGame> games,
        CancellationToken cancellationToken)
    {
        var indexes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var game in games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!game.HasConfirmedPatchTarget) continue;
            var key = Key(game);
            if (_probeCache.TryGetValue(key, out var cached))
            {
                indexes[key] = cached.Files;
                continue;
            }
            if (string.IsNullOrWhiteSpace(game.InstallPath) || !Directory.Exists(game.InstallPath))
            {
                indexes[key] = Array.Empty<string>();
                continue;
            }
            indexes[key] = await Task.Run(() => GameFileIndexer.Build(game.InstallPath, 8, cancellationToken), cancellationToken);
        }
        return indexes;
    }

    [RelayCommand]
    private async Task SwapDlssComponentAsync(string? component)
    {
        if (SelectedGame is null || !TryResolveComponent(component, out var kind, out var version, out var paths)) return;
        if (!SelectedGame.HasConfirmedPatchTarget)
        {
            StatusText = L("Defina o executável alvo em Editar jogo antes de trocar DLLs do jogo.", "Set the target executable in Edit game before swapping game DLLs.");
            return;
        }
        if (OrchestratedDlssStackManaged)
        {
            StatusText = ShortFuseInstalled
                ? L("A troca direta de DLSS fica bloqueada enquanto o ShortFuse controla a pilha transacional DLSS/Streamline. Remova o ShortFuse primeiro.", "Direct DLSS swapping is blocked while ShortFuse controls the transactional DLSS/Streamline stack. Remove ShortFuse first.")
                : L("A troca direta de DLSS fica bloqueada enquanto o DLSS5 Feeder Full gerenciado pelo RHI controla a pilha privada SR/NR. Remova o Feeder Full primeiro.", "Direct DLSS swapping is blocked while RHI-managed DLSS5 Feeder Full controls the private SR/NR stack. Remove Feeder Full first.");
            return;
        }
        if (string.IsNullOrWhiteSpace(version))
        {
            StatusText = L("Escolha primeiro uma versão do DLSS.", "Choose a DLSS version first.");
            return;
        }
        if (paths.Count == 0)
        {
            StatusText = LF("{0}: nenhuma DLL de destino existente foi encontrada; nada foi alterado.", "{0}: no existing target DLL was found; nothing changed.", DlssComponentDisplayName(kind));
            return;
        }

        IsBusy = true;
        try
        {
            var displayName = DlssComponentDisplayName(kind);
            StatusText = LF("Baixando/aplicando {0} {1}…", "Downloading/applying {0} {1}…", displayName, version);
            var result = await _dlss.SwapAsync(kind, paths, version);
            if (result.Success)
            {
                var changed = result.Results.Count(x => x.Changed);
                StatusText = changed == 0
                    ? LF("{0} {1} já corresponde ao arquivo do jogo; nada foi alterado e nenhum .original foi criado.", "{0} {1} already matches the game file; nothing changed and no .original was created.", displayName, version)
                    : LF("{0} {1} aplicado a {2} arquivo(s); original preservado como .original.", "{0} {1} applied to {2} file(s); original preserved as .original.", displayName, version, changed);
                NotifyDlss();
            }
            else
            {
                StatusText = LF("Falha ao trocar {0}: {1}", "Failed to swap {0}: {1}", displayName, result.Error);
            }
        }
        catch (Exception ex)
        {
            StatusText = LF("Falha ao trocar {0}: {1}", "Failed to swap {0}: {1}", DlssComponentDisplayName(kind), ex.Message);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RestoreDlssComponentAsync(string? component)
    {
        if (SelectedGame is null || !TryResolveComponent(component, out var kind, out _, out var paths)) return;
        if (!SelectedGame.HasConfirmedPatchTarget)
        {
            StatusText = L("Defina o executável alvo em Editar jogo antes de restaurar DLLs do jogo.", "Set the target executable in Edit game before restoring game DLLs.");
            return;
        }
        if (OrchestratedDlssStackManaged)
        {
            StatusText = ShortFuseInstalled
                ? L("A restauração direta do DLSS fica bloqueada enquanto o ShortFuse controla a pilha transacional DLSS/Streamline. Remova o ShortFuse primeiro.", "Direct DLSS restore is blocked while ShortFuse controls the transactional DLSS/Streamline stack. Remove ShortFuse first.")
                : L("A restauração direta do DLSS fica bloqueada enquanto o DLSS5 Feeder Full gerenciado pelo RHI controla a pilha privada. Remova o Feeder Full primeiro.", "Direct DLSS restore is blocked while RHI-managed DLSS5 Feeder Full controls the private stack. Remove Feeder Full first.");
            return;
        }
        if (paths.Count == 0)
        {
            StatusText = LF("{0}: nenhuma DLL de destino existente foi encontrada.", "{0}: no existing target DLL was found.", DlssComponentDisplayName(kind));
            return;
        }

        IsBusy = true;
        try
        {
            var displayName = DlssComponentDisplayName(kind);
            var result = await _dlss.RestoreAsync(kind, paths);
            StatusText = result.Success
                ? LF("{0}: DLL original restaurada.", "{0}: original DLL restored.", displayName)
                : LF("{0}: restauração indisponível — {1}", "{0}: restore unavailable — {1}", displayName, result.Error);
            if (result.Success) NotifyDlss();
        }
        catch (Exception ex)
        {
            StatusText = LF("{0}: falha na restauração — {1}", "{0}: restore failed — {1}", DlssComponentDisplayName(kind), ex.Message);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task SwapStreamlineAsync()
    {
        if (SelectedGame is null || IsBusy) return;
        if (OrchestratedDlssStackManaged)
        {
            StatusText = ShortFuseInstalled
                ? L("A troca direta do Streamline fica bloqueada enquanto o ShortFuse controla a pilha transacional. Remova o ShortFuse primeiro.", "Direct Streamline swapping is blocked while ShortFuse controls the transactional stack. Remove ShortFuse first.")
                : L("A troca direta do Streamline fica bloqueada enquanto o DLSS5 Feeder Full é gerenciado para este jogo.", "Direct Streamline swapping is blocked while DLSS5 Feeder Full is managed for this game.");
            return;
        }
        if (string.IsNullOrWhiteSpace(SelectedStreamlineVersion))
        {
            StatusText = L("Escolha primeiro uma versão do Streamline.", "Choose a Streamline version first.");
            return;
        }
        var streamlineFolders = _dlssDefaults.PanelStreamlineFolders(SelectedGame, _dlssDetection);
        if (streamlineFolders.Count == 0)
        {
            StatusText = L("Streamline: nenhuma pasta local do Streamline foi detectada no jogo; nada foi alterado.", "Streamline: no local Streamline folder was detected in the game; nothing changed.");
            return;
        }

        IsBusy = true;
        try
        {
            StatusText = LF("Baixando/aplicando Streamline {0}…", "Downloading/applying Streamline {0}…", SelectedStreamlineVersion);
            var result = await _dlss.SwapStreamlineAsync(streamlineFolders, SelectedStreamlineVersion);
            if (result.Success)
            {
                var changed = result.Results.Count(x => x.Changed);
                StatusText = changed == 0
                    ? LF("Streamline {0} já corresponde a todas as DLLs detectadas; nada foi alterado e nenhum backup .original foi criado.", "Streamline {0} already matches all detected DLLs; nothing changed and no .original backup was created.", SelectedStreamlineVersion)
                    : LF("Streamline {0} aplicado a {1} DLL(s) existente(s); originais preservados como .original.", "Streamline {0} applied to {1} existing DLL(s); originals preserved as .original.", SelectedStreamlineVersion, changed);
                NotifyDlss();
            }
            else
            {
                StatusText = LF("Falha ao trocar Streamline: {0}", "Failed to swap Streamline: {0}", result.Error);
            }
        }
        catch (Exception ex)
        {
            StatusText = LF("Falha ao trocar Streamline: {0}", "Failed to swap Streamline: {0}", ex.Message);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RestoreStreamlineAsync()
    {
        if (SelectedGame is null || IsBusy) return;
        if (OrchestratedDlssStackManaged)
        {
            StatusText = ShortFuseInstalled
                ? L("A restauração direta do Streamline fica bloqueada enquanto o ShortFuse controla a pilha transacional. Remova o ShortFuse primeiro.", "Direct Streamline restore is blocked while ShortFuse controls the transactional stack. Remove ShortFuse first.")
                : L("A restauração direta do Streamline fica bloqueada enquanto o DLSS5 Feeder Full é gerenciado para este jogo.", "Direct Streamline restore is blocked while DLSS5 Feeder Full is managed for this game.");
            return;
        }
        var streamlineFolders = _dlssDefaults.PanelStreamlineFolders(SelectedGame, _dlssDetection);
        if (streamlineFolders.Count == 0)
        {
            StatusText = L("Streamline: nenhuma pasta local do Streamline foi detectada no jogo.", "Streamline: no local Streamline folder was detected in the game.");
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _dlss.RestoreStreamlineAsync(streamlineFolders);
            StatusText = result.Success
                ? LF("Streamline: {0} DLL(s) original(is) restaurada(s).", "Streamline: {0} original DLL(s) restored.", result.Results.Count)
                : LF("Streamline: restauração indisponível — {0}", "Streamline: restore unavailable — {0}", result.Error);
            if (result.Success) NotifyDlss();
        }
        catch (Exception ex)
        {
            StatusText = LF("Streamline: falha na restauração — {0}", "Streamline: restore failed — {0}", ex.Message);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void OpenGameFolder()
    {
        if (SelectedGame is null) return;
        if (!SelectedGame.HasConfirmedPatchTarget)
        {
            StatusText = L("A raiz real do jogo ainda não foi definida. Use Editar jogo.", "The real game root is not defined yet. Use Edit game.");
            return;
        }
        if (!LinuxDesktopOpener.TryOpenDirectory(SelectedGame.InstallPath, out var error))
            StatusText = LF("Não foi possível abrir a pasta do jogo: {0}", "Could not open the game folder: {0}", error);
    }

    [RelayCommand]
    private void OpenPrefix()
    {
        if (SelectedGame?.PrefixPath is not { } prefix || !Directory.Exists(prefix)) return;
        if (!LinuxDesktopOpener.TryOpenDirectory(prefix, out var error))
            StatusText = LF("Não foi possível abrir o prefixo Wine/Proton: {0}", "Could not open the Wine/Proton prefix: {0}", error);
    }

    private void LoadReShadeProfile(DetectedGame game)
    {
        var managed = _managedComponents.IsReShadeManaged(game);
        var profile = _managedComponents.GetReShadeOptions(game);
        SelectedReShadeChannel = ReShadeChannelDisplay(managed ? profile.Channel.ToString() : _settings.ReShadeChannel);
        ReShadeLegacyVersion = string.IsNullOrWhiteSpace(profile.LegacyVersion) ? "6.4.1" : profile.LegacyVersion;
        ReShadeCustomDllPath = profile.CustomDllPath ?? "";
        ReShadeD3DCompiler47Path = profile.D3DCompiler47Path ?? "";
        ReShadeManageIni = managed ? profile.ManageIni : _settings.ReShadeManageIni;
        var packs = profile.ShaderPackIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _selectedShaderPackIds.Clear();
        _selectedShaderPackIds.UnionWith(packs);
        ShaderCrosireMaster = packs.Contains("CrosireMaster");
        ShaderCrosireLegacy = packs.Contains("CrosireLegacy");
        ShaderClshortfuse = packs.Contains("ClshortfuseShaders");
        ShaderMaxG2D = packs.Contains("MaxG2DSimpleHDR");
        ReShadeUpdateText = LF("Perfil: {0}{1}.", "Profile: {0}{1}.", ReShadeChannelDisplay(profile.Channel.ToString()), string.IsNullOrWhiteSpace(profile.LegacyVersion) ? "" : $" {profile.LegacyVersion}");
    }

    private void ResetReShadeProfile()
    {
        SelectedReShadeChannel = ReShadeChannelDisplay(_settings.ReShadeChannel);
        ReShadeLegacyVersion = "6.4.1";
        ReShadeCustomDllPath = "";
        ReShadeD3DCompiler47Path = "";
        ReShadeManageIni = _settings.ReShadeManageIni;
        ShaderCrosireMaster = false;
        ShaderCrosireLegacy = false;
        ShaderClshortfuse = false;
        ShaderMaxG2D = false;
        _selectedShaderPackIds.Clear();
        ReShadeUpdateText = UiLocalizationService.Format("Ui.ReShadeGlobalDefault", ReShadeChannelDisplay(_settings.ReShadeChannel));
    }

    public async Task<ShaderAddonManagerUiSnapshot?> GetShaderAddonManagerSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedGame is null) return null;
        var game = SelectedGame;
        var shaderCatalog = await _managedComponents.RefreshReShadeShaderPackCatalogAsync(cancellationToken);
        var addonSnapshot = await _managedComponents.GetReShadeAddonManagerSnapshotAsync(game, cancellationToken);
        var selectedShaders = _managedComponents.GetReShadeOptions(game).ShaderPackIds.ToArray();
        return new ShaderAddonManagerUiSnapshot(
            shaderCatalog,
            selectedShaders,
            addonSnapshot.Catalog,
            addonSnapshot.SelectedIds,
            _managedComponents.CustomShaderRoot,
            addonSnapshot.CustomAddonRoot);
    }

    public Task<IReadOnlyList<ReShadeShaderPackFileInfo>> GetShaderPackFilesAsync(
        string packId,
        CancellationToken cancellationToken = default)
        => _managedComponents.GetReShadeShaderPackFilesAsync(packId, cancellationToken);

    /// <summary>Saves the pack's file selection; returns the localised failure, or null when saved.</summary>
    public string? SetShaderPackExcludedFiles(string packId, IEnumerable<string> excluded)
        => TrySaveStore(() => _managedComponents.SetReShadeShaderPackExcludedFiles(packId, excluded));

    public async Task<ComponentOperationResult> ApplyShaderAddonManagerAsync(
        IReadOnlyList<string> shaderIds,
        IReadOnlyList<string> addonIds,
        CancellationToken cancellationToken = default)
    {
        if (SelectedGame is null) return ComponentOperationResult.Fail(L("Selecione um jogo primeiro.", "Select a game first."));
        if (IsBusy) return ComponentOperationResult.Fail(L("Outra operação do RHI já está em andamento.", "Another RHI operation is already running."));
        var game = SelectedGame;
        IsBusy = true;
        try
        {
            // Apply installs ReShade when RHI does not manage it yet (plus RE Framework on RE Engine): warn first.
            var installsReShade = !_managedComponents.IsReShadeManaged(game) &&
                (_probeCache.TryGetValue(Key(game), out var probe)
                    ? probe.Components.FirstOrDefault(c => c.Kind == ManagedComponentKind.ReShade) is not { } row || ManifestInstallWarning.IsFirstInstall(row.Availability)
                    : true);
            if (!await AcceptManifestInstallWarningsAsync(game, ManagedComponentKind.ReShade, "ReShade", installsReShade, ProbedFiles(game)))
                return ComponentOperationResult.Fail(StatusText);

            var options = _managedComponents.GetReShadeOptions(game);
            options.ShaderPackIds = shaderIds
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            StatusText = L("Aplicando seleção de pacotes de shaders…", "Applying shader pack selection…");
            var shaderResult = await _managedComponents.ApplyReShadeAsync(game, options, cancellationToken);
            if (!shaderResult.Success)
            {
                StatusText = UiLocalizationService.Present(shaderResult.Message);
                return shaderResult;
            }

            StatusText = L("Aplicando seleção de add-ons do ReShade…", "Applying ReShade add-on selection…");
            var addonResult = await _managedComponents.ApplyReShadeAddonSelectionAsync(game, addonIds, cancellationToken);
            if (!addonResult.Success)
            {
                return ComponentOperationResult.Fail(ShowStatus(LF("Shaders aplicados, mas os add-ons falharam: {0}", "Shaders applied, but add-ons failed: {0}", UiLocalizationService.Present(addonResult.Message))));
            }

            _selectedShaderPackIds.Clear();
            _selectedShaderPackIds.UnionWith(options.ShaderPackIds);
            ShaderCrosireMaster = _selectedShaderPackIds.Contains("CrosireMaster");
            ShaderCrosireLegacy = _selectedShaderPackIds.Contains("CrosireLegacy");
            ShaderClshortfuse = _selectedShaderPackIds.Contains("ClshortfuseShaders");
            ShaderMaxG2D = _selectedShaderPackIds.Contains("MaxG2DSimpleHDR");
            await ReprobeGameAsync(game);
            return ComponentOperationResult.Ok(ShowStatus(UiLocalizationService.ShaderAddonAppliedFor(
                UiLocalizationService.CurrentLanguage, options.ShaderPackIds.Count, addonIds.Count, shaderResult.FromCachedSource)));
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return ComponentOperationResult.Fail(ShowStatus(LF("Falha em Shaders/Add-ons: {0}", "Shaders/Add-ons failed: {0}", ex.Message)));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static void SetPack(HashSet<string> packs, string id, bool enabled)
    {
        if (enabled) packs.Add(id);
        else packs.Remove(id);
    }

    // Swap/restore targets come from LinuxDlssDefaultsService (ownership exclusion re-applied at action time).
    private bool TryResolveComponent(string? component, out DlssComponentKind kind, out string? version, out IReadOnlyList<string> paths)
    {
        if (!_dlssDefaults.TryPanelTargets(SelectedGame, _dlssDetection, component, out kind, out paths))
        {
            version = null;
            return false;
        }
        version = kind switch
        {
            DlssComponentKind.SuperResolution => SelectedDlssSrVersion,
            DlssComponentKind.RayReconstruction => SelectedDlssRrVersion,
            DlssComponentKind.FrameGeneration => SelectedDlssFgVersion,
            _ => SelectedDlssNrVersion
        };
        return true;
    }

    private static string DlssComponentDisplayName(DlssComponentKind kind) => kind switch
    {
        DlssComponentKind.SuperResolution => "Super Resolution",
        DlssComponentKind.RayReconstruction => "Ray Reconstruction",
        DlssComponentKind.FrameGeneration => "Frame Generation",
        DlssComponentKind.NeuralRendering => "Neural Rendering",
        _ => "DLSS"
    };

    private void ApplyFilter()
    {
        var selected = SelectedGame;
        Games.Clear();
        var mode = ParseLibraryFilterMode(LibraryFilterMode);
        var needsManagedFacts = mode is GameLibraryFilterMode.Installed or GameLibraryFilterMode.RenoDX or GameLibraryFilterMode.Luma;
        foreach (var game in _allGames
                     .Where(game => GameLibraryFilter.Matches(
                         game,
                         mode,
                         SearchText,
                         needsManagedFacts ? GetLibraryFilterFacts(game) : default))
                     .OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase))
            Games.Add(game);
        OnPropertyChanged(nameof(GameCountText));
        if (selected is not null && Games.Contains(selected)) SelectedGame = selected;
        else SelectedGame = Games.FirstOrDefault();
    }

    private GameLibraryFilterFacts GetLibraryFilterFacts(DetectedGame game)
    {
        var records = _deploymentRecords.FindAll(game);
        var hasManagedReShade = records.Any(record => record.Component == ManagedComponentKind.ReShade);
        if (!game.HasConfirmedPatchTarget)
        {
            var hasManagedRenoDx = records.Any(record => record.Component == ManagedComponentKind.RenoDX);
            var hasManagedLuma = records.Any(record => record.Component == ManagedComponentKind.LumaFramework);
            return new(hasManagedReShade, hasManagedRenoDx, hasManagedLuma);
        }
        var hasReShadeIni = HasFileIgnoreCase(game.BinaryDirectory ?? game.InstallPath, "ReShade.ini")
                           || HasFileIgnoreCase(game.InstallPath, "ReShade.ini");
        var isUnrealOrUnity = game.EngineHint?.Contains("Unreal", StringComparison.OrdinalIgnoreCase) == true
                             || game.EngineHint?.Contains("Unity", StringComparison.OrdinalIgnoreCase) == true;
        var hasRenoDx = records.Any(record => record.Component == ManagedComponentKind.RenoDX) || isUnrealOrUnity;
        var hasLuma = records.Any(record => record.Component == ManagedComponentKind.LumaFramework)
                      || _managedComponents.GetLumaPolicy(game).EffectiveModeEnabled;
        return new(hasManagedReShade || hasReShadeIni, hasRenoDx, hasLuma);
    }

    private static bool HasFileIgnoreCase(string? directory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return false;
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Any(path => Path.GetFileName(path).Equals(fileName, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static GameLibraryFilterMode ParseLibraryFilterMode(string? value) => value switch
    {
        "Favourites" => GameLibraryFilterMode.Favourites,
        "Installed" => GameLibraryFilterMode.Installed,
        "Unreal" => GameLibraryFilterMode.Unreal,
        "Unity" => GameLibraryFilterMode.Unity,
        "Other" => GameLibraryFilterMode.Other,
        "RenoDX" => GameLibraryFilterMode.RenoDX,
        "Luma" => GameLibraryFilterMode.Luma,
        "Hidden" => GameLibraryFilterMode.Hidden,
        _ => GameLibraryFilterMode.AllGames
    };

    [RelayCommand]
    private void ToggleViewMode()
    {
        ViewMode = IsCompactView ? "Detail" : "Compact";
        _settings.ViewMode = ViewMode;
        TrySaveStore(() => _settingsStore.Save(_settings));
        NotifyUxSettings();
    }

    [RelayCommand]
    private void SetLibraryFilter(string? mode)
    {
        LibraryFilterMode = LinuxGameLibraryStore.NormalizeFilterMode(mode);
    }

    [RelayCommand]
    private void ToggleFavourite()
    {
        if (SelectedGame is null) return;
        var game = SelectedGame;
        var key = LinuxGameLibraryStore.GameKey(game);
        if (_libraryState.FavouriteGames.Contains(key))
        {
            _libraryState.FavouriteGames.Remove(key);
            game.IsFavourite = false;
        }
        else
        {
            _libraryState.FavouriteGames.Add(key);
            game.IsFavourite = true;
        }
        // Kept for this session even when the save is refused; the status line says it was not saved.
        TrySaveStore(() => _libraryStore.Save(_libraryState));
        ApplyFilter();
        NotifySelected();
    }

    [RelayCommand]
    private void ToggleHidden()
    {
        if (SelectedGame is null) return;
        var game = SelectedGame;
        var key = LinuxGameLibraryStore.GameKey(game);
        if (_libraryState.HiddenGames.Contains(key))
        {
            _libraryState.HiddenGames.Remove(key);
            game.IsHidden = false;
        }
        else
        {
            _libraryState.HiddenGames.Add(key);
            game.IsHidden = true;
        }
        // Kept for this session even when the save is refused; the status line says it was not saved.
        TrySaveStore(() => _libraryStore.Save(_libraryState));
        ApplyFilter();
        NotifySelected();
    }

    public async Task<string> SaveSelectedGameEditAsync(
        string patchExecutablePath,
        string gameRootPath,
        string binaryDirectory,
        string? launchProvider)
    {
        if (SelectedGame is null) return L("Selecione um jogo primeiro.", "Select a game first.");

        if (string.IsNullOrWhiteSpace(patchExecutablePath) || !File.Exists(patchExecutablePath))
            return L("Selecione o executável real do jogo que deve receber detecção e modificações.", "Select the real game executable that should receive detection and modifications.");
        if (string.IsNullOrWhiteSpace(gameRootPath) || !Directory.Exists(gameRootPath))
            return L("A raiz do jogo selecionada não existe.", "The selected game root does not exist.");
        if (string.IsNullOrWhiteSpace(binaryDirectory) || !Directory.Exists(binaryDirectory))
            return L("O diretório de binários/patches selecionado não existe.", "The selected binaries/patches directory does not exist.");

        var executable = Path.GetFullPath(patchExecutablePath);
        var root = Path.GetFullPath(gameRootPath);
        var binaries = Path.GetFullPath(binaryDirectory);

        var executableDirectory = Path.GetDirectoryName(executable);
        if (string.IsNullOrWhiteSpace(executableDirectory))
            return L("Não foi possível determinar o diretório do executável alvo.", "Could not determine the target executable directory.");
        if (!IsPathWithin(root, executable))
            return L("O executável alvo precisa estar dentro da raiz do jogo selecionada.", "The target executable must be inside the selected game root.");
        if (!IsPathWithin(root, binaries))
            return L("O diretório de binários/patches precisa estar dentro da raiz do jogo selecionada.", "The binaries/patches directory must be inside the selected game root.");

        var managedRecords = _deploymentRecords.FindAll(SelectedGame);
        if (managedRecords.Count > 0)
        {
            var targetChanged = !PathEquals(SelectedGame.ExecutablePath, executable)
                                || !PathEquals(SelectedGame.InstallPath, root)
                                || !PathEquals(SelectedGame.BinaryDirectory, binaries);
            if (targetChanged)
                return L("Remova primeiro os componentes gerenciados pelo RHI antes de trocar o alvo do jogo. Isso evita deixar arquivos e estado gerenciado no destino anterior.", "Remove RHI-managed components before changing the game target. This avoids leaving files and managed state at the previous target.");
        }

        _libraryState = _libraryStore.Load();
        var key = LinuxGameLibraryStore.GameKey(SelectedGame);
        _libraryState.GameEdits[key] = new LinuxGameEditOverride
        {
            LaunchProvider = launchProvider,
            PatchExecutablePath = executable,
            GameRootPath = root,
            BinaryDirectory = binaries
        }.Normalize();
        if (TrySaveStore(() => _libraryStore.Save(_libraryState)) is { } notSaved) return notSaved;

        var name = SelectedGame.Name;
        _probeCache.Remove(Key(SelectedGame));
        await RefreshAsync(NoDetectors);
        return LF("{0}: alvo de modificações salvo. A inicialização do launcher foi preservada.", "{0}: modification target saved. Launcher startup was preserved.", name);
    }

    public async Task<string> ResetSelectedGameEditAsync()
    {
        if (SelectedGame is null) return L("Selecione um jogo primeiro.", "Select a game first.");
        if (_deploymentRecords.FindAll(SelectedGame).Count > 0)
            return L("Remova primeiro os componentes gerenciados pelo RHI antes de restaurar a detecção automática do alvo.", "Remove RHI-managed components before restoring automatic target detection.");
        _libraryState = _libraryStore.Load();
        var key = LinuxGameLibraryStore.GameKey(SelectedGame);
        var removed = _libraryState.GameEdits.Remove(key);
        if (TrySaveStore(() => _libraryStore.Save(_libraryState)) is { } notSaved) return notSaved;
        var name = SelectedGame.Name;
        _probeCache.Remove(Key(SelectedGame));
        await RefreshAsync(NoDetectors);
        return removed
            ? LF("{0}: substituições manuais removidas; a detecção automática foi restaurada.", "{0}: manual overrides removed; automatic detection was restored.", name)
            : LF("{0}: nenhuma substituição manual estava salva.", "{0}: no manual overrides were saved.", name);
    }

    public async Task<string> AddManualGameAsync(LinuxManualGameEntry entry)
        => (await TryAddManualGameAsync(entry)).Message;

    /// <summary>Adds a manual game; <c>Added</c> is the structural success flag (the message is localised).</summary>
    public async Task<(bool Added, string Message)> TryAddManualGameAsync(LinuxManualGameEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Name)) return (false, L("Digite um nome para o jogo.", "Enter a name for the game."));
        if (string.IsNullOrWhiteSpace(entry.InstallPath) || !Directory.Exists(entry.InstallPath))
            return (false, L("O diretório do jogo não existe.", "The game directory does not exist."));
        if (!string.IsNullOrWhiteSpace(entry.ExecutablePath) && !File.Exists(entry.ExecutablePath))
            return (false, L("O executável selecionado não existe.", "The selected executable does not exist."));
        if (!string.IsNullOrWhiteSpace(entry.PrefixPath) && !Directory.Exists(entry.PrefixPath))
            return (false, L("O prefixo Wine/Proton selecionado não existe.", "The selected Wine/Proton prefix does not exist."));

        var normalizedPath = Path.GetFullPath(entry.InstallPath);
        if (_allGames.Any(game => Path.GetFullPath(game.InstallPath).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase)))
            return (false, L("Esse diretório de jogo já está presente na biblioteca.", "That game directory is already in the library."));

        _libraryState = _libraryStore.Load();
        if (_libraryState.ManualGames.Any(existing =>
                Path.GetFullPath(existing.InstallPath).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase)))
            return (false, L("Esse jogo manual já está salvo.", "That manual game is already saved."));

        _libraryState.ManualGames.Add(entry);
        _libraryState.FilterMode = "All Games";
        if (TrySaveStore(() => _libraryStore.Save(_libraryState)) is { } notSaved) return (false, notSaved);
        LibraryFilterMode = "All Games";
        await RefreshAsync(ManualOnly);
        SelectedGame = _allGames.FirstOrDefault(game => game.IsManuallyAdded &&
            Path.GetFullPath(game.InstallPath).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));
        return (true, LF("{0} foi adicionado à biblioteca manual.", "Added {0} to the manual library.", entry.Name));
    }

    /// <summary>
    /// Explicit repair after a move: checks every file RHI owns at the game's new folder and only
    /// then re-points its records there, together with the game's preferences, favourite/hidden
    /// flags, edits, HDR settings and update history. Nothing changes when a check fails. Records
    /// found by name and folder (a game without a store id) are adopted only after the user
    /// confirms the old and new folders.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRepairAfterMove))]
    private async Task RepairAfterMoveAsync()
    {
        if (SelectedGame is not { } game) return;
        IsBusy = true;
        var repaired = false;
        try
        {
            var preview = await Task.Run(() => _managedComponents.RelocationPreview(game));
            if (preview.FirstOrDefault(r => r.Adopted) is { } adopted && ConfirmAsync is { } confirm
                && !await confirm(
                    L("Reparar após mover", "Repair after move"),
                    LF("O RHI encontrou componentes que instalou para {0} na pasta {1}, que não existe mais.\n\nUsar esses registros para o jogo em {2}? Confirme somente se você moveu este jogo daquela pasta.",
                        "RHI found components it installed for {0} in {1}, which no longer exists.\n\nUse those records for the game in {2}? Only confirm if you moved this game from that folder.",
                        game.Name, adopted.Record.GameKey, game.InstallPath),
                    L("Usar estes registros", "Use these records")))
            {
                StatusText = L("Reparar após mover cancelado; nada foi alterado.", "Repair after move cancelled; nothing was changed.");
                return;
            }

            StatusText = LF("{0}: verificando os arquivos do RHI na nova pasta…", "{0}: checking RHI's files in the new folder…", game.Name);
            IGameStateStore[] uiStores = [_libraryStore, _apiOverrideStore, _optiScalerVariantStore];
            var result = await Task.Run(() => _managedComponents.RepairAfterMove(game, uiStores));
            if (!result.Success)
            {
                StatusText = UiLocalizationService.Present(result.Message);
                return;
            }

            repaired = true;
            StatusText = LF("{0}: registros e configurações do RHI atualizados para a nova pasta ({1}).",
                "{0}: RHI's records and settings now point to the new folder ({1}).", game.Name, result.Repaired);
            // The library file now holds the game's favourite/hidden/edits/HDR under its new key.
            _libraryState = _libraryStore.Load();
            var libraryKey = LinuxGameLibraryStore.GameKey(game);
            game.IsFavourite = _libraryState.FavouriteGames.Contains(libraryKey);
            game.IsHidden = _libraryState.HiddenGames.Contains(libraryKey);
            if (_libraryState.GameEdits.TryGetValue(libraryKey, out var edit))
                LinuxGameIdentityService.ApplyDetectedIdentity(game, edit);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = StoreSaveFailedText(ex);
        }
        finally
        {
            IsBusy = false;
            NotifySelected();
        }

        if (repaired && ReferenceEquals(SelectedGame, game))
        {
            // Reload every per-game preference from the stores and probe the new folder.
            _probeCache.Remove(Key(game));
            OnSelectedGameChanged(game);
        }
        CheckRepairAfterMove(force: true);
    }

    [RelayCommand(CanExecute = nameof(CanRemoveManualGame))]
    private async Task RemoveManualGameAsync()
    {
        if (SelectedGame is not { IsManuallyAdded: true } selected) return;
        var installPath = Path.GetFullPath(selected.InstallPath);
        _libraryState = _libraryStore.Load();
        _libraryState.ManualGames.RemoveAll(entry =>
        {
            try { return Path.GetFullPath(entry.InstallPath).Equals(installPath, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        });
        if (TrySaveStore(() => _libraryStore.Save(_libraryState)) is not null) return;
        await RefreshAsync(ManualOnly);
        StatusText = LF("{0} removido da biblioteca manual. Os arquivos do jogo não foram alterados.", "{0} removed from the manual library. Game files were not changed.", selected.Name);
    }

    public async Task<ComponentOperationResult> InstallLocalLumaPackageAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        if (SelectedGame is null)
            return ComponentOperationResult.Fail(L("Selecione o jogo que receberá o add-on Luma antes de arrastar o arquivo.", "Select the game that will receive the Luma add-on before dropping the file."));
        if (IsBusy || IsSelectionProbeBusy)
            return ComponentOperationResult.Fail(L("Aguarde a operação/análise atual terminar antes de instalar o add-on Luma.", "Wait for the current operation/analysis to finish before installing the Luma add-on."));

        var game = SelectedGame;
        IsBusy = true;
        try
        {
            if (!await AcceptManifestInstallWarningsAsync(game, ManagedComponentKind.LumaFramework, "Luma", installsTarget: true, ProbedFiles(game)))
                return ComponentOperationResult.Fail(StatusText);
            StatusText = LF("Instalando pacote Luma local em {0}…", "Installing local Luma package in {0}…", game.Name);
            var result = await _managedComponents
                .InstallLocalLumaPackageAsync(game, sourcePath, cancellationToken)
                .ConfigureAwait(true);
            StatusText = UiLocalizationService.Present(result.Message);
            if (result.Success)
                await ReprobeGameAsync(game);
            return result;
        }
        catch (Exception ex)
        {
            var result = ComponentOperationResult.Fail(LF("Luma local: {0}", "Local Luma: {0}", ex.Message));
            StatusText = UiLocalizationService.Present(result.Message);
            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public string ImportCustomAddonFile(string sourcePath)
    {
        var result = _managedComponents.ImportCustomAddonFile(sourcePath);
        return ShowStatus(UiLocalizationService.Present(result.Message));
    }

    private void NotifyUxSettings()
    {
        OnPropertyChanged(nameof(IsCompactView));
        OnPropertyChanged(nameof(ViewToggleText));
        OnPropertyChanged(nameof(GameCountText));
        OnPropertyChanged(nameof(FavouriteActionText));
        OnPropertyChanged(nameof(HideActionText));
        OnPropertyChanged(nameof(TrayIconEnabled));
        OnPropertyChanged(nameof(CloseToTray));
        OnPropertyChanged(nameof(AddonWatcherEnabled));
        OnPropertyChanged(nameof(AddonWatchFolder));
        OnPropertyChanged(nameof(ShowMotdOnStartup));
    }

    private void CancelSelectionProbe()
    {
        _selectionProbeCts?.Cancel();
        _selectionProbeCts?.Dispose();
        _selectionProbeCts = null;
        _selectionGeneration++;
    }

    private void ResetNeuralVersionPins()
    {
        SelectedDlss5FeederVersion = LatestDisplay();
        SelectedDlss5BridgeVersion = LatestDisplay();
        SelectedShortFuseVersion = LatestDisplay();
        SelectedNeuralRenderingDllVersion = LatestDisplay();
        ReplaceLatestLabel(Dlss5FeederVersionOptions, SelectedDlss5FeederVersion);
        ReplaceLatestLabel(Dlss5BridgeVersionOptions, SelectedDlss5BridgeVersion);
        ReplaceLatestLabel(ShortFuseVersionOptions, SelectedShortFuseVersion);
        ReplaceLatestLabel(NeuralRenderingDllVersionOptions, SelectedNeuralRenderingDllVersion);
    }

    private void LoadNeuralVersionPins(DetectedGame game)
    {
        SelectedDlss5FeederVersion = _managedComponents.GetNeuralAddonVersionPin(game, ManagedComponentKind.Dlss5Feeder) ?? LatestDisplay();
        SelectedDlss5BridgeVersion = _managedComponents.GetNeuralAddonVersionPin(game, ManagedComponentKind.Dlss5Dx11Bridge) ?? LatestDisplay();
        SelectedShortFuseVersion = _managedComponents.GetNeuralAddonVersionPin(game, ManagedComponentKind.ShortFuse) ?? LatestDisplay();
        SelectedNeuralRenderingDllVersion = _managedComponents.GetNeuralRenderingDllVersionPin(game) ?? LatestDisplay();
        EnsureOption(Dlss5FeederVersionOptions, SelectedDlss5FeederVersion);
        EnsureOption(Dlss5BridgeVersionOptions, SelectedDlss5BridgeVersion);
        EnsureOption(ShortFuseVersionOptions, SelectedShortFuseVersion);
        EnsureOption(NeuralRenderingDllVersionOptions, SelectedNeuralRenderingDllVersion);
    }

    private void SaveNeuralVersionPin(ManagedComponentKind kind, string value)
    {
        // A null/empty value only ever arrives from an ItemsSource rebuild, never from the user.
        if (_rebuildingOptionLists || string.IsNullOrWhiteSpace(value) || SelectedGame is null) return;
        var game = SelectedGame;
        var pin = IsLatestSelection(value) ? null : value;
        var previous = _managedComponents.GetNeuralAddonVersionPin(game, kind);
        // Loading a game's pins re-sets the same value; only a real change persists or swaps.
        if (string.Equals(previous, pin, StringComparison.OrdinalIgnoreCase)) return;
        if (TrySaveStore(() => _managedComponents.SetNeuralAddonVersionPin(game, kind, pin)) is not null)
        {
            LoadNeuralVersionPins(game); // the dropdown goes back to what is saved
            return;
        }
        if (_managedComponents.IsManaged(game, kind))
            _ = SwapNeuralVersionInPlaceAsync(game, kind, previous);
    }

    private void SaveNeuralRenderingDllPin(string value)
    {
        if (_rebuildingOptionLists || string.IsNullOrWhiteSpace(value) || SelectedGame is null) return;
        var game = SelectedGame;
        var pin = IsLatestSelection(value) ? null : value;
        var previous = _managedComponents.GetNeuralRenderingDllVersionPin(game);
        if (string.Equals(previous, pin, StringComparison.Ordinal)) return;
        if (TrySaveStore(() => _managedComponents.SetNeuralRenderingDllVersionPin(game, pin)) is not null)
        {
            LoadNeuralVersionPins(game);
            return;
        }
        if (OrchestratedDlssStackManaged)
            _ = SwapNeuralVersionInPlaceAsync(game, kind: null, previous);
    }

    /// <summary>
    /// Applies a changed pin to an installed NR method by swapping only the add-on (kind set) or
    /// the orchestrated nvngx_dlssnr.dll (kind null). On failure the previous pin and selection
    /// are restored so the dropdown keeps describing what is installed.
    /// </summary>
    private async Task SwapNeuralVersionInPlaceAsync(DetectedGame game, ManagedComponentKind? kind, string? previousPin)
    {
        void RevertPin()
        {
            if (kind is { } addon) TrySaveStore(() => _managedComponents.SetNeuralAddonVersionPin(game, addon, previousPin));
            else TrySaveStore(() => _managedComponents.SetNeuralRenderingDllVersionPin(game, previousPin));
            if (ReferenceEquals(SelectedGame, game)) LoadNeuralVersionPins(game);
        }

        IsBusy = true;
        try
        {
            StatusText = L("Trocando a versão NR no local…", "Swapping the NR version in place…");
            var result = kind is { } addon
                ? await _managedComponents.SwapNeuralAddonVersionAsync(game, addon)
                : await _managedComponents.SwapNeuralRenderingDllAsync(game);
            StatusText = UiLocalizationService.Present(result.Message);
            if (result.Success)
            {
                await ReprobeGameAsync(game);
                RefreshShortFuseState(game);
                return;
            }
            RevertPin();
        }
        catch (Exception ex)
        {
            StatusText = LF("Falha ao trocar a versão NR: {0}", "Failed to swap the NR version: {0}", ex.Message);
            try { RevertPin(); } catch { /* the status line already reports the failure */ }
        }
        finally
        {
            IsBusy = false;
            NotifyDlss();
        }
    }

    [RelayCommand]
    private async Task LoadNeuralVersionHistoryAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            StatusText = L("Atualizando catálogo de versões dos add-ons de Renderização Neural…", "Refreshing Neural Rendering add-on versions…");
            await EnsureNeuralVersionCatalogAsync(forceRefresh: true, reportStatus: true);
        }
        finally
        {
            IsBusy = false;
            NotifyDlss();
        }
    }

    private async Task EnsureNeuralVersionCatalogAsync(bool forceRefresh = false, bool reportStatus = false)
    {
        if (!forceRefresh && _neuralVersionCatalogLoaded) return;

        if (_neuralVersionCatalogLoadTask is not null)
        {
            try { await _neuralVersionCatalogLoadTask; }
            catch { /* The original load reports/handles its own failure below. */ }
            if (!forceRefresh || _neuralVersionCatalogLoaded)
            {
                if (reportStatus && _neuralVersionCatalogLoaded)
                    StatusText = L("Versões NR atualizadas automaticamente.", "NR versions are up to date.");
                return;
            }
        }

        var loadTask = LoadNeuralVersionCatalogCoreAsync();
        _neuralVersionCatalogLoadTask = loadTask;
        try
        {
            await loadTask;
            _neuralVersionCatalogLoaded = true;
            if (reportStatus)
                StatusText = L("Versões NR atualizadas. Mais recente continua sendo o padrão; selecione uma tag para fixar a versão por jogo.", "NR versions refreshed. Latest remains the default; select a tag to pin that version per game.");
        }
        catch (Exception ex)
        {
            if (reportStatus)
                StatusText = LF("Falha ao atualizar versões NR: {0}", "Failed to refresh NR versions: {0}", ex.Message);
            else
                Debug.WriteLine($"[RHI] Automatic NR version catalog refresh failed: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_neuralVersionCatalogLoadTask, loadTask))
                _neuralVersionCatalogLoadTask = null;
        }
    }

    private async Task LoadNeuralVersionCatalogCoreAsync()
    {
        var feederTask = _managedComponents.ListNeuralAddonVersionsAsync(ManagedComponentKind.Dlss5Feeder);
        var bridgeTask = _managedComponents.ListNeuralAddonVersionsAsync(ManagedComponentKind.Dlss5Dx11Bridge);
        var shortFuseTask = _managedComponents.ListNeuralAddonVersionsAsync(ManagedComponentKind.ShortFuse);
        await Task.WhenAll(feederTask, bridgeTask, shortFuseTask);
        var feederSelected = SelectedDlss5FeederVersion;
        var bridgeSelected = SelectedDlss5BridgeVersion;
        var shortFuseSelected = SelectedShortFuseVersion;
        _rebuildingOptionLists = true;
        try
        {
            ReplaceVersionOptions(Dlss5FeederVersionOptions, feederTask.Result, feederSelected);
            ReplaceVersionOptions(Dlss5BridgeVersionOptions, bridgeTask.Result, bridgeSelected);
            ReplaceVersionOptions(ShortFuseVersionOptions, shortFuseTask.Result, shortFuseSelected);
            // Clearing the list nulled the bound selection; restore it now that the option exists again.
            SelectedDlss5FeederVersion = feederSelected ?? LatestDisplay();
            SelectedDlss5BridgeVersion = bridgeSelected ?? LatestDisplay();
            SelectedShortFuseVersion = shortFuseSelected ?? LatestDisplay();
        }
        finally { _rebuildingOptionLists = false; }
    }

    private static void ReplaceVersionOptions(ObservableCollection<string> target, IReadOnlyList<GitHubReleaseVersion> versions, string selected)
    {
        target.Clear();
        target.Add(LatestDisplay());
        foreach (var version in versions)
            if (!target.Contains(version.Tag, StringComparer.OrdinalIgnoreCase))
                target.Add(version.Tag);
        EnsureOption(target, selected);
    }

    private static void EnsureOption(ObservableCollection<string> target, string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !target.Contains(value, StringComparer.OrdinalIgnoreCase))
            target.Add(value);
    }

    private PeArchitecture? GetUalArchitecture(DetectedGame game)
    {
        var architecture = game.ForcedArchitecture ?? PeArchitectureDetector.Detect(game.ExecutablePath);
        return architecture is PeArchitecture.X86 or PeArchitecture.X64 ? architecture : null;
    }

    private string FormatLatestVersionSummary(string? installedVersion, string? latestVersion)
    {
        if (!string.IsNullOrWhiteSpace(installedVersion) && !string.IsNullOrWhiteSpace(latestVersion))
        {
            if (string.Equals(installedVersion, latestVersion, StringComparison.OrdinalIgnoreCase))
                return LF("Instalado {0} · já é a versão mais recente.", "Installed {0} · already latest.", installedVersion);
            return LF("Instalado {0} · mais recente {1}.", "Installed {0} · latest {1}.", installedVersion, latestVersion);
        }
        if (!string.IsNullOrWhiteSpace(latestVersion))
            return LF("Versão mais recente: {0}.", "Latest version: {0}.", latestVersion);
        if (!string.IsNullOrWhiteSpace(installedVersion))
            return LF("Instalado {0} · carregando versão mais recente…", "Installed {0} · loading latest version…", installedVersion);
        return L("Carregando versão mais recente…", "Loading latest version…");
    }

    private string? CachedUalLatestVersion(DetectedGame game)
    {
        var architecture = GetUalArchitecture(game);
        return architecture is not null && _ualLatestVersionCache.TryGetValue(architecture.Value, out var version)
            ? version
            : null;
    }

    private async Task<string?> GetUalLatestVersionAsync(DetectedGame game, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var architecture = GetUalArchitecture(game);
        if (architecture is null) return null;

        if (!forceRefresh && _ualLatestVersionCache.TryGetValue(architecture.Value, out var cached))
            return cached;
        if (!forceRefresh && _ualLatestVersionLoads.TryGetValue(architecture.Value, out var existingLoad))
            return await existingLoad;

        async Task<string?> LoadAsync()
        {
            var status = await _managedComponents.CheckUltimateAsiLoaderUpdateAsync(game, cancellationToken);
            return status.Success ? status.LatestVersion : null;
        }

        var load = LoadAsync();
        if (!forceRefresh) _ualLatestVersionLoads[architecture.Value] = load;
        try
        {
            var version = await load;
            if (!string.IsNullOrWhiteSpace(version)) _ualLatestVersionCache[architecture.Value] = version;
            return version;
        }
        finally
        {
            if (!forceRefresh && _ualLatestVersionLoads.TryGetValue(architecture.Value, out var active) && ReferenceEquals(active, load))
                _ualLatestVersionLoads.Remove(architecture.Value);
        }
    }

    private async Task<string?> GetShortFuseLatestVersionAsync(DetectedGame game, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && !string.IsNullOrWhiteSpace(_shortFuseLatestVersionCache))
            return _shortFuseLatestVersionCache;
        if (!forceRefresh && _shortFuseLatestVersionLoad is not null)
            return await _shortFuseLatestVersionLoad;

        async Task<string?> LoadAsync()
        {
            var status = await _managedComponents.CheckShortFuseLatestReleaseAsync(game, cancellationToken);
            return status.Success ? status.LatestVersion : null;
        }

        var load = LoadAsync();
        if (!forceRefresh) _shortFuseLatestVersionLoad = load;
        try
        {
            var version = await load;
            if (!string.IsNullOrWhiteSpace(version)) _shortFuseLatestVersionCache = version;
            return version;
        }
        finally
        {
            if (!forceRefresh && ReferenceEquals(_shortFuseLatestVersionLoad, load))
                _shortFuseLatestVersionLoad = null;
        }
    }

    private async Task RefreshSelectedLatestVersionsAsync(DetectedGame game)
    {
        try
        {
            var ualTask = GetUalLatestVersionAsync(game);
            var shortFuseTask = GetShortFuseLatestVersionAsync(game);
            await Task.WhenAll(ualTask, shortFuseTask);
            if (!ReferenceEquals(SelectedGame, game)) return;

            UalLatestVersionText = !string.IsNullOrWhiteSpace(ualTask.Result)
                ? FormatLatestVersionSummary(_managedComponents.GetUltimateAsiLoaderRecord(game)?.Version, ualTask.Result)
                : GetUalArchitecture(game) is null
                    ? L("Versão mais recente indisponível — arquitetura Windows x86/x64 não detectada.", "Latest version unavailable — no x86/x64 Windows architecture detected.")
                    : L("Versão mais recente indisponível no momento.", "Latest version is currently unavailable.");
            ShortFuseLatestVersionText = !string.IsNullOrWhiteSpace(shortFuseTask.Result)
                ? FormatLatestVersionSummary(_managedComponents.GetShortFuseRecord(game)?.Version, shortFuseTask.Result)
                : L("Versão mais recente indisponível no momento.", "Latest version is currently unavailable.");
            NotifyUal();
            NotifyShortFuse();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RHI] Automatic latest-version refresh failed: {ex.Message}");
        }
    }

    private void ResetShortFuseState()
    {
        ShortFuseAutoConfigEnabled = false;
        SetShortFuseZzzLoadOrderSilently(false);
        ShortFuseLatestVersionText = UiLocalizationService.Text("Ui.NotCheckedYet");
        NotifyShortFuse();
    }

    private void RefreshShortFuseState(DetectedGame game)
    {
        ShortFuseAutoConfigEnabled = _managedComponents.GetShortFuseAutoConfigEnabled(game);
        SetShortFuseZzzLoadOrderSilently(_managedComponents.GetShortFuseZzzLoadOrderInEffect(game));
        var record = _managedComponents.GetShortFuseRecord(game);
        ShortFuseLatestVersionText = FormatLatestVersionSummary(record?.Version, _shortFuseLatestVersionCache);
        NotifyShortFuse();
    }

    private void NotifyShortFuse()
    {
        OnPropertyChanged(nameof(ShortFuseInstalled));
        OnPropertyChanged(nameof(ShortFuseInstalledVersion));
        OnPropertyChanged(nameof(ShortFuseStatusText));
        OnPropertyChanged(nameof(ShortFuseActionText));
        OnPropertyChanged(nameof(ShortFuseNotInstalled));
        OnPropertyChanged(nameof(CanInstallOrRemoveShortFuse));
        OnPropertyChanged(nameof(ShortFuseActionHint));
        OnPropertyChanged(nameof(CanConfigureShortFuse));
        OnPropertyChanged(nameof(CanConfigureShortFuseZzzLoadOrder));
        OnPropertyChanged(nameof(OrchestratedDlssStackManaged));
        CheckShortFuseUpdateCommand.NotifyCanExecuteChanged();
        InstallOrRemoveShortFuseCommand.NotifyCanExecuteChanged();
    }

    // Upstream 2.7.9 ZZZ load order: unlike Auto Config it stays live while ShortFuse is
    // installed, because toggling renames the owned add-on in place.
    public bool CanConfigureShortFuseZzzLoadOrder => SelectedGame is not null && !IsBusy && !IsSelectionProbeBusy;

    private void SetShortFuseZzzLoadOrderSilently(bool value)
    {
        _loadingShortFuseZzzLoadOrder = true;
        try { ShortFuseZzzLoadOrder = value; }
        finally { _loadingShortFuseZzzLoadOrder = false; }
    }

    partial void OnShortFuseZzzLoadOrderChanged(bool value)
    {
        if (_loadingShortFuseZzzLoadOrder || SelectedGame is null) return;
        _ = ApplyShortFuseZzzLoadOrderAsync(SelectedGame, value);
    }

    private async Task ApplyShortFuseZzzLoadOrderAsync(DetectedGame game, bool enabled)
    {
        IsBusy = true;
        try
        {
            var result = await _managedComponents.SetShortFuseZzzLoadOrderAsync(game, enabled);
            StatusText = !result.Success
                ? UiLocalizationService.Present(result.Message)
                : enabled
                    ? L("ShortFuse carregará por último (zzz_renodx-dlss.addon64).", "ShortFuse will load last (zzz_renodx-dlss.addon64).")
                    : L("ShortFuse usa o nome padrão (renodx-dlss.addon64).", "ShortFuse uses the default name (renodx-dlss.addon64).");
        }
        catch (Exception ex) { StatusText = LF("ShortFuse: {0}", "ShortFuse: {0}", ex.Message); }
        finally
        {
            IsBusy = false;
            if (ReferenceEquals(SelectedGame, game))
                SetShortFuseZzzLoadOrderSilently(_managedComponents.GetShortFuseZzzLoadOrderInEffect(game));
        }
    }

    private void ResetUalState()
    {
        UalProxyNames.Clear();
        SelectedUalProxyName = "";
        UalLatestVersionText = UiLocalizationService.Text("Ui.NotCheckedYet");
        NotifyUal();
    }

    private void RefreshUalState(DetectedGame game)
    {
        var previous = SelectedUalProxyName;
        UalProxyNames.Clear();
        foreach (var name in _managedComponents.GetUltimateAsiLoaderProxyNames(game))
            UalProxyNames.Add(name);

        var record = _managedComponents.GetUltimateAsiLoaderRecord(game);
        var installed = record?.Metadata.GetValueOrDefault("UAL.ProxyName");
        if (!string.IsNullOrWhiteSpace(installed))
            SelectedUalProxyName = installed;
        else if (!string.IsNullOrWhiteSpace(previous) &&
                 UalProxyNames.Contains(previous, StringComparer.OrdinalIgnoreCase))
            SelectedUalProxyName = UalProxyNames.First(x => x.Equals(previous, StringComparison.OrdinalIgnoreCase));
        else
            SelectedUalProxyName = _managedComponents.GetUltimateAsiLoaderRecommendedProxy(game);

        UalLatestVersionText = FormatLatestVersionSummary(record?.Version, CachedUalLatestVersion(game));
        NotifyUal();
    }

    private void NotifyUal()
    {
        OnPropertyChanged(nameof(UalInstalled));
        OnPropertyChanged(nameof(UalInstalledProxy));
        OnPropertyChanged(nameof(UalInstalledVersion));
        OnPropertyChanged(nameof(UalStatusText));
        OnPropertyChanged(nameof(CanInstallUal));
        OnPropertyChanged(nameof(CanUpdateUal));
        OnPropertyChanged(nameof(CanInstallOrUpdateUal));
        OnPropertyChanged(nameof(CanRemoveUal));
        OnPropertyChanged(nameof(CanSelectUalProxy));
        OnPropertyChanged(nameof(UalActionText));
        OnPropertyChanged(nameof(UalProxyHint));
        NotifyUalCommands();
    }

    private void NotifyUalCommands()
    {
        CheckUalUpdateCommand.NotifyCanExecuteChanged();
        InstallOrUpdateUalCommand.NotifyCanExecuteChanged();
        RemoveUalCommand.NotifyCanExecuteChanged();
    }

    private void LoadGraphicsApiOverride(DetectedGame game)
    {
        _loadingGraphicsApiOverride = true;
        try
        {
            game.UserGraphicsApiOverride = _apiOverrideStore.Get(game);
            SelectedGraphicsApiOverride = FormatGraphicsApiOverride(game.UserGraphicsApiOverride);
        }
        finally { _loadingGraphicsApiOverride = false; }
    }

    private void LoadOptiScalerVariant(DetectedGame game)
    {
        _loadingOptiScalerVariant = true;
        try
        {
            var variant = _managedComponents.GetInstalledOptiScalerVariant(game) ?? _optiScalerVariantStore.Get(game);
            SelectedOptiScalerVariant = FormatOptiScalerVariant(variant);
        }
        finally { _loadingOptiScalerVariant = false; }
    }

    private void ResetOptiScalerVariant()
    {
        _loadingOptiScalerVariant = true;
        try { SelectedOptiScalerVariant = UiLocalizationService.Text("Ui.OptionStable"); }
        finally { _loadingOptiScalerVariant = false; }
    }

    private static OptiScalerVariant ParseOptiScalerVariant(string? value)
    {
        var normalized = value?.Trim();
        if (string.Equals(normalized, "DLSS NR", StringComparison.OrdinalIgnoreCase)) return OptiScalerVariant.DlssNr;
        if (string.Equals(normalized, UiLocalizationService.Text("Ui.OptionNightly"), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "Nightly", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "Desenvolvimento", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "Desenvolvimento (Nightly)", StringComparison.OrdinalIgnoreCase)) return OptiScalerVariant.Nightly;
        return OptiScalerVariant.Stable;
    }

    private static string FormatOptiScalerVariant(OptiScalerVariant variant)
        => variant switch
        {
            OptiScalerVariant.DlssNr => "DLSS NR",
            OptiScalerVariant.Nightly => UiLocalizationService.Text("Ui.OptionNightly"),
            _ => UiLocalizationService.Text("Ui.OptionStable")
        };

    private void LoadLumaModePreference(DetectedGame game)
    {
        _loadingLumaModePreference = true;
        try
        {
            SelectedLumaModePreference = FormatLumaModePreference(_managedComponents.GetLumaPolicy(game).UserPreference);
        }
        finally { _loadingLumaModePreference = false; }
    }

    private void ReevaluateCurrentGameComponents(DetectedGame game)
    {
        if (_probeCache.TryGetValue(Key(game), out var cached))
        {
            var components = ComponentProbe.Probe(game, cached.Files);
            _managedComponents.Decorate(game, components);
            var refreshed = cached with { Components = components };
            _probeCache[Key(game)] = refreshed;
            ApplyProbeResult(refreshed);
        }
        else
        {
            NotifySelected();
        }
    }

    private void ReevaluateCurrentGameForLumaPolicy(DetectedGame game)
    {
        if (_probeCache.TryGetValue(Key(game), out var cached))
        {
            var components = ComponentProbe.Probe(game, cached.Files);
            _managedComponents.Decorate(game, components);
            var refreshed = cached with { Components = components };
            _probeCache[Key(game)] = refreshed;
            ApplyProbeResult(refreshed);
        }
        else NotifySelected();
        OnPropertyChanged(nameof(LumaPolicyText));
        OnPropertyChanged(nameof(LumaCoexistenceText));
        StatusText = $"{game.Name}: {UiLocalizationService.Present(_managedComponents.GetLumaPolicy(game).Summary)}.";
    }

    private static string ParseHdrMode(string? value)
    {
        if (string.Equals(value, UiLocalizationService.Text("Ui.HdrNativeWayland"), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, LinuxHdrModes.NativeWayland, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "Native Wayland", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "Wayland nativo", StringComparison.OrdinalIgnoreCase)) return LinuxHdrModes.NativeWayland;
        if (string.Equals(value, UiLocalizationService.Text("Ui.HdrGamescope"), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, LinuxHdrModes.Gamescope, StringComparison.OrdinalIgnoreCase)) return LinuxHdrModes.Gamescope;
        return LinuxHdrModes.Off;
    }

    private static string FormatHdrMode(string? mode)
        => mode switch
        {
            LinuxHdrModes.NativeWayland => UiLocalizationService.Text("Ui.HdrNativeWayland"),
            LinuxHdrModes.Gamescope => UiLocalizationService.Text("Ui.HdrGamescope"),
            _ => UiLocalizationService.Text("Ui.HdrOff")
        };

    private void LoadHdrProfile(DetectedGame game)
    {
        _loadingHdrProfile = true;
        try
        {
            var key = LinuxGameLibraryStore.GameKey(game);
            var profile = _libraryState.HdrProfiles.TryGetValue(key, out var saved)
                ? saved.Normalize()
                : new LinuxHdrGameProfile();
            SelectedHdrMode = FormatHdrMode(profile.Mode);
            HdrLegacyWsi = profile.EnableLegacyWsi;
        }
        finally { _loadingHdrProfile = false; }
        NotifyHdrProfile();
    }

    private void ResetHdrProfile()
    {
        _loadingHdrProfile = true;
        try
        {
            SelectedHdrMode = FormatHdrMode(LinuxHdrModes.Off);
            HdrLegacyWsi = false;
        }
        finally { _loadingHdrProfile = false; }
        NotifyHdrProfile();
    }

    private void SaveHdrProfile(DetectedGame game)
    {
        var key = LinuxGameLibraryStore.GameKey(game);
        var profile = new LinuxHdrGameProfile { Mode = ParseHdrMode(SelectedHdrMode), EnableLegacyWsi = HdrLegacyWsi }.Normalize();
        if (profile.Mode == LinuxHdrModes.Off && !profile.EnableLegacyWsi) _libraryState.HdrProfiles.Remove(key);
        else _libraryState.HdrProfiles[key] = profile;
        TrySaveStore(() => _libraryStore.Save(_libraryState));
        NotifyHdrProfile();
    }

    private void NotifyHdrProfile()
    {
        OnPropertyChanged(nameof(CanEditHdrProfile));
        OnPropertyChanged(nameof(SelectedHdrPlan));
        OnPropertyChanged(nameof(HdrProfileHint));
        OnPropertyChanged(nameof(HdrLaunchOptions));
        OnPropertyChanged(nameof(CanCopyHdrLaunchOptions));
    }

    public string GetSelectedHdrLaunchOptions() => HdrLaunchOptions;

    private void ResetGraphicsApiOverride()
    {
        _loadingGraphicsApiOverride = true;
        try { SelectedGraphicsApiOverride = UiLocalizationService.Text("Ui.OptionAutomatic"); }
        finally { _loadingGraphicsApiOverride = false; }
    }

    private void ReevaluateCurrentGameForApiOverride(DetectedGame game)
    {
        if (_probeCache.TryGetValue(Key(game), out var cached))
        {
            var components = ComponentProbe.Probe(game, cached.Files);
            _managedComponents.Decorate(game, components);
            var refreshed = cached with { Components = components };
            _probeCache[Key(game)] = refreshed;
            ApplyProbeResult(refreshed);
        }
        else
        {
            NotifySelected();
        }
        StatusText = game.UserGraphicsApiOverride is null
            ? game.ManifestGraphicsApiOverride is GraphicsApi manifestApi && manifestApi != GraphicsApi.Unknown
                ? LF("{0}: substituição explícita da API gráfica removida; os dados do RHI upstream indicam {1}.", "{0}: explicit graphics API override removed; upstream RHI data indicates {1}.", game.Name, FormatApi(manifestApi))
                : LF("{0}: substituição explícita da API gráfica removida; a detecção automática local é a autoridade.", "{0}: explicit graphics API override removed; local automatic detection is authoritative.", game.Name)
            : LF("{0}: substituição explícita da API gráfica definida como {1}.", "{0}: explicit graphics API override set to {1}.", game.Name, FormatApi(game.UserGraphicsApiOverride.Value));
    }

    private static GraphicsApi? ParseGraphicsApiOverride(string? value) => value switch
    {
        "DirectX 8" => GraphicsApi.DirectX8,
        "DirectX 9" => GraphicsApi.DirectX9,
        "DirectX 10" => GraphicsApi.DirectX10,
        "DirectX 11" => GraphicsApi.DirectX11,
        "DirectX 12" => GraphicsApi.DirectX12,
        "Vulkan" => GraphicsApi.Vulkan,
        "OpenGL" => GraphicsApi.OpenGL,
        _ => null
    };

    private static string FormatGraphicsApiConfidence(GraphicsApiConfidence confidence) => confidence switch
    {
        GraphicsApiConfidence.High => L("confiança alta", "high confidence"),
        GraphicsApiConfidence.Medium => L("confiança média", "medium confidence"),
        GraphicsApiConfidence.Low => L("confiança baixa", "low confidence"),
        _ => L("confiança desconhecida", "unknown confidence")
    };

    private static string FormatGraphicsApiOverride(GraphicsApi? api) => api switch
    {
        GraphicsApi.DirectX8 => "DirectX 8",
        GraphicsApi.DirectX9 => "DirectX 9",
        GraphicsApi.DirectX10 => "DirectX 10",
        GraphicsApi.DirectX11 => "DirectX 11",
        GraphicsApi.DirectX12 => "DirectX 12",
        GraphicsApi.Vulkan => "Vulkan",
        GraphicsApi.OpenGL => "OpenGL",
        _ => UiLocalizationService.Text("Ui.OptionAutomatic")
    };

    private static LumaModePreference ParseLumaModePreference(string? value)
    {
        if (string.Equals(value, UiLocalizationService.Text("Ui.OptionEnabled"), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "Ativado", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "Enabled", StringComparison.OrdinalIgnoreCase)) return LumaModePreference.Enabled;
        if (string.Equals(value, UiLocalizationService.Text("Ui.OptionDisabled"), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "Desativado", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "Disabled", StringComparison.OrdinalIgnoreCase)) return LumaModePreference.Disabled;
        return LumaModePreference.Auto;
    }

    private static string FormatLumaModePreference(LumaModePreference value) => value switch
    {
        LumaModePreference.Enabled => UiLocalizationService.Text("Ui.OptionEnabled"),
        LumaModePreference.Disabled => UiLocalizationService.Text("Ui.OptionDisabled"),
        _ => UiLocalizationService.Text("Ui.OptionAutomatic")
    };

    private static bool IsLatestSelection(string? value)
        => string.Equals(value, "Latest", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "Mais recente", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, UiLocalizationService.Text("Ui.OptionLatest"), StringComparison.OrdinalIgnoreCase);

    private static string LatestDisplay() => UiLocalizationService.Text("Ui.OptionLatest");

    private void NotifySelected()
    {
        OnPropertyChanged(nameof(SelectedTitle));
        OnPropertyChanged(nameof(SelectedSource));
        OnPropertyChanged(nameof(SelectedLibraryProvider));
        OnPropertyChanged(nameof(SelectedLaunchProvider));
        OnPropertyChanged(nameof(SelectedLibraryProviderIcon));
        OnPropertyChanged(nameof(SelectedLaunchProviderIcon));
        OnPropertyChanged(nameof(ShowLibraryProviderBadge));
        OnPropertyChanged(nameof(ShowLaunchProviderBadge));
        OnPropertyChanged(nameof(LibraryProviderToolTip));
        OnPropertyChanged(nameof(LaunchProviderToolTip));
        OnPropertyChanged(nameof(HasConfirmedPatchTarget));
        OnPropertyChanged(nameof(PatchTargetStatusText));
        OnPropertyChanged(nameof(SelectedLaunchExecutable));
        OnPropertyChanged(nameof(SelectedInstallPath));
        OnPropertyChanged(nameof(SelectedPrefix));
        OnPropertyChanged(nameof(SelectedExecutable));
        OnPropertyChanged(nameof(SelectedBinaryDirectory));
        OnPropertyChanged(nameof(SelectedApi));
        OnPropertyChanged(nameof(SelectedApiEvidence));
        OnPropertyChanged(nameof(CanSelectGraphicsApiOverride));
        OnPropertyChanged(nameof(GraphicsApiOverrideHint));
        OnPropertyChanged(nameof(CanSelectLumaMode));
        OnPropertyChanged(nameof(LumaPolicyText));
        OnPropertyChanged(nameof(LumaCoexistenceText));
        OnPropertyChanged(nameof(SelectedEngineHint));
        NotifyHdrProfile();
        OnPropertyChanged(nameof(HasSelectedGame));
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(LaunchToolTip));
        OnPropertyChanged(nameof(CanEditSelectedGame));
        OnPropertyChanged(nameof(FavouriteActionText));
        OnPropertyChanged(nameof(HideActionText));
        OnPropertyChanged(nameof(CanRemoveManualGame));
        RemoveManualGameCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ShowRepairAfterMove));
        RepairAfterMoveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanInstallD3DCompilerWithProtontricks));
        OnPropertyChanged(nameof(CanInstallD3DCompilerWithWinetricks));
        OnPropertyChanged(nameof(D3DCompilerWinetricksButtonText));
        OnPropertyChanged(nameof(D3DCompilerToolHint));
        LaunchCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanUpdateAll));
        UpdateAllCommand.NotifyCanExecuteChanged();
        NotifyDlss();
        NotifyUal();
        NotifyShortFuse();
    }

    private void NotifyDlss()
    {
        OnPropertyChanged(nameof(NeuralRenderingRecommendation));
        OnPropertyChanged(nameof(Dlss5FeederFullManaged));
        OnPropertyChanged(nameof(ShortFuseInstalled));
        OnPropertyChanged(nameof(CanConfigureFeederVersionPin));
        OnPropertyChanged(nameof(CanConfigureBridgeVersionPin));
        OnPropertyChanged(nameof(CanConfigureShortFuseVersionPin));
        OnPropertyChanged(nameof(CanConfigureNeuralRenderingDllVersion));
        OnPropertyChanged(nameof(CanLoadNeuralVersionHistory));
        OnPropertyChanged(nameof(OrchestratedDlssStackManaged));
        OnPropertyChanged(nameof(CanSwapDlssSr));
        OnPropertyChanged(nameof(CanSwapDlssRr));
        OnPropertyChanged(nameof(CanSwapDlssFg));
        OnPropertyChanged(nameof(CanSwapDlssNr));
        OnPropertyChanged(nameof(CanSwapStreamline));
        OnPropertyChanged(nameof(DlssSrDetected));
        OnPropertyChanged(nameof(DlssRrDetected));
        OnPropertyChanged(nameof(DlssFgDetected));
        OnPropertyChanged(nameof(DlssNrDetected));
        OnPropertyChanged(nameof(StreamlineDetected));
        OnPropertyChanged(nameof(DlssSrRestoreAvailable));
        OnPropertyChanged(nameof(DlssRrRestoreAvailable));
        OnPropertyChanged(nameof(DlssFgRestoreAvailable));
        OnPropertyChanged(nameof(DlssNrRestoreAvailable));
        OnPropertyChanged(nameof(StreamlineRestoreAvailable));
        OnPropertyChanged(nameof(DlssSrDeploymentState));
        OnPropertyChanged(nameof(DlssRrDeploymentState));
        OnPropertyChanged(nameof(DlssFgDeploymentState));
        OnPropertyChanged(nameof(DlssNrDeploymentState));
        OnPropertyChanged(nameof(StreamlineDeploymentState));
        OnPropertyChanged(nameof(DlssSrPath));
        OnPropertyChanged(nameof(DlssRrPath));
        OnPropertyChanged(nameof(DlssFgPath));
        OnPropertyChanged(nameof(DlssNrPath));
        OnPropertyChanged(nameof(StreamlinePath));
        OnPropertyChanged(nameof(DlssStatusSummary));
        NotifyDlssDefaults();
    }

    private void NotifyDlssDefaults()
    {
        OnPropertyChanged(nameof(DlssVersionDefaults));
        OnPropertyChanged(nameof(HasDlssVersionDefaults));
        OnPropertyChanged(nameof(DlssDefaultsSummary));
        OnPropertyChanged(nameof(CanQuickApplyDlssDefaults));
        OnPropertyChanged(nameof(CanRestoreAllDlss));
        OnPropertyChanged(nameof(CanOpenDlssBatch));
        QuickApplyDlssDefaultsCommand.NotifyCanExecuteChanged();
        RestoreAllDlssCommand.NotifyCanExecuteChanged();
    }

    private static bool HasRhiBackup(IEnumerable<string> targets)
        => targets.Any(target => File.Exists(target + TransactionalFileDeploymentService.BackupExtension));

    private static string DlssDeploymentState(IReadOnlyList<string> targets)
    {
        if (targets.Count == 0) return L("— Ausente", "— Missing");
        var backups = targets.Count(target => File.Exists(target + TransactionalFileDeploymentService.BackupExtension));
        return backups > 0
            ? LF("✓ RHI aplicado · {0}/{1}", "✓ RHI applied · {0}/{1}", backups, targets.Count)
            : targets.Count == 1 ? L("• DLL do jogo", "• Game DLL") : LF("• DLLs do jogo · {0}", "• Game DLLs · {0}", targets.Count);
    }

    private int CountStreamlineBackups()
    {
        var ext = TransactionalFileDeploymentService.BackupExtension;
        return _dlssDetection.StreamlineFolders
            .SelectMany(folder => DlssManagerService.KnownStreamlineDlls.Select(name => Path.Combine(folder, name)))
            .Distinct(StringComparer.Ordinal)
            .Count(target => File.Exists(target + ext));
    }

    private static void FillVersions(ObservableCollection<string> target, IEnumerable<string> versions)
    {
        target.Clear();
        foreach (var version in versions) target.Add(version);
    }

    private static string FirstOrNotFound(IReadOnlyList<string> paths, string dll)
        => paths.FirstOrDefault() ?? LF("{0} ausente — troca direta bloqueada", "{0} missing — direct swap blocked", dll);

    private static string FormatApi(GraphicsApi api) => api switch
    {
        GraphicsApi.DirectX12 => "DirectX 12",
        GraphicsApi.DirectX11 => "DirectX 11",
        GraphicsApi.DirectX10 => "DirectX 10",
        GraphicsApi.DirectX9 => "DirectX 9",
        GraphicsApi.DirectX8 => "DirectX 8",
        GraphicsApi.Vulkan => "Vulkan",
        GraphicsApi.OpenGL => "OpenGL",
        _ => L("Desconhecida", "Unknown")
    };

    private static bool PathEquals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
        try
        {
            return Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return left.Trim().Equals(right.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool IsPathWithin(string root, string candidate)
    {
        try
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var normalizedCandidate = Path.GetFullPath(candidate);
            if (normalizedCandidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)) return true;
            return normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string Key(DetectedGame g) => LinuxGameLibraryStore.GameKey(g);
}

/// <summary>One component's manifest warning in the install-warning dialog; <see cref="Heading"/> names the component when several are shown.</summary>
public sealed record InstallWarningSection(string? Heading, ManifestInstallWarning Warning);
