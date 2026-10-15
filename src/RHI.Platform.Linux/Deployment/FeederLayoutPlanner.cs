using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using static RHI.Platform.Linux.Deployment.Dlss5FeederFullDeploymentService;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// DLSS5 Feeder Full (DEP-S3, #169): where every file goes for a game, and whether the game can take the stack at all —
/// reads the game folder, changes nothing.
/// </summary>
internal static class FeederLayoutPlanner
{
    internal static ComponentOperationResult? ValidateTarget(DetectedGame game, Dlss5FeederConsumer consumer, bool requireReShade = true)
    {
        if (string.Equals(game.Runner, "Native Linux", StringComparison.OrdinalIgnoreCase))
            return ComponentOperationResult.Fail("DLSS5 Feeder Full currently targets Windows games running through Wine/Proton.");
        if (string.IsNullOrWhiteSpace(game.BinaryDirectory) || !Directory.Exists(game.BinaryDirectory))
            return ComponentOperationResult.Fail("Game binary directory is not known yet.");
        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath))
            return ComponentOperationResult.Fail("Game executable was not detected.");

        var architecture = GameArchitecture.Of(game);
        if (architecture is PeArchitecture.Unknown or PeArchitecture.Arm64)
            return ComponentOperationResult.Fail("DLSS5 Feeder Full requires a recognized x86/x64 Windows executable.");
        if (architecture == PeArchitecture.X86 && consumer != Dlss5FeederConsumer.RenoDxDlss5)
            return ComponentOperationResult.Fail("32-bit DLSS5 Feeder uses the documented RenoDX host64 consumer path. Deep Fried Chicken remains limited to the proven 64-bit layout.");

        var api = game.EffectiveGraphicsApi;
        var supported = architecture == PeArchitecture.X86
            ? api is GraphicsApi.DirectX9 or GraphicsApi.DirectX10 or GraphicsApi.DirectX11
            : api is GraphicsApi.DirectX9 or GraphicsApi.DirectX11 or GraphicsApi.DirectX12;
        if (!supported)
            return ComponentOperationResult.Fail($"DLSS5 Feeder Full does not have a verified local orchestration path for {architecture} {api} yet.");
        if (requireReShade && !GitHubReShadeAddonDeploymentService.HasReShade(game.BinaryDirectory))
            return ComponentOperationResult.Fail("Install ReShade with full add-on support first.");

        // Feeder is the no-native-DLSS workflow. Reuse the bounded file index instead of
        // doing an unbounded recursive walk through large game trees.
        var scanRoot = Directory.Exists(game.InstallPath) ? game.InstallPath : game.BinaryDirectory;
        if (GameFileIndexer.Build(scanRoot!, 8).Any(path => Path.GetFileName(path).Equals(DlssName, StringComparison.OrdinalIgnoreCase)))
            return ComponentOperationResult.Fail("A native nvngx_dlss.dll already exists in this game. Use the native-DLSS workflow instead of Feeder.");
        return null;
    }

    internal static FeederDeploymentLayout ResolveFeederLayout(
        string binaryDirectory,
        string addonDirectory,
        PeArchitecture architecture,
        Dlss5FeederConsumer consumer)
    {
        if (architecture == PeArchitecture.X86)
        {
            if (consumer != Dlss5FeederConsumer.RenoDxDlss5)
                throw new NotSupportedException("Deep Fried Chicken does not have a verified 32-bit host64 deployment contract in RHI Linux.");
            var host64 = Path.Combine(binaryDirectory, Host64DirectoryName);
            return new FeederDeploymentLayout(
                Path.Combine(addonDirectory, FeederAddon32Name),
                Path.Combine(host64, RenoDx5AddonName),
                Path.Combine(host64, DlssName),
                Path.Combine(host64, NrName),
                true,
                host64,
                Path.Combine(host64, Host64ExecutableName));
        }

        if (architecture != PeArchitecture.X64)
            throw new NotSupportedException($"DLSS5 Feeder requires x86 or x64, not {architecture}.");

        return new FeederDeploymentLayout(
            Path.Combine(addonDirectory, FeederAddonName),
            consumer == Dlss5FeederConsumer.RenoDxDlss5
                ? Path.Combine(addonDirectory, RenoDx5AddonName)
                : Path.Combine(binaryDirectory, DeepFriedAddonName),
            Path.Combine(binaryDirectory, DlssName),
            Path.Combine(binaryDirectory, NrName),
            false,
            null,
            null);
    }

    internal static FeederCompilerPlan ResolveCompilerPlan(
        DetectedGame game,
        PeArchitecture architecture,
        string? selectedExternalPath)
    {
        if (string.IsNullOrWhiteSpace(game.BinaryDirectory) || !Directory.Exists(game.BinaryDirectory))
            throw new InvalidOperationException("Game binary directory is not available.");

        var existing = ReShadeDeploymentService.FindNativeD3DCompilerForGame(game, architecture);
        if (existing is not null)
        {
            var inPrefix = !string.IsNullOrWhiteSpace(game.PrefixPath) &&
                           Path.GetFullPath(existing).StartsWith(Path.GetFullPath(game.PrefixPath), StringComparison.Ordinal);
            return new FeederCompilerPlan(existing, null, inPrefix ? $"prefix:{existing}" : $"app-local:{existing}");
        }

        var selected = ExpandUserPath(selectedExternalPath);
        if (!string.IsNullOrWhiteSpace(selected))
        {
            if (!ReShadeDeploymentService.LooksLikeNativeD3DCompiler(selected, architecture))
                throw new InvalidOperationException($"The selected D3DCompiler_47.dll is missing, has the wrong PE architecture, or is not recognized as Microsoft's native HLSL compiler: {selected}");

            var target = Path.Combine(game.BinaryDirectory, "D3DCompiler_47.dll");
            var occupied = ReShadeDeploymentService.FindFileCaseInsensitive(game.BinaryDirectory, "D3DCompiler_47.dll");
            if (occupied is not null)
                throw new InvalidOperationException($"An app-local D3DCompiler_47.dll already exists but was not recognized as a usable native Microsoft compiler: {occupied}");

            return new FeederCompilerPlan(target, selected, $"selected:{selected}");
        }

        throw new InvalidOperationException("No valid compiler was found app-local or in the selected Wine/Proton prefix.");
    }

    internal static string? ExpandUserPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (expanded == "~") expanded = home;
        else if (expanded.StartsWith("~/", StringComparison.Ordinal))
            expanded = Path.Combine(home, expanded[2..]);
        return Path.GetFullPath(expanded);
    }

    internal static bool IsBlockingPreexistingFullStackTarget(string path)
        => !Path.GetFileName(path).Equals(DeepFriedConfigName, StringComparison.OrdinalIgnoreCase);

    internal static bool ShouldPreserveOnRemove(LinuxDeployedFile file, string? currentHash)
        => file.ExistedBeforeInstall ||
           (file.AllowUserChanges &&
            !string.Equals(currentHash, file.Sha256, StringComparison.OrdinalIgnoreCase));

    internal sealed record FeederDeploymentLayout(
        string FeederTarget,
        string ConsumerTarget,
        string DlssTarget,
        string NrTarget,
        bool RequiresHost64,
        string? Host64Directory,
        string? Host64ExecutableTarget);

    internal sealed record FeederCompilerPlan(string UsablePath, string? DeploySourcePath, string Description);

    /// <summary>Where an install puts everything, as <see cref="PlanInstall"/> decided it.</summary>
    internal sealed record FeederInstallTargets(
        string BinaryDirectory,
        string AddonDirectory,
        FeederDeploymentLayout Layout,
        string DeepFriedAddon,
        string DeepFriedBridge,
        string DeepFriedConfig,
        bool DeepFriedConfigPreexisting,
        string IntegrationRoot,
        string ShaderRoot,
        string TextureRoot,
        string Preset);

    /// <summary>
    /// The install's targets, or why the game cannot take them: a target already there (except a Deep Fried Chicken
    /// config, which is kept as the user's), another neural consumer, or a host64 / shader folder that holds files. Folders
    /// holding only empty folders (left by an install rolled back after a crash, #169) do not block. Reads only.
    /// </summary>
    internal static (FeederInstallTargets? Targets, string? Refusal) PlanInstall(DetectedGame game, PeArchitecture architecture, Dlss5FeederConsumer consumer)
    {
        var binaryDir = Path.GetFullPath(game.BinaryDirectory!);
        var addonDir = RenoDxDeploymentService.ResolveAddonDeployPath(binaryDir);
        FeederDeploymentLayout layout;
        try { layout = ResolveFeederLayout(binaryDir, addonDir, architecture, consumer); }
        catch (NotSupportedException ex) { return (null, ex.Message); }
        var deepFriedConfig = (consumer == Dlss5FeederConsumer.DeepFriedChicken
            ? ReShadeDeploymentService.FindFileCaseInsensitive(binaryDir, DeepFriedConfigName)
            : null) ?? Path.Combine(binaryDir, DeepFriedConfigName);
        var integrationRoot = Path.Combine(binaryDir, "reshade-shaders", "RHI", IntegrationRootName);
        var targets = new FeederInstallTargets(
            binaryDir, addonDir, layout,
            Path.Combine(binaryDir, DeepFriedAddonName),
            Path.Combine(binaryDir, DeepFriedBridgeName),
            deepFriedConfig,
            consumer == Dlss5FeederConsumer.DeepFriedChicken && File.Exists(deepFriedConfig),
            integrationRoot,
            Path.Combine(integrationRoot, "Shaders"),
            Path.Combine(integrationRoot, "Textures"),
            Path.Combine(binaryDir, PresetName));

        var protectedTargets = consumer == Dlss5FeederConsumer.DeepFriedChicken
            ? new[] { layout.FeederTarget, targets.DeepFriedAddon, targets.DeepFriedBridge, targets.DeepFriedConfig, layout.DlssTarget, layout.NrTarget, targets.Preset }
            : new[] { layout.FeederTarget, layout.ConsumerTarget, layout.DlssTarget, layout.NrTarget, targets.Preset };
        foreach (var path in protectedTargets)
            if (IsBlockingPreexistingFullStackTarget(path) &&
                ReShadeDeploymentService.FindFileCaseInsensitive(Path.GetDirectoryName(path)!, Path.GetFileName(path)) is { } existingTarget)
                return (null, $"External or pre-existing DLSS5 Feeder Full target detected: {existingTarget}. RHI Linux will not overwrite it automatically.");

        foreach (var conflict in ConflictPaths(binaryDir, addonDir, consumer))
            if (ReShadeDeploymentService.FindFileCaseInsensitive(Path.GetDirectoryName(conflict)!, Path.GetFileName(conflict)) is { } existingConflict)
                return (null, $"Consumidor neural conflitante detectado: {existingConflict}. Apenas um consumidor neural pode controlar o processo. Remova explicitamente o add-on conflitante primeiro.");

        if (layout.RequiresHost64 && HoldsFiles(layout.Host64Directory!))
            return (null, $"External or previous host64 tree detected at {layout.Host64Directory}. RHI Linux will not overwrite it automatically.");
        if (HoldsFiles(integrationRoot))
            return (null, $"External or previous shader tree detected at {integrationRoot}. RHI Linux will not overwrite it automatically.");
        return (targets, null);
    }

    private static bool HoldsFiles(string directory)
        => Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = 0 }).Any();

    /// <summary>The other neural consumers (and ShortFuse under its ZZZ name) that keep this one from being installed.</summary>
    internal static IEnumerable<string> ConflictPaths(string binaryDir, string addonDir, Dlss5FeederConsumer consumer)
        => (consumer == Dlss5FeederConsumer.DeepFriedChicken
            ? new[]
            {
                Path.Combine(binaryDir, RenoDx5AddonName),
                Path.Combine(binaryDir, RenoDxLegacyAddonName),
                Path.Combine(binaryDir, AlexToolkitAddonName),
                Path.Combine(binaryDir, Dlss5BridgeAddonName),
                Path.Combine(binaryDir, Dlss5Dx11BridgeAddonName),
                Path.Combine(addonDir, RenoDx5AddonName),
                Path.Combine(addonDir, RenoDxLegacyAddonName),
                Path.Combine(addonDir, AlexToolkitAddonName),
                Path.Combine(addonDir, Dlss5BridgeAddonName),
                Path.Combine(addonDir, Dlss5Dx11BridgeAddonName)
            }
            : new[]
            {
                Path.Combine(binaryDir, DeepFriedAddonName),
                Path.Combine(binaryDir, DeepFriedBridgeName),
                Path.Combine(addonDir, DeepFriedAddonName),
                Path.Combine(binaryDir, RenoDxLegacyAddonName),
                Path.Combine(binaryDir, AlexToolkitAddonName),
                Path.Combine(binaryDir, Dlss5BridgeAddonName),
                Path.Combine(binaryDir, Dlss5Dx11BridgeAddonName),
                Path.Combine(addonDir, RenoDxLegacyAddonName),
                Path.Combine(addonDir, AlexToolkitAddonName),
                Path.Combine(addonDir, Dlss5BridgeAddonName),
                Path.Combine(addonDir, Dlss5Dx11BridgeAddonName)
            }).Concat(new[]
            {
                // ShortFuse deployed under the upstream ZZZ load-order name is the same consumer.
                Path.Combine(binaryDir, ShortFuseDeploymentService.ZzzAddonFileName),
                Path.Combine(addonDir, ShortFuseDeploymentService.ZzzAddonFileName)
            }).Distinct(StringComparer.Ordinal);
}
