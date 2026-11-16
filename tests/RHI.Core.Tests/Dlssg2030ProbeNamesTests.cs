using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// w13 final review nit: the component row finds DLSSG 20/30 under every proxy name the 0.4 package installs (#261's
/// <see cref="Dlssg2030UnlockDeploymentService.ProxyNames"/>) and under winhttp.dll, which older installs still use.
/// </summary>
public sealed class Dlssg2030ProbeNamesTests
{
    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (var name in Dlssg2030UnlockDeploymentService.ProxyNames) data.Add(name);
        data.Add("winhttp.dll");
        return data;
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Probe_FindsTheModUnderEachProxyName(string proxy)
    {
        var bin = Path.Combine(Path.GetTempPath(), "rhi-dlssg-probe-" + Guid.NewGuid().ToString("N"));
        var game = new DetectedGame { Name = "G", InstallPath = bin, BinaryDirectory = bin, Source = "Manual" };

        var row = ComponentProbe.Probe(game, [Path.Combine(bin, "dlssg_sm86.ini"), Path.Combine(bin, proxy)])
            .Single(component => component.Kind == ManagedComponentKind.Dlssg2030Unlock);

        Assert.Equal(ComponentAvailability.Installed, row.Availability);
    }

    [Fact]
    public void TheDetectedNames_AreTheOfferedOnesPlusWinhttp()
        => Assert.Equal([.. Dlssg2030UnlockDeploymentService.ProxyNames, "winhttp.dll"], Dlssg2030ProxyNames.Detected);
}
