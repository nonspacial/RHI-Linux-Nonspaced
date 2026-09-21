using System.Text;
using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Core.Tests;

public sealed class OptiScalerProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-optiscaler-probe-tests-" + Guid.NewGuid().ToString("N"));

    public OptiScalerProbeTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Probe_DetectsOptiScalerRenamedToDxgiAndCoexistingReShade64()
    {
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        var dxgi = Path.Combine(bin, "dxgi.dll");
        var reshade = Path.Combine(bin, "ReShade64.dll");
        WriteSignature(dxgi, "OptiScaler middleware");
        WriteSignature(reshade, "ReShade crosire reshade.me");
        var game = new DetectedGame { Name = "Game", InstallPath = _root, BinaryDirectory = bin, Runner = "Proton" };

        var result = ComponentProbe.Probe(game, [dxgi, reshade]);

        Assert.Equal(ComponentAvailability.Installed, result.Single(c => c.Kind == ManagedComponentKind.OptiScaler).Availability);
        Assert.Equal(ComponentAvailability.Installed, result.Single(c => c.Kind == ManagedComponentKind.ReShade).Availability);
    }

    [Fact]
    public void Probe_DoesNotTreatIniOrNestedOptiScalerNamedFileAsInstalledProxy()
    {
        var bin = Path.Combine(_root, "clean-bin");
        var nested = Path.Combine(bin, "OptiScaler");
        Directory.CreateDirectory(nested);
        var ini = Path.Combine(bin, "OptiScaler.ini");
        var nestedDll = Path.Combine(nested, "OptiScaler.dll");
        File.WriteAllText(ini, "[Plugins]\nLoadReshade=auto\n");
        WriteSignature(nestedDll, "OptiScaler middleware");
        var game = new DetectedGame { Name = "Game", InstallPath = _root, BinaryDirectory = bin, Runner = "Proton" };

        var result = ComponentProbe.Probe(game, [ini, nestedDll]);

        Assert.Equal(ComponentAvailability.Available, result.Single(c => c.Kind == ManagedComponentKind.OptiScaler).Availability);
    }

    private static void WriteSignature(string path, string signature)
    {
        var bytes = new byte[4096];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        Encoding.ASCII.GetBytes(signature).CopyTo(bytes, 128);
        File.WriteAllBytes(path, bytes);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
