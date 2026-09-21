using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

public sealed class ReShadeIniManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-reshade-ini-tests-" + Guid.NewGuid().ToString("N"));

    public ReShadeIniManagerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Apply_WhenMissing_CreatesConservativeBaseline()
    {
        var result = ReShadeIniManager.Apply(
            _root,
            [@".\reshade-shaders\RHI\CrosireMaster\Shaders\**"],
            [@".\reshade-shaders\RHI\CrosireMaster\Textures\**"]);

        var text = File.ReadAllText(result.Path);
        Assert.True(result.CreatedByRhi);
        Assert.Contains("[ADDON]", text);
        Assert.Contains("DisabledAddons=Generic Depth,Effect Runtime Sync", text);
        Assert.Contains(@"EffectSearchPaths=.\reshade-shaders\RHI\CrosireMaster\Shaders\**", text);
        Assert.Contains(@"TextureSearchPaths=.\reshade-shaders\RHI\CrosireMaster\Textures\**", text);
        Assert.Contains("TutorialProgress=4", text);
    }

    [Fact]
    public void Apply_ExistingIni_PreservesUserSettingsAndIsIdempotent()
    {
        var path = Path.Combine(_root, "ReShade.ini");
        File.WriteAllText(path,
            "; user comment\n[GENERAL]\nEffectSearchPaths=.\\user\\Shaders\\**\nTextureSearchPaths=.\\user\\Textures\\**\n[OVERLAY]\nKeyOverlay=36,0,0,0\n");
        var effect = @".\reshade-shaders\RHI\CrosireMaster\Shaders\**";
        var texture = @".\reshade-shaders\RHI\CrosireMaster\Textures\**";

        var first = ReShadeIniManager.Apply(_root, [effect], [texture]);
        var afterFirst = File.ReadAllText(path);
        var second = ReShadeIniManager.Apply(_root, [effect], [texture]);
        var afterSecond = File.ReadAllText(path);

        Assert.False(first.CreatedByRhi);
        Assert.Equal(afterFirst, afterSecond);
        Assert.Empty(second.AddedEffectPaths);
        Assert.Empty(second.AddedTexturePaths);
        Assert.Contains("; user comment", afterSecond);
        Assert.Contains("KeyOverlay=36,0,0,0", afterSecond);
        Assert.Contains(@".\user\Shaders\**", afterSecond);
        Assert.Contains(effect, afterSecond);
    }

    [Fact]
    public void Cleanup_PreexistingIni_RemovesOnlyRhiPaths()
    {
        var path = Path.Combine(_root, "ReShade.ini");
        File.WriteAllText(path,
            "[GENERAL]\nEffectSearchPaths=.\\user\\Shaders\\**\nTextureSearchPaths=.\\user\\Textures\\**\n[OVERLAY]\nUserSetting=keep\n");
        var effect = @".\reshade-shaders\RHI\CrosireMaster\Shaders\**";
        var texture = @".\reshade-shaders\RHI\CrosireMaster\Textures\**";
        var applied = ReShadeIniManager.Apply(_root, [effect], [texture]);

        ReShadeIniManager.Cleanup(
            path,
            applied.CreatedByRhi,
            applied.Sha256,
            applied.AddedEffectPaths,
            applied.AddedTexturePaths,
            applied.EffectKeyCreated,
            applied.TextureKeyCreated);

        var text = File.ReadAllText(path);
        Assert.Contains(@"EffectSearchPaths=.\user\Shaders\**", text);
        Assert.Contains(@"TextureSearchPaths=.\user\Textures\**", text);
        Assert.Contains("UserSetting=keep", text);
        Assert.DoesNotContain(@"reshade-shaders\RHI", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cleanup_RhiCreatedUnchangedIni_DeletesIt()
    {
        var applied = ReShadeIniManager.Apply(_root, [], []);

        ReShadeIniManager.Cleanup(
            applied.Path,
            applied.CreatedByRhi,
            applied.Sha256,
            applied.AddedEffectPaths,
            applied.AddedTexturePaths,
            applied.EffectKeyCreated,
            applied.TextureKeyCreated);

        Assert.False(File.Exists(applied.Path));
    }

    [Fact]
    public void Cleanup_RhiCreatedButUserModifiedIni_PreservesUserEdit()
    {
        var effect = @".\reshade-shaders\RHI\CrosireMaster\Shaders\**";
        var applied = ReShadeIniManager.Apply(_root, [effect], []);
        File.AppendAllText(applied.Path, "\n[USER]\nKeepMe=1\n");

        ReShadeIniManager.Cleanup(
            applied.Path,
            applied.CreatedByRhi,
            applied.Sha256,
            applied.AddedEffectPaths,
            applied.AddedTexturePaths,
            applied.EffectKeyCreated,
            applied.TextureKeyCreated);

        Assert.True(File.Exists(applied.Path));
        var text = File.ReadAllText(applied.Path);
        Assert.Contains("KeepMe=1", text);
        Assert.DoesNotContain(effect, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_LowercaseExistingIniIsReusedWithoutCreatingCaseDuplicate()
    {
        var lowercase = Path.Combine(_root, "reshade.ini");
        File.WriteAllText(lowercase, "[GENERAL]\nEffectSearchPaths=.\\user\\Shaders\\**\n");

        var result = ReShadeIniManager.Apply(
            _root,
            [@".\reshade-shaders\RHI\Test\Shaders\**"],
            []);

        Assert.Equal(lowercase, result.Path);
        Assert.False(result.CreatedByRhi);
        Assert.True(File.Exists(lowercase));
        Assert.False(File.Exists(Path.Combine(_root, "ReShade.ini")));
        Assert.Contains(@".\reshade-shaders\RHI\Test\Shaders\**", File.ReadAllText(lowercase));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
