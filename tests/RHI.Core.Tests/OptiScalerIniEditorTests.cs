using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class OptiScalerIniEditorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-optiscaler-ini-tests-" + Guid.NewGuid().ToString("N"));

    public OptiScalerIniEditorTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void SetLoadReShade_ReplacesExistingAutoValue()
    {
        var path = Path.Combine(_root, "OptiScaler.ini");
        File.WriteAllText(path, "[Plugins]\nLoadReshade=auto\nLoadSpecialK=auto\n");

        OptiScalerIniEditor.SetLoadReShade(_root, true);

        var text = File.ReadAllText(path);
        Assert.Contains("LoadReshade=true", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LoadReshade=auto", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LoadSpecialK=auto", text);
    }

    [Fact]
    public void SetLoadReShade_InsertsValueIntoExistingPluginsSection()
    {
        var path = Path.Combine(_root, "OptiScaler.ini");
        File.WriteAllText(path, "[Plugins]\nLoadSpecialK=auto\n\n[Other]\nX=1\n");

        OptiScalerIniEditor.SetLoadReShade(_root, true);

        var text = File.ReadAllText(path);
        var plugins = text.IndexOf("[Plugins]", StringComparison.OrdinalIgnoreCase);
        var loader = text.IndexOf("LoadReshade=true", StringComparison.OrdinalIgnoreCase);
        var other = text.IndexOf("[Other]", StringComparison.OrdinalIgnoreCase);
        Assert.True(plugins >= 0 && loader > plugins && other > loader);
    }

    [Fact]
    public void SetLoadReShade_CreatesPluginsSectionWhenMissingAndCanDisableAgain()
    {
        var path = Path.Combine(_root, "OptiScaler.ini");
        File.WriteAllText(path, "[Upscalers]\nDx12Upscaler=auto\n");

        OptiScalerIniEditor.SetLoadReShade(_root, true);
        OptiScalerIniEditor.SetLoadReShade(_root, false);

        var text = File.ReadAllText(path);
        Assert.Contains("[Plugins]", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LoadReshade=false", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LoadReshade=true", text, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
