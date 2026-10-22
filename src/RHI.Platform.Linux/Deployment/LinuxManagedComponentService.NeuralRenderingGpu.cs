using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Hdr;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// w11/nr-gpu: the host's NVIDIA facts every Neural Rendering decision reads (#229 card generation, #231 driver
/// version) — one seam, set once by the composition (the app: <see cref="NvidiaGpuProbe.Read()"/>, read-only /proc;
/// tests and the harness: a fake). Setting it also points the DLSS service's NR lists and "Latest" at the same card, so
/// every consumer follows one answer. #230: Update All moves "Latest" NR installs of ShortFuse and DLSS5 Feeder Full onto
/// the DLL made for the card; a hand-picked version stays, with a row note.
/// </summary>
public sealed partial class LinuxManagedComponentService
{
    private Func<NvidiaGpuFacts> _nvidiaGpu = static () => NvidiaGpuFacts.None;

    /// <summary>
    /// The NVIDIA facts (#229/#231). Default (never set): no NVIDIA facts — unknown card, no driver — so a service built
    /// without the composition never reads the real /proc and never hides or moves anything because of the card.
    /// </summary>
    public Func<NvidiaGpuFacts> NvidiaGpu
    {
        get => _nvidiaGpu;
        set
        {
            var facts = value ?? (static () => NvidiaGpuFacts.None);
            _nvidiaGpu = facts;
            if (NrDlss is { } dlss) dlss.CardGeneration = () => facts().Generation;
        }
    }

    /// <summary>#229: the card generation NR lists and "Latest" follow (unknown = every entry, as upstream).</summary>
    public RtxGeneration CardGeneration => NrDlss is { } dlss ? dlss.CardGeneration() : _nvidiaGpu().Generation;

    // The one DLSS service every NR owner stages from (the app and the fixtures build one).
    private DlssManagerService? NrDlss => _shortFuse?.Dlss ?? _dlss5Feeder?.Dlss;

    // ---- #230: Update All moves "Latest" NR installs --------------------------------------------------------------

    /// <summary>What Update All does with an owner's NR DLL.</summary>
    internal enum NrMoveKind
    {
        /// <summary>Nothing to judge: no NR DLL owned, a legacy add-on-only Feeder, a hand-picked pin, or an unknown card.</summary>
        NotApplicable,
        /// <summary>The installed DLL is "Latest" made for this card.</summary>
        Current,
        /// <summary>A "Latest" install on another DLL: Update All swaps it to <see cref="NrMovePlan.To"/>.</summary>
        Move,
        /// <summary>RHI cannot tell which version is installed (or there is no manifest): could not check, never a blind swap.</summary>
        Unknown
    }

    internal sealed record NrMovePlan(NrMoveKind Kind, string? From = null, string? To = null);

    /// <summary>#230: fixed text of the update check's NR part; the versions follow (from → to).</summary>
    public const string NrMovePrefix = "The Neural Rendering DLL follows \"Latest\" and is not the one made for this graphics card; Update All moves it: ";

    /// <summary>#230: fixed text when the installed NR DLL cannot be told (never "current", never a blind swap).</summary>
    public const string NrUnknownMessage = "RHI could not tell which Neural Rendering DLL is installed, so it was not checked against the one made for this graphics card.";

    /// <summary>#230: the row note of a hand-picked NR version made for another generation; the version follows.</summary>
    public const string NrPinOtherGenerationPrefix = "The Neural Rendering DLL picked for this game is made for another graphics card generation; Update All leaves it as picked: ";

    internal async Task<NrMovePlan> PlanNeuralRenderingMoveAsync(DetectedGame game, ManagedComponentKind owner, CancellationToken cancellationToken)
    {
        if (owner is not (ManagedComponentKind.ShortFuse or ManagedComponentKind.Dlss5Feeder) || NrDlss is not { } dlss)
            return new(NrMoveKind.NotApplicable);
        var record = _records.Find(game, owner);
        if (record is null || ManagedFileSwap.FindFile(record, NeuralRenderingDllSwap.DllName) is null
            || (owner == ManagedComponentKind.Dlss5Feeder && !IsDlss5FeederFullManaged(game)))
            return new(NrMoveKind.NotApplicable);
        // A version the user picked by hand is never changed behind their back (its row says so when it is for another card).
        if (!string.IsNullOrWhiteSpace(_neuralVersionPins.GetNeuralRenderingDllVersion(game))) return new(NrMoveKind.NotApplicable);
        // Unknown card: nothing moves because of the card (today's behaviour).
        var card = dlss.CardGeneration();
        if (card == RtxGeneration.Unknown) return new(NrMoveKind.NotApplicable);
        try
        {
            await dlss.FetchManifestAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            AppLog.Warn("nr-gpu", $"The DLSS manifest could not be read to check {game.Name}'s Neural Rendering DLL", ex);
            return new(NrMoveKind.Unknown);
        }
        var latest = dlss.LatestVersion(DlssComponentKind.NeuralRendering);
        if (latest is null) return new(NrMoveKind.Unknown);
        var installed = InstalledNeuralRenderingVersion(game, record);
        if (!installed.Known) return new(NrMoveKind.Unknown);
        return string.Equals(installed.Version, latest, StringComparison.OrdinalIgnoreCase)
            ? new(NrMoveKind.Current, installed.Version, latest)
            : new(NrMoveKind.Move, installed.Version, latest);
    }

    /// <summary>#230 (one helper, #234 reuses it): the manifest version of the owner's installed NR DLL.</summary>
    public InstalledNrVersion InstalledNeuralRenderingVersion(DetectedGame game, LinuxDeploymentRecord record)
        => NrDlss is { } dlss
            ? NeuralRenderingInstalledVersion.Identify(record, dlss.PublishedVersions(DlssComponentKind.NeuralRendering),
                dlss.CacheFolder(DlssComponentKind.NeuralRendering), _records.FindAll(game))
            : new(null, InstalledNrVersionSource.Unknown);

    /// <summary>
    /// The ShortFuse / DLSS5 Feeder update check with its NR DLL part: the add-on part as before; a "Latest" install on
    /// another DLL than "Latest" made for this card is an update; an NR DLL RHI cannot tell is "could not check" unless
    /// the add-on itself has an update.
    /// </summary>
    private async Task<ManagedUpdateCheck> WithNeuralRenderingMoveAsync(DetectedGame game, ManagedComponentKind owner, ManagedUpdateCheck addon,
        CancellationToken cancellationToken)
    {
        if (!addon.Managed) return addon;
        var plan = await PlanNeuralRenderingMoveAsync(game, owner, cancellationToken).ConfigureAwait(false);
        return plan.Kind switch
        {
            NrMoveKind.Move => addon with
            {
                UpdateAvailable = true,
                CouldNotCheck = false,
                Message = Join(addon.UpdateAvailable ? addon.Message : "", NrMovePrefix + $"{plan.From} → {plan.To}")
            },
            NrMoveKind.Unknown when !addon.UpdateAvailable => addon with { CouldNotCheck = true, Message = Join(addon.Message, NrUnknownMessage) },
            _ => addon
        };
    }

    private static string Join(string first, string second)
        => string.IsNullOrWhiteSpace(first) ? second : first.TrimEnd() + " " + second;

    /// <summary>
    /// The ShortFuse / DLSS5 Feeder update (Update All, manual and background): the add-on as before, and a "Latest" NR
    /// install moved onto "Latest" made for this card through the owner's journaled in-place swap (logged in the update
    /// history like a hand-made NR swap). Every download happens before the first write: the NR DLL is staged first, the
    /// add-on swap downloads before it writes, and the NR swap then finds the staged copy.
    /// </summary>
    private async Task<ComponentOperationResult> UpdateNeuralRenderingOwnerAsync(DetectedGame game, ManagedComponentKind owner,
        CancellationToken cancellationToken)
    {
        var plan = await PlanNeuralRenderingMoveAsync(game, owner, cancellationToken).ConfigureAwait(false);
        if (plan.Kind != NrMoveKind.Move) return await SwapNeuralAddonVersionCoreAsync(game, owner, cancellationToken).ConfigureAwait(false);

        try
        {
            await NrDlss!.EnsureCachedVersionAsync(DlssComponentKind.NeuralRendering, plan.To, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return ComponentOperationResult.Fail(NrStageFailedPrefix + ex.Message);
        }

        var pin = _neuralVersionPins.Get(game, owner);
        var addonCheck = owner == ManagedComponentKind.ShortFuse
            ? await _shortFuse.CheckManagedUpdateAsync(game, pin, cancellationToken).ConfigureAwait(false)
            : await _dlss5Feeder.CheckUpdateAsync(game, pin, cancellationToken).ConfigureAwait(false);
        ComponentOperationResult? addon = null;
        if (addonCheck.UpdateAvailable)
        {
            addon = await SwapNeuralAddonVersionCoreAsync(game, owner, cancellationToken).ConfigureAwait(false);
            if (!addon.Success) return addon;
        }

        // Once the add-on swap has written, this unit runs to the end: the NR swap never sees a Cancel pressed after it
        // (its DLL is staged above, so nothing is downloaded from here on).
        var nrToken = addon is null ? cancellationToken : CancellationToken.None;
        var nr = await TrackNeuralRenderingDll(game, owner, () => owner == ManagedComponentKind.ShortFuse
            ? _shortFuse.SwapNeuralRenderingDllAsync(game, plan.To, nrToken)
            : _dlss5Feeder.SwapNeuralRenderingDllAsync(game, plan.To, nrToken)).ConfigureAwait(false);
        if (addon is null) return nr;
        // A landed line keeps its note first (LandedLine reads it from the start of the message).
        return nr with
        {
            Message = nr.Landed ? Join(nr.Message, addon.Message) : Join(addon.Message, nr.Message),
            Landed = nr.Landed || addon.Landed
        };
    }

    /// <summary>#230: the NR DLL could not be downloaded before the move; nothing was changed. The reason follows.</summary>
    public const string NrStageFailedPrefix = "The Neural Rendering DLL made for this graphics card could not be downloaded, so nothing was changed: ";

    /// <summary>The component rows' Neural Rendering notes (w11/nr-gpu), added after every other note.</summary>
    private void DecorateNeuralRenderingRows(DetectedGame game, IReadOnlyList<ManagedComponent> components)
        => _rows.DecorateNeuralRendering(components, _neuralVersionPins.GetNeuralRenderingDllVersion(game), CardGeneration);
}
