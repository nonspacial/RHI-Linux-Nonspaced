using RHI.Core.Models;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

public sealed class LumaModePreferenceStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-luma-pref-" + Guid.NewGuid().ToString("N"));
    public LumaModePreferenceStoreTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void MissingStore_ReturnsAuto()
    {
        var store = new LumaModePreferenceStore(Path.Combine(_root, "missing.json"));
        Assert.Equal(LumaModePreference.Auto, store.Get(Game("One")));
    }

    [Fact]
    public void SetEnabled_PersistsByStableGameKey()
    {
        var path = Path.Combine(_root, "prefs.json");
        var game = Game("One");
        new LumaModePreferenceStore(path).Set(game, LumaModePreference.Enabled);

        Assert.Equal(LumaModePreference.Enabled, new LumaModePreferenceStore(path).Get(game));
    }

    [Fact]
    public void SetAuto_RemovesExplicitPreference()
    {
        var path = Path.Combine(_root, "prefs-auto.json");
        var game = Game("One");
        var store = new LumaModePreferenceStore(path);
        store.Set(game, LumaModePreference.Disabled);
        store.Set(game, LumaModePreference.Auto);

        Assert.Equal(LumaModePreference.Auto, new LumaModePreferenceStore(path).Get(game));
    }

    [Fact]
    public void CorruptJson_FailsClosedToAuto()
    {
        var path = Path.Combine(_root, "prefs-corrupt.json");
        File.WriteAllText(path, "{broken");
        var store = new LumaModePreferenceStore(path);

        Assert.Equal(LumaModePreference.Auto, store.Get(Game("One")));
    }

    private DetectedGame Game(string name)
    {
        var install = Path.Combine(_root, name);
        Directory.CreateDirectory(install);
        return new DetectedGame { Name = name, InstallPath = install, Source = "Test", Runner = "Wine" };
    }
}
