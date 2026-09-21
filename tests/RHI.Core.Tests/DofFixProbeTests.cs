using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Core.Tests;

public sealed class DofFixProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-dof-probe-tests-" + Guid.NewGuid().ToString("N"));

    public DofFixProbeTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ComponentProbe_RequiresExactDofFixAddonNameInBinaryDirectory()
    {
        var bin = Path.Combine(_root, "bin");
        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(other);
        var game = new DetectedGame
        {
            Name = "Game",
            InstallPath = _root,
            BinaryDirectory = bin,
            Source = "Steam",
            Runner = "Proton"
        };
        var noise = Path.Combine(bin, "my-dof-notes.addon64");
        var exactElsewhere = Path.Combine(other, "renodx-universal_ue-dof-fix.addon64");
        File.WriteAllText(noise, "x");
        File.WriteAllText(exactElsewhere, "x");

        var first = ComponentProbe.Probe(game, [noise, exactElsewhere]);
        Assert.Equal(ComponentAvailability.Available, first.Single(x => x.Kind == ManagedComponentKind.DofFix).Availability);

        var exact = Path.Combine(bin, "renodx-universal_ue-dof-fix.addon64");
        File.WriteAllText(exact, "x");
        var second = ComponentProbe.Probe(game, [noise, exactElsewhere, exact]);
        Assert.Equal(ComponentAvailability.Installed, second.Single(x => x.Kind == ManagedComponentKind.DofFix).Availability);
        Assert.Equal(ComponentAvailability.Available, second.Single(x => x.Kind == ManagedComponentKind.RenoDX).Availability);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
