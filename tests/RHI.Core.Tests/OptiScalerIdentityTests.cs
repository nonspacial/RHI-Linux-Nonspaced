using RHI.Core.Models;
using RHI.Core.Services;

namespace RHI.Core.Tests;

/// <summary>
/// #154 (upstream 2.8.6/2.8.7 7920ae6, 5031d97): OptiScaler is identified by its PE version resource
/// (<c>FileDescription = OptiScaler</c>, <c>CompanyName = nitec</c>, as both real builds checked carry: OptiScaler 0.9.4
/// and F5 DLSSNR 0.1.30); a file with a resource naming another tool is never OptiScaler, and only a file without a
/// resource is read for strings. The RTX-Encore-like fixture copies what its real proxies carry (version resource
/// CompanyName SilyNoMeta, "OptiScaler" mentioned in the body).
/// </summary>
public sealed class OptiScalerIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-optiscaler-identity-" + Guid.NewGuid().ToString("N"));

    public OptiScalerIdentityTests() => Directory.CreateDirectory(_root);

    /// <summary>OptiScaler's own version resource (+ optional body text, e.g. the "ReShade" its real DLL carries).</summary>
    internal static byte[] OptiScalerDll(string? bodyText = null, int size = 300_000)
        => PeVersionReaderTests.BuildVersionedPe("0.9.4.0", "0.9.4-final", size,
            extraStrings: [("CompanyName", "nitec"), ("FileDescription", "OptiScaler"), ("ProductName", "OptiScaler")], bodyText: bodyText);

    /// <summary>A tool that mentions OptiScaler, with its own version resource (RTX Encore's proxies).</summary>
    internal static byte[] RtxEncoreLikeDll(string bodyText = "Disable OptiScaler before loading RTX Encore. optiscaler/OptiScaler OptiScaler_DontLoad")
        => PeVersionReaderTests.BuildVersionedPe("1.0.0.0", "1.0.0-rc.2", 300_000,
            extraStrings: [("CompanyName", "SilyNoMeta"), ("FileDescription", "RTX Encore for RTX 20, 30 and 40")], bodyText: bodyText);

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void VersionResourceNamingOptiScaler_IsOptiScaler_WithoutAnyString()
        => Assert.True(BinarySignatureInspector.LooksLikeOptiScaler(Write("a.dll", OptiScalerDll())));

    /// <summary>Both fields, as upstream: a description alone (any tool can call itself that) is not OptiScaler's resource.</summary>
    [Fact]
    public void VersionResourceWithOptiScalerDescriptionButAnotherCompany_IsNotOptiScaler()
        => Assert.False(BinarySignatureInspector.LooksLikeOptiScaler(Write("b.dll", PeVersionReaderTests.BuildVersionedPe("1.0.0.0", null, 8192,
            extraStrings: [("CompanyName", "SomeoneElse"), ("FileDescription", "OptiScaler")], bodyText: "optiscaler/OptiScaler"))));

    [Fact]
    public void VersionResourceNamingAnotherTool_IsNotOptiScaler_EvenWithOptiScalerStrings()
        => Assert.False(BinarySignatureInspector.LooksLikeOptiScaler(Write("winmm.dll", RtxEncoreLikeDll())));

    [Fact]
    public void WithoutVersionResource_OnlyOptiScalersOwnStringsCount()
    {
        var mention = new byte[8192];
        mention[0] = (byte)'M'; mention[1] = (byte)'Z';
        System.Text.Encoding.ASCII.GetBytes("works with OptiScaler; see OptiScaler.ini").CopyTo(mention, 512);
        var own = (byte[])mention.Clone();
        System.Text.Encoding.ASCII.GetBytes("https://github.com/optiscaler/OptiScaler/wiki").CopyTo(own, 1024);

        Assert.False(BinarySignatureInspector.LooksLikeOptiScaler(Write("mention.dll", mention)));
        Assert.True(BinarySignatureInspector.LooksLikeOptiScaler(Write("own.dll", own)));
    }

    /// <summary>OptiScaler's real DLL mentions "ReShade" (it can load it); its version resource rules ReShade out.</summary>
    [Fact]
    public void OptiScalerCarryingReShadeText_IsNotReShade()
    {
        var path = Write("dxgi.dll", OptiScalerDll("LoadReshade: load ReShade64.dll"));

        Assert.True(BinarySignatureInspector.LooksLikeOptiScaler(path));
        Assert.False(BinarySignatureInspector.LooksLikeReShade(path));
    }

    [Fact]
    public void ReShadeWithItsStrings_IsStillReShade()
    {
        var path = Write("dxgi.dll", PeVersionReaderTests.BuildVersionedPe("6.4.1.0", "6.4.1", 300_000,
            extraStrings: [("CompanyName", "crosire"), ("FileDescription", "ReShade")], bodyText: "ReShade crosire reshade.me"));

        Assert.True(BinarySignatureInspector.LooksLikeReShade(path));
        Assert.False(BinarySignatureInspector.LooksLikeOptiScaler(path));
    }

    [Theory]
    [InlineData("winmm.dll")]
    [InlineData("dxgi.dll")]
    [InlineData("version.dll")]
    [InlineData("d3d11.dll")]
    public void Probe_RtxEncoreProxy_IsNoOptiScalerBadge_ButRealOptiScalerIs(string proxy)
    {
        var encoreBin = Path.Combine(_root, "encore-" + proxy);
        var optiBin = Path.Combine(_root, "opti-" + proxy);
        Directory.CreateDirectory(encoreBin);
        Directory.CreateDirectory(optiBin);
        var encore = Path.Combine(encoreBin, proxy);
        var opti = Path.Combine(optiBin, proxy);
        File.WriteAllBytes(encore, RtxEncoreLikeDll());
        File.WriteAllBytes(opti, OptiScalerDll());

        var withEncore = ComponentProbe.Probe(new DetectedGame { Name = "G", InstallPath = encoreBin, BinaryDirectory = encoreBin, Runner = "Proton" }, [encore]);
        var withOpti = ComponentProbe.Probe(new DetectedGame { Name = "G", InstallPath = optiBin, BinaryDirectory = optiBin, Runner = "Proton" }, [opti]);

        Assert.Equal(ComponentAvailability.Available, withEncore.Single(c => c.Kind == ManagedComponentKind.OptiScaler).Availability);
        Assert.Equal(ComponentAvailability.Installed, withOpti.Single(c => c.Kind == ManagedComponentKind.OptiScaler).Availability);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
