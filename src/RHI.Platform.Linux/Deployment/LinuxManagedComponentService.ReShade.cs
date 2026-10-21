using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Deployment;

public sealed partial class LinuxManagedComponentService
{
    /// <summary>
    /// #92: the ReShade row's (and profile Apply's) install on a game without a DLL ReShade whose API is Vulkan takes the
    /// Vulkan-layer path; that explicit choice is the only way the layer is installed (dependency installs never ask).
    /// </summary>
    private ReShadeInstallOptions ReShadeRowOptions(DetectedGame game)
    {
        var record = _records.Find(game, ManagedComponentKind.ReShade);
        // #186: a new install takes the manifest's legacy version for the game; an installed ReShade keeps its own profile.
        var options = record is null ? ReShadeDeploymentService.DefaultOptionsFor(game) : _reshade.GetInstalledOptions(game);
        options.VulkanLayer = ReShadeVulkanLayer.IsLayerRecord(record) || (record is null && ReShadeVulkanLayer.IsVulkanTarget(game));
        return options;
    }

    /// <summary>#186: the versions the ReShade profile's Legacy channel offers (the RHI manifest's list, else 6.4.1).</summary>
    public Task<IReadOnlyList<string>> LegacyReShadeVersionsAsync(CancellationToken cancellationToken = default)
        => _reshade.LegacyVersionsAsync(cancellationToken);

    /// <summary>
    /// #92 (upstream PAR-D07): why a graphics-API override change must wait while ReShade is installed, or null. A
    /// Vulkan-layer ReShade on a game that would no longer be Vulkan, or a DLL ReShade on a game that would become Vulkan,
    /// would leave the wrong ReShade for the API; RHI refuses until ReShade is removed (upstream removes it on the switch;
    /// RHI never removes a component the user did not ask to remove). Changes within DirectX are never refused.
    /// </summary>
    public string? ReShadeApiSwitchRefusal(DetectedGame game, GraphicsApi? newOverride)
    {
        var record = _records.Find(game, ManagedComponentKind.ReShade);
        if (record is null) return null;
        var after = game.ShallowClone();
        after.UserGraphicsApiOverride = newOverride;
        var layer = ReShadeVulkanLayer.IsLayerRecord(record);
        if (layer && !ReShadeVulkanLayer.IsVulkanTarget(after)) return ReShadeVulkanLayer.ApiSwitchLayerInstalledMessage;
        if (!layer && ReShadeVulkanLayer.IsVulkanTarget(after) && !ReShadeVulkanLayer.IsVulkanTarget(game)) return ReShadeVulkanLayer.ApiSwitchDllInstalledMessage;
        return null;
    }

    /// <summary>w7/dxvk-hdr (#200): the game a ReShade install targets (a Vulkan view beside DX9 HDR; see ReShadeDeploymentService.ViewForDxvkHdr).</summary>
    private DetectedGame ReShadeViewForDxvkHdr(DetectedGame game) => _reshade is null ? game : _reshade.ViewForDxvkHdr(game);

    public IReadOnlyList<ReShadeShaderPackInfo> AvailableReShadeShaderPacks => _reshade.AvailableShaderPacks;
    /// <summary>#194 (upstream CacheAllShaders): stages every shader pack not in the cache yet; cache only.</summary>
    public Task<int> PrefetchShaderPacksAsync(CancellationToken cancellationToken = default) => _reshade.PrefetchShaderPacksAsync(cancellationToken);
    public string CustomShaderRoot => _reshade.CustomShaderRoot;

    public Task<IReadOnlyList<ReShadeShaderPackInfo>> RefreshReShadeShaderPackCatalogAsync(
        CancellationToken cancellationToken = default)
        => _reshade.RefreshShaderPackCatalogAsync(cancellationToken);

    public Task<IReadOnlyList<ReShadeShaderPackFileInfo>> GetReShadeShaderPackFilesAsync(
        string packId,
        CancellationToken cancellationToken = default)
        => _reshade.ListShaderPackFilesAsync(packId, cancellationToken);

    public void SetReShadeShaderPackExcludedFiles(string packId, IEnumerable<string> excluded)
        => _reshade.SetShaderPackExcludedFiles(packId, excluded);

    public bool IsReShadeManaged(DetectedGame game) => _reshade.IsManaged(game);

    /// <summary>
    /// The profile the ReShade section and the Shaders/Add-ons dialog start from: the installed profile, with the packs the
    /// game gets now (#179, its effective set; review major 1: a new game's empty record would otherwise read as an explicit
    /// "no packs" choice and turn it into Select on an untouched Apply).
    /// </summary>
    public ReShadeInstallOptions GetReShadeOptions(DetectedGame game)
    {
        var options = _reshade.GetInstalledOptions(game);
        if (EffectiveShaderPacks(game) is { } effective) options.ShaderPackIds = effective.ToArray(); // w7/shader-sets hook (#179)
        return options;
    }

    public async Task<ComponentOperationResult> ApplyReShadeAsync(
        DetectedGame game,
        ReShadeInstallOptions options,
        CancellationToken cancellationToken = default)
    {
        var guard = InstallGuard(game);
        if (guard is not null) return guard;
        if (RecordExplicitShaderChoice(game, options.ShaderPackIds) is { } notSaved) return ComponentOperationResult.Fail(notSaved); // w7/shader-sets hook (#179): a changed list makes the game Select
        game = ReShadeViewForDxvkHdr(game); // w7/dxvk-hdr hook (#200): ReShade beside DX9 HDR takes the Vulkan layer
        // w5/reshade-core (#92): Apply on a Vulkan game is the explicit layer choice too; its shaders need no D3DCompiler.
        options.VulkanLayer = options.VulkanLayer || ReShadeRowOptions(game).VulkanLayer;
        return await Track(game, ManagedComponentKind.ReShade, ComponentUpdateAction.Install, () => InstallWithManagedDependenciesAsync(
            game,
            ManagedComponentKind.ReShade,
            indexedFiles: null,
            async () =>
            {
                if (!options.VulkanLayer && options.ShaderPackIds.Count > 0 && string.IsNullOrWhiteSpace(options.D3DCompiler47Path))
                {
                    var compiler = await EnsureNativeD3DCompilerDependencyAsync(
                        game,
                        explicitCompilerPath: null,
                        requiredBy: "ReShade shader packs",
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (compiler is not null) return compiler;
                }
                return await (ReShadeInstallForTesting ?? _reshade.InstallAsync)(game, options, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Test seam: ReShade's own install inside <see cref="ApplyReShadeAsync"/> (VM tests run offline).</summary>
    internal Func<DetectedGame, ReShadeInstallOptions, CancellationToken, Task<ComponentOperationResult>>? ReShadeInstallForTesting { get; set; }

    public Task<ReShadeUpdateStatus> CheckReShadeUpdateAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
        => _reshade.CheckUpdateAsync(game, cancellationToken);

    public Task<ComponentOperationResult> InstallD3DCompilerWithProtontricksAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var guard = InstallGuard(game);
        return guard is null
            ? _d3dCompilerTools.InstallWithProtontricksAsync(game, cancellationToken)
            : Task.FromResult(guard);
    }

    public Task<ComponentOperationResult> InstallD3DCompilerWithWinetricksAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var guard = InstallGuard(game);
        return guard is null
            ? _d3dCompilerTools.InstallWithWinetricksAsync(game, cancellationToken)
            : Task.FromResult(guard);
    }

    private async Task<ComponentOperationResult?> EnsureNativeD3DCompilerDependencyAsync(
        DetectedGame game,
        string? explicitCompilerPath,
        string requiredBy,
        CancellationToken cancellationToken)
    {
        // An explicit compiler selection belongs to the requesting install transaction itself; the
        // backend validates PE architecture/signature and stages it without invoking helper tools.
        if (!string.IsNullOrWhiteSpace(explicitCompilerPath)) return null;
        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath))
            return ComponentOperationResult.Fail($"{requiredBy} dependency resolution requires a detected Windows executable.");
        if (string.IsNullOrWhiteSpace(game.PrefixPath) || !Directory.Exists(game.PrefixPath))
            return ComponentOperationResult.Fail($"{requiredBy} dependency resolution requires an initialized Wine/Proton prefix.");

        var architecture = GameArchitecture.Of(game);
        if (architecture is not (PeArchitecture.X86 or PeArchitecture.X64))
            return ComponentOperationResult.Fail($"{requiredBy} dependency resolution could not determine x86/x64 architecture.");
        if (ReShadeDeploymentService.FindNativeD3DCompilerForGame(game, architecture) is not null)
            return null;

        var install = game.SteamAppId.HasValue
            ? await _d3dCompilerTools.InstallDependencyWithProtontricksAsync(game, cancellationToken).ConfigureAwait(false)
            : D3DCompilerToolInstaller.CanInstallWithWinetricks(game)
                ? await _d3dCompilerTools.InstallDependencyWithWinetricksAsync(game, cancellationToken).ConfigureAwait(false)
                : D3DCompilerToolInstaller.IsRuntimeUnknownPrefix(game)
                    ? ComponentOperationResult.Fail(D3DCompilerToolInstaller.RuntimeUnknownMessagePrefix + game.PrefixPath)
                    : ComponentOperationResult.Fail("No safe Protontricks/Winetricks route is available for this prefix.");

        return install.Success
            ? null
            : ComponentOperationResult.Fail($"Não foi possível instalar automaticamente D3DCompiler_47, dependência de {requiredBy}: {install.Message}");
    }
}
