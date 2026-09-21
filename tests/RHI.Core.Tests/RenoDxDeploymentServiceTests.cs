using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class RenoDxDeploymentServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-renodx-deploy-tests-" + Guid.NewGuid().ToString("N"));
    public RenoDxDeploymentServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ResolveAddonDeployPath_UsesReshadeAddonPath()
    {
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "reshade.ini"), "[GENERAL]\nfoo=bar\n[ADDON]\nAddonPath=.\\reshade-addons\n");
        var result = RenoDxDeploymentService.ResolveAddonDeployPath(bin);
        Assert.Equal(Path.GetFullPath(Path.Combine(bin, "reshade-addons")), result);
    }

    [Fact]
    public void ResolveAddonDeployPath_FallsBackToBinaryDirectory()
    {
        Assert.Equal(_root, RenoDxDeploymentService.ResolveAddonDeployPath(_root));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
