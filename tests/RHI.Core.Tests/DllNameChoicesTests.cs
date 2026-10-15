using System.Reflection;
using System.Text.Json;
using RHI.Core.Models;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

/// <summary>
/// w4/encore review minor 3: one game's entry in <c>dll-names.json</c> holds every component's choice; saving one choice
/// keeps every other one, including fields added later (generic copy) and fields a newer RHI wrote (kept unknown fields).
/// </summary>
public sealed class DllNameChoicesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-dllchoices-" + Guid.NewGuid().ToString("N"));
    private readonly DetectedGame _game;

    public DllNameChoicesTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "game"));
        _game = new DetectedGame { Name = "Choices", InstallPath = Path.Combine(_root, "game"), Source = "Test" };
    }

    private string File_ => Path.Combine(_root, "dll-names.json");

    private static IEnumerable<PropertyInfo> ChoiceProperties()
        => typeof(DllNameChoices).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanWrite && property.Name != nameof(DllNameChoices.Unknown));

    // A non-default value for every choice type; a new property type must be added here (the test throws otherwise).
    private static object SampleFor(PropertyInfo property) => property.PropertyType switch
    {
        var t when t == typeof(string) => "sample-" + property.Name,
        var t when t == typeof(bool?) => true,
        var t => throw new NotSupportedException("Add a sample for " + t + " (" + property.Name + ")")
    };

    private static DllNameChoices Full()
    {
        var full = new DllNameChoices();
        foreach (var property in ChoiceProperties()) property.SetValue(full, SampleFor(property));
        return full;
    }

    [Fact]
    public void Copy_CarriesEveryField_AndEachFieldAloneMakesTheEntryNonEmpty()
    {
        var full = Full();
        var copy = full.Copy();

        Assert.NotSame(full, copy);
        foreach (var property in ChoiceProperties())
            Assert.Equal(property.GetValue(full), property.GetValue(copy));
        Assert.True(new DllNameChoices().IsEmpty());
        Assert.True(new DllNameChoices { Unknown = [] }.IsEmpty());
        foreach (var property in ChoiceProperties())
        {
            var one = new DllNameChoices();
            property.SetValue(one, SampleFor(property));
            Assert.False(one.IsEmpty(), property.Name);
        }
    }

    [Fact]
    public void SavingOneChoice_KeepsEveryOther()
    {
        var store = new DllNamePreferenceStore(File_);
        store.SetReShade(_game, "dxgi.dll");
        store.SetRtxEncore(_game, "winmm.dll");
        store.SetRtxEncoreNeuralRendering(_game, true);

        store.SetReShade(_game, "d3d11.dll");
        Assert.Equal("winmm.dll", store.GetRtxEncore(_game));
        Assert.True(store.GetRtxEncoreNeuralRendering(_game));

        store.SetRtxEncore(_game, "dinput8.dll");
        Assert.Equal("d3d11.dll", store.GetReShade(_game));
        Assert.True(store.GetRtxEncoreNeuralRendering(_game));

        store.SetRtxEncoreNeuralRendering(_game, false);
        Assert.Equal("d3d11.dll", store.GetReShade(_game));
        Assert.Equal("dinput8.dll", store.GetRtxEncore(_game));

        var reread = new DllNamePreferenceStore(File_);
        Assert.Equal("d3d11.dll", reread.GetReShade(_game));
        Assert.Equal("dinput8.dll", reread.GetRtxEncore(_game));
        Assert.False(reread.GetRtxEncoreNeuralRendering(_game));
    }

    // Every field the entry holds (by reflection, so a choice another lane adds is covered too) and a field a newer RHI
    // wrote survive a save through each setter.
    [Theory]
    [InlineData("reshade")]
    [InlineData("rtxEncore")]
    [InlineData("rtxEncoreNeuralRendering")]
    public void EverySetter_KeepsEveryOtherStoredField_AndFieldsANewerRhiWrote(string changed)
    {
        new DllNamePreferenceStore(File_).SetReShade(_game, "dxgi.dll"); // creates the game's entry under its real key
        var map = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(File_))!;
        var key = map.Keys.Single();
        var full = Full();
        var raw = JsonSerializer.SerializeToNode(full)!.AsObject();
        raw["fromANewerRhi"] = "kept";
        File.WriteAllText(File_, new System.Text.Json.Nodes.JsonObject { [key] = raw }.ToJsonString());
        var store = new DllNamePreferenceStore(File_);

        switch (changed)
        {
            case "reshade": store.SetReShade(_game, "d3d11.dll"); break;
            case "rtxEncore": store.SetRtxEncore(_game, "winmm.dll"); break;
            default: store.SetRtxEncoreNeuralRendering(_game, false); break;
        }

        var saved = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(File_))![key];
        foreach (var property in ChoiceProperties())
        {
            var name = property.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()!.Name;
            if (name == changed) continue;
            Assert.Equal(JsonSerializer.Serialize(property.GetValue(full)), saved.GetProperty(name).GetRawText());
        }
        Assert.Equal("kept", saved.GetProperty("fromANewerRhi").GetString());
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
