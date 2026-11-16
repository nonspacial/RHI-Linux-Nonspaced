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

    // #255: the add-on in reshade.ini's AddonPath folder counts, also when that folder is outside the game tree.
    [Fact]
    public void ComponentProbe_FindsAddonsInTheAddonPathFolder()
    {
        var install = Path.Combine(_root, "game");
        var addons = Path.Combine(_root, "shared-addons");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(addons);
        File.WriteAllText(Path.Combine(addons, "renodx-universal_ue-dof-fix.addon64"), "x");
        File.WriteAllText(Path.Combine(addons, "renodx-mfgunlock.addon64"), "x");
        var game = new DetectedGame { Name = "Game", InstallPath = install, BinaryDirectory = install, Source = "Steam", Runner = "Proton" };

        var without = ComponentProbe.Probe(game, []);
        Assert.Equal(ComponentAvailability.Available, without.Single(x => x.Kind == ManagedComponentKind.DofFix).Availability);

        var with = ComponentProbe.Probe(game, [], addonDirectory: addons);
        Assert.Equal(ComponentAvailability.Installed, with.Single(x => x.Kind == ManagedComponentKind.DofFix).Availability);
        Assert.Equal(ComponentAvailability.Installed, with.Single(x => x.Kind == ManagedComponentKind.MfgAdaUnlock).Availability);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
