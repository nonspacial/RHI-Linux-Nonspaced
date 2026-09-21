using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class RenoDxIniOverrideManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-renodx-ini-tests-" + Guid.NewGuid().ToString("N"));

    public RenoDxIniOverrideManagerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ApplyAndCleanup_NewSection_RemovesOnlyRhiOwnedContent()
    {
        var ini = Path.Combine(_root, "ReShade.ini");
        File.WriteAllText(ini, "[GENERAL]\nEffectSearchPaths=.\\reshade-shaders\\Shaders\\**\n");

        var result = RenoDxIniOverrideManager.Apply(
            _root,
            new Dictionary<string, string> { ["Set_Path"] = "1" });

        Assert.NotNull(result.State);
        Assert.Contains("[renodx]", File.ReadAllText(ini), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Set_Path=1", File.ReadAllText(ini));

        RenoDxIniOverrideManager.Cleanup(result.State);

        var final = File.ReadAllText(ini);
        Assert.DoesNotContain("[renodx]", final, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EffectSearchPaths=.\\reshade-shaders\\Shaders\\**", final);
    }

    [Fact]
    public void ApplyAndCleanup_PreexistingValue_RestoresExactPriorValue()
    {
        var ini = Path.Combine(_root, "reshade.ini");
        File.WriteAllText(ini, "[renodx]\nSet_Path=0\nKeepMe=abc\n");

        var result = RenoDxIniOverrideManager.Apply(
            _root,
            new Dictionary<string, string> { ["Set_Path"] = "1" });
        Assert.Contains("Set_Path=1", File.ReadAllText(ini));

        RenoDxIniOverrideManager.Cleanup(result.State);

        var final = File.ReadAllText(ini);
        Assert.Contains("Set_Path=0", final);
        Assert.Contains("KeepMe=abc", final);
    }

    [Fact]
    public void Cleanup_UserChangedManagedKey_PreservesUserValue()
    {
        var ini = Path.Combine(_root, "ReShade.ini");
        File.WriteAllText(ini, "[renodx]\nSet_Path=0\n");
        var result = RenoDxIniOverrideManager.Apply(
            _root,
            new Dictionary<string, string> { ["Set_Path"] = "1" });

        File.WriteAllText(ini, "[renodx]\nSet_Path=7\n");
        RenoDxIniOverrideManager.Cleanup(result.State);

        Assert.Contains("Set_Path=7", File.ReadAllText(ini));
    }

    [Fact]
    public void Apply_UpdateAfterUserEdit_DoesNotForceManifestAgain()
    {
        var ini = Path.Combine(_root, "ReShade.ini");
        File.WriteAllText(ini, "[renodx]\nSet_Path=0\n");
        var first = RenoDxIniOverrideManager.Apply(
            _root,
            new Dictionary<string, string> { ["Set_Path"] = "1" });

        File.WriteAllText(ini, "[renodx]\nSet_Path=7\n");
        var second = RenoDxIniOverrideManager.Apply(
            _root,
            new Dictionary<string, string> { ["Set_Path"] = "1" },
            first.State);

        Assert.Equal(1, second.PreservedUserEdits);
        Assert.Contains("Set_Path=7", File.ReadAllText(ini));
    }


    [Fact]
    public void Apply_UpdateAfterUserDeletesSection_DoesNotRecreateIt()
    {
        var ini = Path.Combine(_root, "ReShade.ini");
        File.WriteAllText(ini, "[renodx]\nSet_Path=0\n");
        var first = RenoDxIniOverrideManager.Apply(
            _root,
            new Dictionary<string, string> { ["Set_Path"] = "1" });

        File.WriteAllText(ini, "[GENERAL]\nEffectSearchPaths=.\\reshade-shaders\\Shaders\\**\n");
        var second = RenoDxIniOverrideManager.Apply(
            _root,
            new Dictionary<string, string> { ["Set_Path"] = "1" },
            first.State);

        Assert.Equal(1, second.PreservedUserEdits);
        Assert.DoesNotContain("[renodx]", File.ReadAllText(ini), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SerializeRoundTrip_PreservesRollbackAuthority()
    {
        var ini = Path.Combine(_root, "ReShade.ini");
        File.WriteAllText(ini, "[renodx]\nSet_Path=0\n");
        var applied = RenoDxIniOverrideManager.Apply(
            _root,
            new Dictionary<string, string> { ["Set_Path"] = "1" });

        var json = RenoDxIniOverrideManager.Serialize(applied.State!);
        var restoredState = RenoDxIniOverrideManager.Deserialize(json);
        RenoDxIniOverrideManager.Cleanup(restoredState);

        Assert.Contains("Set_Path=0", File.ReadAllText(ini));
    }

    [Fact]
    public void ResolveAddonDeployPath_RecognizesCanonicalUppercaseReShadeIni()
    {
        var addons = Path.Combine(_root, "reshade-addons");
        File.WriteAllText(Path.Combine(_root, "ReShade.ini"), "[ADDON]\nAddonPath=.\\reshade-addons\n");

        var resolved = RenoDxDeploymentService.ResolveAddonDeployPath(_root);

        Assert.Equal(Path.GetFullPath(addons), resolved);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
