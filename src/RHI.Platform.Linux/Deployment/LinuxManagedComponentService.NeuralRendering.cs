using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Platform.Linux.Deployment;

public sealed partial class LinuxManagedComponentService
{
    /// <summary>
    /// What a changed Neural Rendering add-on pin does to the installed add-on: nothing yet (not installed: the pin
    /// applies at install), an in-place swap, or nothing until a reinstall — a legacy add-on-only DLSS5 Feeder cannot
    /// be swapped in place, so its pin is kept for the next install instead of failing and reverting (#115).
    /// </summary>
    public NeuralPinChange PlanNeuralPinChange(DetectedGame game, ManagedComponentKind kind)
    {
        if (!IsManaged(game, kind)) return NeuralPinChange.SaveOnly;
        if (kind == ManagedComponentKind.Dlss5Feeder && !IsDlss5FeederFullManaged(game)) return NeuralPinChange.ReinstallNeeded;
        return NeuralPinChange.SwapInPlace;
    }

    public bool IsDlss5FeederFullManaged(DetectedGame game)
    {
        var record = _records.Find(game, ManagedComponentKind.Dlss5Feeder);
        return record is not null && record.Metadata.TryGetValue("Feeder.FullStack", out var value) && value == "1";
    }

    public async Task<ComponentOperationResult> InstallDlss5FeederAsync(
        DetectedGame game,
        Dlss5FeederConsumer consumer,
        string? deepFriedChickenPackagePath,
        string? d3dCompiler47Path,
        CancellationToken cancellationToken = default)
    {
        var guard = InstallGuard(game); // merge review nit 4: the same bitness/encrypted-exe answer as every installer (#167)
        if (guard is not null) return guard;
        if (NrCostScalerService.OwnerChangeRefusal(_records, game, ManagedComponentKind.Dlss5Feeder) is { } layered) return ComponentOperationResult.Fail(layered); // w3/parity (#96)

        var preflight = FeederLayoutPlanner.ValidateTarget(game, consumer, requireReShade: false);
        if (preflight is not null) return preflight;
        if (consumer == Dlss5FeederConsumer.DeepFriedChicken &&
            (string.IsNullOrWhiteSpace(deepFriedChickenPackagePath) || !File.Exists(deepFriedChickenPackagePath)))
            return ComponentOperationResult.Fail("DLSS5 Feeder Full com Deep Fried Chicken exige o pacote oficial .zip ou .7z antes que dependências sejam alteradas.");
        if (consumer == Dlss5FeederConsumer.DeepFriedChicken &&
            !DeepFriedChickenPackageVerifier.IsSupportedDeepFriedChickenArchive(deepFriedChickenPackagePath!))
            return ComponentOperationResult.Fail("O pacote consumidor do Deep Fried Chicken deve ser um arquivo oficial .zip ou .7z.");

        return await Track(game, ManagedComponentKind.Dlss5Feeder, ComponentUpdateAction.Install, () => InstallWithManagedDependenciesAsync(
            game,
            ManagedComponentKind.Dlss5Feeder,
            indexedFiles: null,
            async () =>
            {
                var compiler = await EnsureNativeD3DCompilerDependencyAsync(game, d3dCompiler47Path, "DLSS5 Feeder", cancellationToken).ConfigureAwait(false);
                if (compiler is not null) return compiler;

                var cleanup = await RemoveConflictingNeuralRenderingMethodsAsync(game, ManagedComponentKind.Dlss5Feeder, cancellationToken).ConfigureAwait(false);
                if (cleanup is not null) return cleanup;
                var pin = _neuralVersionPins.Get(game, ManagedComponentKind.Dlss5Feeder);
                return await _dlss5Feeder.InstallAsync(game, consumer, deepFriedChickenPackagePath, d3dCompiler47Path, pin,
                    _neuralVersionPins.GetNeuralRenderingDllVersion(game), cancellationToken).ConfigureAwait(false);
            },
            cancellationToken)).ConfigureAwait(false);
    }

    public Task<ComponentOperationResult> InstallDlss5FeederAsync(
        DetectedGame game,
        string? deepFriedChickenPackagePath,
        string? d3dCompiler47Path,
        CancellationToken cancellationToken = default)
        => InstallDlss5FeederAsync(game, Dlss5FeederConsumer.DeepFriedChicken, deepFriedChickenPackagePath, d3dCompiler47Path, cancellationToken);

    public string? GetNeuralAddonVersionPin(DetectedGame game, ManagedComponentKind kind)
        => _neuralVersionPins.Get(game, kind);

    public void SetNeuralAddonVersionPin(DetectedGame game, ManagedComponentKind kind, string? releaseTag)
        => _neuralVersionPins.Set(game, kind, releaseTag);

    public string? GetNeuralRenderingDllVersionPin(DetectedGame game)
        => _neuralVersionPins.GetNeuralRenderingDllVersion(game);

    public void SetNeuralRenderingDllVersionPin(DetectedGame game, string? version)
        => _neuralVersionPins.SetNeuralRenderingDllVersion(game, version);

    /// <summary>
    /// Swaps the nvngx_dlssnr.dll owned by the installed ShortFuse or DLSS5 Feeder Full stack to the
    /// stored NR DLL pin (or the newest manifest version). DX11 Bridge owns no NR DLL on Linux; its
    /// game's own DLL stays under the regular DLSS swap row.
    /// </summary>
    public Task<ComponentOperationResult> SwapNeuralRenderingDllAsync(
        DetectedGame game,
        CancellationToken cancellationToken = default)
    {
        var guard = PatchTargetGuard(game);
        if (guard is not null) return Task.FromResult(guard);
        if (NrCostScalerService.OwnerChangeRefusal(_records, game, ManagedComponentKind.ShortFuse) is { } layered) return Task.FromResult(ComponentOperationResult.Fail(layered)); // w3/parity (#96)
        var version = _neuralVersionPins.GetNeuralRenderingDllVersion(game);
        if (IsShortFuseManaged(game))
            return TrackNeuralRenderingDll(game, ManagedComponentKind.ShortFuse,
                () => _shortFuse.SwapNeuralRenderingDllAsync(game, version, cancellationToken));
        if (IsDlss5FeederFullManaged(game))
            return TrackNeuralRenderingDll(game, ManagedComponentKind.Dlss5Feeder,
                () => _dlss5Feeder.SwapNeuralRenderingDllAsync(game, version, cancellationToken));
        return Task.FromResult(ComponentOperationResult.Fail("No ShortFuse or DLSS5 Feeder Full stack owns nvngx_dlssnr.dll for this game; the version applies at the next install."));
    }

    /// <summary>
    /// Brings an installed Neural Rendering add-on to its stored pin (or latest) by replacing only
    /// the add-on file(s) in place; the rest of the method's stack is untouched (upstream RHI 2.7.6).
    /// </summary>
    public Task<ComponentOperationResult> SwapNeuralAddonVersionAsync(
        DetectedGame game,
        ManagedComponentKind kind,
        CancellationToken cancellationToken = default)
    {
        // #170: a swap would put a loadable copy back (DX11 Bridge) or miss the renamed file (Feeder, ShortFuse) while
        // No Addons has the add-on turned off; it is refused like an update, before any download.
        var guard = PatchTargetGuard(game) ?? AddonOffGuard(game, kind);
        if (guard is not null) return Task.FromResult(guard);
        if (kind is not (ManagedComponentKind.Dlss5Feeder or ManagedComponentKind.Dlss5Dx11Bridge or ManagedComponentKind.ShortFuse))
            return SwapNeuralAddonVersionCoreAsync(game, kind, cancellationToken);
        // Logged like Update All's NR updates (which reach the same core through UpdateManagedAsync).
        return Track(game, kind, ComponentUpdateAction.Update, () => SwapNeuralAddonVersionCoreAsync(game, kind, cancellationToken));
    }

    // The NR DLL version lives in the owning record's metadata, not in its Version, so the plain
    // ledger diff in Track would miss it. A record from before #230 has no version there (Borderlands 3's Feeder): its
    // "from" is the version told just before the swap (record hash vs cache stamp, else PE version; see #230's helper).
    private Task<ComponentOperationResult> TrackNeuralRenderingDll(
        DetectedGame game,
        ManagedComponentKind owner,
        Func<Task<ComponentOperationResult>> swap)
    {
        if (_history is null) return swap();
        var unrecordedFrom = _records.Find(game, owner) is { } before && RecordedNrVersion(before) is null
            ? InstalledNeuralRenderingVersion(game, before).Version
            : null;
        return _history.TrackOwnedFileAsync(game, owner, NeuralRenderingDllSwap.DllName,
            record => RecordedNrVersion(record) ?? unrecordedFrom,
            swap);
    }

    private static string? RecordedNrVersion(LinuxDeploymentRecord record)
        => record.Metadata.TryGetValue(NeuralRenderingDllSwap.MetaDllVersion, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    private Task<ComponentOperationResult> SwapNeuralAddonVersionCoreAsync(
        DetectedGame game,
        ManagedComponentKind kind,
        CancellationToken cancellationToken)
        => kind switch
        {
            ManagedComponentKind.Dlss5Feeder => _dlss5Feeder.SwapFeederVersionAsync(game, _neuralVersionPins.Get(game, kind), cancellationToken),
            ManagedComponentKind.Dlss5Dx11Bridge => _dlss5Bridge.UpdateAsync(game, _neuralVersionPins.Get(game, kind), cancellationToken),
            ManagedComponentKind.ShortFuse => _shortFuse.SwapAddonVersionAsync(game, _neuralVersionPins.Get(game, kind), cancellationToken),
            _ => Task.FromResult(ComponentOperationResult.Fail($"{kind} is not a Neural Rendering add-on."))
        };

    public Task<IReadOnlyList<GitHubReleaseVersion>> ListNeuralAddonVersionsAsync(
        ManagedComponentKind kind,
        CancellationToken cancellationToken = default)
        => kind switch
        {
            ManagedComponentKind.Dlss5Feeder => _dlss5Feeder.ListVersionsAsync(cancellationToken),
            ManagedComponentKind.Dlss5Dx11Bridge => _dlss5Bridge.ListVersionsAsync(cancellationToken),
            ManagedComponentKind.ShortFuse => _shortFuse.ListVersionsAsync(cancellationToken),
            _ => Task.FromException<IReadOnlyList<GitHubReleaseVersion>>(new ArgumentOutOfRangeException(nameof(kind)))
        };

    public LinuxDeploymentRecord? GetShortFuseRecord(DetectedGame game) => _shortFuse.GetRecord(game);

    public bool IsShortFuseManaged(DetectedGame game) => _shortFuse.IsManaged(game);

    public bool GetShortFuseAutoConfigEnabled(DetectedGame game) => _shortFuse.GetAutoConfigEnabled(game);

    public void SetShortFuseAutoConfigEnabled(DetectedGame game, bool enabled) => _shortFuse.SetAutoConfigEnabled(game, enabled);

    public bool GetShortFuseZzzLoadOrderEnabled(DetectedGame game) => _shortFuse.GetZzzLoadOrderEnabled(game);

    /// <summary>ZZZ state as ReShade will see it: the owned add-on's on-disk name when managed, else the preference.</summary>
    public bool GetShortFuseZzzLoadOrderInEffect(DetectedGame game) => _shortFuse.GetZzzLoadOrderInEffect(game);

    public Task<ComponentOperationResult> SetShortFuseZzzLoadOrderAsync(DetectedGame game, bool enabled, CancellationToken cancellationToken = default)
    {
        // #170: the rename needs the add-on under its own name; while No Addons has it turned off nothing changes.
        var guard = PatchTargetGuard(game) ?? AddonOffGuard(game, ManagedComponentKind.ShortFuse);
        return guard is null ? _shortFuse.SetZzzLoadOrderAsync(game, enabled, cancellationToken) : Task.FromResult(guard);
    }

    public Task<ShortFuseUpdateStatus> CheckShortFuseUpdateAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => _shortFuse.CheckLatestAsync(game, _neuralVersionPins.Get(game, ManagedComponentKind.ShortFuse), cancellationToken);

    public Task<ShortFuseUpdateStatus> CheckShortFuseLatestReleaseAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => _shortFuse.CheckLatestAsync(game, cancellationToken);

    public async Task<ComponentOperationResult> InstallShortFuseAsync(DetectedGame game, bool autoConfigEnabled = false, CancellationToken cancellationToken = default)
    {
        var guard = InstallGuard(game);
        if (guard is not null) return guard;
        if (NrCostScalerService.OwnerChangeRefusal(_records, game, ManagedComponentKind.ShortFuse) is { } layered) return ComponentOperationResult.Fail(layered); // w3/parity (#96)
        return await Track(game, ManagedComponentKind.ShortFuse, ComponentUpdateAction.Install, () => InstallWithManagedDependenciesAsync(
            game,
            ManagedComponentKind.ShortFuse,
            indexedFiles: null,
            async () =>
            {
                var cleanup = await RemoveConflictingNeuralRenderingMethodsAsync(game, ManagedComponentKind.ShortFuse, cancellationToken).ConfigureAwait(false);
                if (cleanup is not null) return cleanup;
                return await _shortFuse.InstallAsync(game, autoConfigEnabled, _neuralVersionPins.Get(game, ManagedComponentKind.ShortFuse),
                    _neuralVersionPins.GetNeuralRenderingDllVersion(game), cancellationToken).ConfigureAwait(false);
            },
            cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// The Extras ShortFuse Remove: the generic removal itself, so both Remove buttons give one answer. Removal is the
    /// user's own choice and works while No Addons has the add-on turned off (#170; it deletes the off file).
    /// </summary>
    public Task<ComponentOperationResult> RemoveShortFuseAsync(DetectedGame game, CancellationToken cancellationToken = default)
        => RemoveAsync(game, ManagedComponentKind.ShortFuse, cancellationToken);

    private async Task<ComponentOperationResult> InstallNeuralRenderingManagedAsync(
        DetectedGame game,
        ManagedComponentKind target,
        CancellationToken cancellationToken)
    {
        if (target == ManagedComponentKind.ShortFuse && _records.Find(game, ManagedComponentKind.Dlssg2030Unlock) is not null)
            return ComponentOperationResult.Fail("RTX 20 FG Unlock legado já está ativo. Remova-o antes de instalar ShortFuse.");
        if (target == ManagedComponentKind.ShortFuse && _records.Find(game, ManagedComponentKind.Rtx40MfgStandalone) is not null)
            return ComponentOperationResult.Fail("Universal MFG Experimental já está ativo. Remova-o antes de instalar ShortFuse.");
        if (target == ManagedComponentKind.Dlss5Feeder)
        {
            var compiler = await EnsureNativeD3DCompilerDependencyAsync(
                game,
                explicitCompilerPath: null,
                requiredBy: "DLSS5 Feeder",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (compiler is not null) return compiler;
        }

        var cleanup = await RemoveConflictingNeuralRenderingMethodsAsync(game, target, cancellationToken).ConfigureAwait(false);
        if (cleanup is not null) return cleanup;

        return target switch
        {
            ManagedComponentKind.Dlss5Feeder => await _dlss5Feeder.InstallAsync(
                game,
                Dlss5FeederConsumer.RenoDxDlss5,
                deepFriedChickenPackagePath: null,
                d3dCompiler47Path: null,
                releaseTag: _neuralVersionPins.Get(game, ManagedComponentKind.Dlss5Feeder),
                neuralRenderingDllVersion: _neuralVersionPins.GetNeuralRenderingDllVersion(game),
                cancellationToken: cancellationToken).ConfigureAwait(false),
            ManagedComponentKind.Dlss5Dx11Bridge => await _dlss5Bridge.InstallAsync(game, _neuralVersionPins.Get(game, ManagedComponentKind.Dlss5Dx11Bridge), cancellationToken).ConfigureAwait(false),
            ManagedComponentKind.ShortFuse => await _shortFuse.InstallAsync(game, _shortFuse.GetAutoConfigEnabled(game), _neuralVersionPins.Get(game, ManagedComponentKind.ShortFuse),
                _neuralVersionPins.GetNeuralRenderingDllVersion(game), cancellationToken).ConfigureAwait(false),
            _ => ComponentOperationResult.Fail($"{target} is not a Neural Rendering method.")
        };
    }

    private async Task<ComponentOperationResult?> RemoveConflictingNeuralRenderingMethodsAsync(
        DetectedGame game,
        ManagedComponentKind target,
        CancellationToken cancellationToken)
    {
        var conflicts = NeuralRenderingConflictsFor(target);

        foreach (var conflict in conflicts)
        {
            if (_records.Find(game, conflict) is null) continue;
            var result = conflict switch
            {
                ManagedComponentKind.ShortFuse => await _shortFuse.RemoveAsync(game, cancellationToken).ConfigureAwait(false),
                ManagedComponentKind.Dlss5Feeder => await _dlss5Feeder.RemoveAsync(game, cancellationToken).ConfigureAwait(false),
                ManagedComponentKind.Dlss5Dx11Bridge => await _dlss5Bridge.RemoveAsync(game, cancellationToken).ConfigureAwait(false),
                _ => ComponentOperationResult.Ok("No conflicting method.")
            };

            if (!result.Success)
                return ComponentOperationResult.Fail($"Neural Rendering method switch aborted while removing {DisplayNeuralRenderingMethod(conflict)}: {result.Message}");
        }

        return null;
    }

    public bool HasNativeDlssForNeuralRendering(DetectedGame game, bool dlssDetectedNow)
        => InferNativeDlssBeforeManagedNeuralRendering(
            dlssDetectedNow,
            _records.Find(game, ManagedComponentKind.ShortFuse),
            _records.Find(game, ManagedComponentKind.Dlss5Feeder));

    internal static bool InferNativeDlssBeforeManagedNeuralRendering(
        bool dlssDetectedNow,
        LinuxDeploymentRecord? shortFuseRecord,
        LinuxDeploymentRecord? feederRecord)
    {
        if (!dlssDetectedNow) return false;

        // ShortFuse and Feeder can create nvngx_dlss.dll themselves. Picker/recommendation
        // policy must reflect the game's capability before the active managed method, not
        // the payload injected by that method. ExistedBeforeInstall is authoritative when
        // the managed record tracks the SR DLL. If no matching file is tracked while a
        // managed method is active, fail safe as native/external rather than assuming the
        // current DLL was created by RHI.
        foreach (var record in new[] { shortFuseRecord, feederRecord })
        {
            if (record is null) continue;
            var tracked = record.Files.FirstOrDefault(file =>
                !string.IsNullOrWhiteSpace(file.Path) &&
                Path.GetFileName(file.Path).Equals("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase));
            if (tracked is not null) return tracked.ExistedBeforeInstall;
            return true;
        }

        return true;
    }

    internal static IReadOnlyList<ManagedComponentKind> NeuralRenderingConflictsFor(ManagedComponentKind target) => target switch
    {
        ManagedComponentKind.ShortFuse => new[] { ManagedComponentKind.Dlss5Feeder, ManagedComponentKind.Dlss5Dx11Bridge },
        ManagedComponentKind.Dlss5Feeder => new[] { ManagedComponentKind.ShortFuse, ManagedComponentKind.Dlss5Dx11Bridge },
        ManagedComponentKind.Dlss5Dx11Bridge => new[] { ManagedComponentKind.ShortFuse, ManagedComponentKind.Dlss5Feeder },
        _ => Array.Empty<ManagedComponentKind>()
    };

    /// <summary>
    /// Mirrors the upstream RHI Neural Rendering default selection policy. The recommendation
    /// does not bypass Linux eligibility/ownership gates; it only identifies the upstream
    /// preferred method for the selected game's observed capabilities.
    /// </summary>
    public static ManagedComponentKind RecommendedNeuralRenderingMethod(DetectedGame game, bool hasNativeDlss)
    {
        var architecture = GameArchitecture.Of(game);
        if (architecture == PeArchitecture.X86)
            return ManagedComponentKind.Dlss5Feeder;
        if (!hasNativeDlss)
            return ManagedComponentKind.Dlss5Feeder;
        return game.EffectiveGraphicsApi is GraphicsApi.DirectX11 or GraphicsApi.Vulkan
            ? ManagedComponentKind.Dlss5Dx11Bridge
            : ManagedComponentKind.ShortFuse;
    }

    private static string DisplayNeuralRenderingMethod(ManagedComponentKind kind) => kind switch
    {
        ManagedComponentKind.ShortFuse => "ShortFuse",
        ManagedComponentKind.Dlss5Feeder => "DLSS5 Feeder",
        ManagedComponentKind.Dlss5Dx11Bridge => "DLSS5 DX11 Bridge",
        _ => kind.ToString()
    };
}

/// <summary>Outcome of changing a Neural Rendering add-on pin (see <see cref="LinuxManagedComponentService.PlanNeuralPinChange"/>).</summary>
public enum NeuralPinChange
{
    /// <summary>Not installed: the pin is used by the next install.</summary>
    SaveOnly,
    /// <summary>Installed: the add-on is swapped to the pinned version now.</summary>
    SwapInPlace,
    /// <summary>Installed as a legacy add-on-only DLSS5 Feeder: the pin applies once it is removed and installed again.</summary>
    ReinstallNeeded
}
