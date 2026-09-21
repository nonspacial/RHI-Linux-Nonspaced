using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

public sealed class DetailSectionLayoutStoreTests
{
    [Fact]
    public void Load_MissingFileUsesCompleteDefaultOrder()
    {
        using var temp = new TempDir();
        var store = new DetailSectionLayoutStore(Path.Combine(temp.Path, "ui-settings.json"));

        var settings = store.Load();

        Assert.Empty(settings.CollapsedSections);
        Assert.Equal(DetailSectionLayoutStore.DefaultSectionOrder, settings.SectionOrder);
    }

    [Fact]
    public void SaveAndLoad_PersistsCollapsedAndCustomOrder()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "ui-settings.json");
        var store = new DetailSectionLayoutStore(path);
        var expectedOrder = new[]
        {
            "Extras",
            "ManagedComponents",
            "Runtime",
            "DlssStreamline",
            "ReShadeProfile"
        };

        store.Save(new DetailSectionLayoutSettings
        {
            CollapsedSections = ["ReShadeProfile", "Extras"],
            SectionOrder = [.. expectedOrder]
        });

        var loaded = store.Load();
        Assert.Equal(new[] { "ReShadeProfile", "Extras" }, loaded.CollapsedSections);
        Assert.Equal(expectedOrder, loaded.SectionOrder);
    }

    [Fact]
    public void Normalize_DropsUnknownsAndDuplicatesAndAppendsMissingSections()
    {
        var normalized = DetailSectionLayoutStore.Normalize(new DetailSectionLayoutSettings
        {
            CollapsedSections = ["extras", "Unknown", "EXTRAS", "Runtime"],
            SectionOrder = ["extras", "Unknown", "runtime", "EXTRAS"]
        });

        Assert.Equal(new[] { "Extras", "Runtime" }, normalized.CollapsedSections);
        Assert.Equal(
            new[] { "Extras", "Runtime", "DlssStreamline", "ReShadeProfile", "ManagedComponents" },
            normalized.SectionOrder);
    }

    [Fact]
    public void Load_CorruptJsonFallsBackToDefaults()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "ui-settings.json");
        File.WriteAllText(path, "{ definitely-not-json");
        var store = new DetailSectionLayoutStore(path);

        var loaded = store.Load();

        Assert.Empty(loaded.CollapsedSections);
        Assert.Equal(DetailSectionLayoutStore.DefaultSectionOrder, loaded.SectionOrder);
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rhi-layout-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
