using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Hdr;

namespace RHI.Platform.Linux.Deployment;

/// <summary>
/// w11/nr-gpu: the host's NVIDIA facts every Neural Rendering decision reads (#229 card generation, #231 driver
/// version) — one seam, set once by the composition (the app: <see cref="NvidiaGpuProbe.Read()"/>, read-only /proc;
/// tests and the harness: a fake). Setting it also points the DLSS service's NR lists and "Latest" at the same card, so
/// every consumer follows one answer.
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
            if (_shortFuse?.Dlss is { } dlss) dlss.CardGeneration = () => facts().Generation;
        }
    }

    /// <summary>#229: the card generation NR lists and "Latest" follow (unknown = every entry, as upstream).</summary>
    public RtxGeneration CardGeneration => _shortFuse?.Dlss is { } dlss ? dlss.CardGeneration() : _nvidiaGpu().Generation;
}
