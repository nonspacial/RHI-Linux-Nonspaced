using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #158 detection: a real RTX Encore proxy (v1.0.0-rc.3: version resource CompanyName SilyNoMeta, FileDescription
/// "RTX Encore for RTX 20, 30 and 40"; its body mentions ReShade 3× and OptiScaler) is RTX Encore, never ReShade
/// (it was read as an external ReShade under dxgi/d3d9/d3d11/opengl32 and blocked RHI's ReShade) and never OptiScaler.
/// </summary>
public sealed class RtxEncoreIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-encore-identity-" + Guid.NewGuid().ToString("N"));

    public RtxEncoreIdentityTests() => Directory.CreateDirectory(_root);

    /// <summary>What a real RTX Encore build carries: its own resource, ReShade and OptiScaler mentioned in the body.</summary>
    internal static byte[] RtxEncoreDll(string version = "1.0.0-rc.3", int size = 300_000)
        => PeVersionReaderTests.BuildVersionedPe(version, version, size,
            extraStrings: [("CompanyName", "SilyNoMeta"), ("FileDescription", "RTX Encore for RTX 20, 30 and 40"), ("ProductName", "RTX Encore")],
            bodyText: "ReShade add-on loaded ReShade reshade.me OptiScaler flip metering bypass");

    private string Write(string folder, string name, byte[] bytes)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Theory]
    [InlineData("dxgi.dll")]
    [InlineData("d3d11.dll")]
    [InlineData("version.dll")]
    [InlineData("winmm.dll")]
    public void RtxEncoreProxy_IsRtxEncore_NotReShade_NotOptiScaler(string name)
    {
        var path = Write("a-" + name, name, RtxEncoreDll());

        Assert.True(BinarySignatureInspector.LooksLikeRtxEncore(path));
        Assert.False(BinarySignatureInspector.LooksLikeReShade(path));
        Assert.False(BinarySignatureInspector.LooksLikeOptiScaler(path));
        Assert.False(ProxyDllNames.IsReShadeOnDisk(path));
    }

    [Fact]
    public void ReShadeAndOptiScaler_AreNotRtxEncore()
    {
        var reshade = Write("r", "dxgi.dll", PeVersionReaderTests.BuildVersionedPe("6.4.1.0", "6.4.1", 300_000,
            extraStrings: [("CompanyName", "crosire"), ("FileDescription", "ReShade")], bodyText: "ReShade crosire reshade.me"));
        var opti = Write("o", "dxgi.dll", OptiScalerIdentityTests.OptiScalerDll());

        Assert.False(BinarySignatureInspector.LooksLikeRtxEncore(reshade));
        Assert.True(BinarySignatureInspector.LooksLikeReShade(reshade));
        Assert.False(BinarySignatureInspector.LooksLikeRtxEncore(opti));
    }

    /// <summary>Both fields: another SilyNoMeta file, or a file merely calling itself RTX Encore, is not RTX Encore.</summary>
    [Fact]
    public void BothResourceFieldsAreNeeded()
    {
        var other = Write("x", "version.dll", PeVersionReaderTests.BuildVersionedPe("1.0.0.0", null, 8192,
            extraStrings: [("CompanyName", "Someone"), ("FileDescription", "RTX Encore for RTX 20, 30 and 40")]));
        var none = Write("n", "version.dll", PeVersionReaderTests.BuildVersionedPe("1.0.0.0", null, 8192,
            extraStrings: [("CompanyName", "SilyNoMeta"), ("FileDescription", "Something else")]));

        Assert.False(BinarySignatureInspector.LooksLikeRtxEncore(other));
        Assert.False(BinarySignatureInspector.LooksLikeRtxEncore(none));
    }

    /// <summary>The library row: RTX Encore installed (external) and no ReShade, whichever name it has.</summary>
    [Theory]
    [InlineData("dxgi.dll", null)]
    [InlineData("d3d11.dll", null)]
    [InlineData("version.dll", null)]
    [InlineData("rtx-encore.asi", "plugins")]
    [InlineData("rtx-encore.asi", null)]
    public void Probe_RtxEncore_IsItsOwnRow_NotReShade(string name, string? sub)
    {
        var bin = Path.Combine(_root, "game-" + name + sub);
        var path = Write(Path.Combine("game-" + name + sub, sub ?? ""), name, RtxEncoreDll());

        var rows = ComponentProbe.Probe(new DetectedGame { Name = "G", InstallPath = bin, BinaryDirectory = bin, Runner = "Proton" }, [path]);

        Assert.Equal(ComponentAvailability.Installed, rows.Single(c => c.Kind == ManagedComponentKind.RtxEncore).Availability);
        Assert.Equal(ComponentAvailability.Available, rows.Single(c => c.Kind == ManagedComponentKind.ReShade).Availability);
        Assert.Equal(ComponentAvailability.Available, rows.Single(c => c.Kind == ManagedComponentKind.OptiScaler).Availability);
    }

    /// <summary>Encore's name list alone is no evidence: a plain version.dll is not RTX Encore.</summary>
    [Fact]
    public void Probe_ForeignFileUnderAnEncoreName_IsNotRtxEncore()
    {
        var bin = Path.Combine(_root, "plain");
        var path = Write("plain", "version.dll", PeVersionReaderTests.BuildVersionedPe("10.0.0.0", null, 8192,
            extraStrings: [("CompanyName", "Microsoft Corporation"), ("FileDescription", "Version Checking and File Installation Libraries")]));

        var rows = ComponentProbe.Probe(new DetectedGame { Name = "G", InstallPath = bin, BinaryDirectory = bin, Runner = "Proton" }, [path]);

        Assert.Equal(ComponentAvailability.Available, rows.Single(c => c.Kind == ManagedComponentKind.RtxEncore).Availability);
    }

    /// <summary>One ReShade per game (#120) no longer counts an RTX Encore proxy as "another ReShade".</summary>
    [Fact]
    public void FindReShadeOnDisk_SkipsRtxEncore()
    {
        var bin = Path.Combine(_root, "one");
        Write("one", "d3d11.dll", RtxEncoreDll());

        Assert.Null(ProxyPayloadLocator.FindReShadeOnDisk(bin));
        Assert.EndsWith("d3d11.dll", RtxEncoreNames.FindOnDisk(bin));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
