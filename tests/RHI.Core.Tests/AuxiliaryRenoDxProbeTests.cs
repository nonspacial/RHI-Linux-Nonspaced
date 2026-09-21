using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Core.Tests;

public sealed class AuxiliaryRenoDxProbeTests
{
    [Theory]
    [InlineData("renodx-unreal.addon64", true, false)]
    [InlineData("renodx-dlss5.addon64", false, false)]
    [InlineData("renodx-mfgunlock.addon64", false, true)]
    public void AuxiliaryRenoDxAddons_DoNotMasqueradeAsPrimaryRenoDx(
        string fileName,
        bool expectedRenoDx,
        bool expectedMfgUnlock)
    {
        var root = Path.Combine(Path.GetTempPath(), "rhi-aux-renodx-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, fileName);
            File.WriteAllBytes(path, [0x4D, 0x5A]);
            var game = new DetectedGame
            {
                Name = "Probe",
                InstallPath = root,
                BinaryDirectory = root,
                Runner = "Proton"
            };

            var components = ComponentProbe.Probe(game, [path]);
            Assert.Equal(expectedRenoDx,
                components.Single(c => c.Kind == ManagedComponentKind.RenoDX).Availability == ComponentAvailability.Installed);
            Assert.Equal(expectedMfgUnlock,
                components.Single(c => c.Kind == ManagedComponentKind.MfgAdaUnlock).Availability == ComponentAvailability.Installed);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
