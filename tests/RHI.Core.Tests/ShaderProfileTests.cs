using System.IO.Compression;
using System.Text;
using System.Text.Json;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>#93: named shader-selection profiles, zip export (into RHI's own folder) and profile import.</summary>
[Collection(UiLanguageCollection.Name)]
public sealed class ShaderProfileTests : IDisposable
{
    private const string Custom = ReShadeShaderPackCatalog.CustomPackId;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-shader-profiles-" + Guid.NewGuid().ToString("N"));
    private readonly ReShadeShaderPackService _packs;
    private readonly List<string> _opened = [];

    private string CustomRoot => Path.Combine(_root, "custom");
    private string ExportFolder => Path.Combine(_root, "exports");
    private string StorePath => Path.Combine(_root, "config", "shader-profiles.json");

    public ShaderProfileTests()
    {
        Directory.CreateDirectory(_root);
        // Offline: a handler that refuses every request, so only the local Custom pack can stage.
        _packs = new ReShadeShaderPackService(new HttpClient(new Refuse()), Path.Combine(_root, "cache"), customRoot: CustomRoot);
        File.WriteAllText(Path.Combine(CustomRoot, "Shaders", "Keep.fx"), "keep");
        File.WriteAllText(Path.Combine(CustomRoot, "Shaders", "Skip.fx"), "skip");
        File.WriteAllText(Path.Combine(CustomRoot, "Textures", "lut.png"), "png");
    }

    public void Dispose()
    {
        UiLocalizationService.Apply("en-US");
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private ShaderProfilePresenter Presenter() => new(new ShaderSelectionProfileStore(StorePath), _packs, ExportFolder,
        () => new DateTime(2026, 10, 14, 12, 30, 0), folder => { _opened.Add(folder); return (true, null); });

    [Fact]
    public void Save_KeepsTheTickedPacksAndTheirFileChoices_LoadPutsBothBack()
    {
        var presenter = Presenter();
        _packs.SetExcludedFiles(Custom, ["Shaders/Skip.fx"]);

        var (line, saved) = presenter.Save("HDR evening", [Custom]);
        _packs.SetExcludedFiles(Custom, []); // the user changes the files afterwards
        var (selected, loaded) = presenter.Load("HDR evening");

        Assert.Equal("Profile \"HDR evening\" saved (1 packs).", line);
        Assert.Equal("HDR evening", saved);
        Assert.Equal([Custom], selected);
        Assert.Equal(["Shaders/Skip.fx"], _packs.GetExcludedFiles(Custom));
        Assert.StartsWith("Profile \"HDR evening\" loaded: 1 packs ticked.", loaded, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_SkipsPacksTheCatalogueNoLongerHas_AndSaysSo()
    {
        var presenter = Presenter();
        presenter.Save("Old", [Custom, "GonePack"]);

        var (selected, line) = presenter.Load("Old");

        Assert.Equal([Custom], selected);
        Assert.Contains("1 of the profile's packs are not in the catalogue and were skipped.", line, StringComparison.Ordinal);
    }

    [Fact]
    public void SameName_Overwrites_BlankName_IsNumbered_RenameAndDelete()
    {
        var presenter = Presenter();
        presenter.Save("A", [Custom]);
        Assert.Equal("Profile \"A\" updated (0 packs).", presenter.Save("a", []).Status);
        Assert.Equal("Profile 2", presenter.Save(null, [Custom]).Saved);
        Assert.Equal("A profile called \"A\" already exists.", presenter.Rename("Profile 2", "A"));
        Assert.Equal("Profile \"Profile 2\" renamed to \"Evening\".", presenter.Rename("Profile 2", "Evening"));
        Assert.Equal("Profile \"A\" deleted.", presenter.Delete("A"));

        Assert.Equal(["Evening"], new ShaderSelectionProfileStore(StorePath).Load().Select(p => p.Name));
    }

    [Fact]
    public async Task Export_ZipsTheProfilesDeployedFiles_WithTheProfile_InRhisOwnFolder_ThenOpensIt()
    {
        var presenter = Presenter();
        _packs.SetExcludedFiles(Custom, ["Shaders/Skip.fx"]);
        presenter.Save("Share me", [Custom]);

        var line = await presenter.ExportAsync("Share me", []);

        var zipPath = Path.Combine(ExportFolder, "RHI_Shaders_Share_me_2026-10-14_12-30-00.zip");
        Assert.Equal($"Zip exported: {zipPath}", line);
        using var zip = ZipFile.OpenRead(zipPath);
        Assert.Equal(["Shaders/__custom__/Keep.fx", "Textures/__custom__/lut.png", "shader_import.json"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        using var json = JsonDocument.Parse(new StreamReader(zip.GetEntry("shader_import.json")!.Open()).ReadToEnd());
        Assert.Equal("Share me", json.RootElement.GetProperty("Name").GetString());
        Assert.Equal(Custom, json.RootElement.GetProperty("SelectedPacks")[0].GetString());
        Assert.Equal([ExportFolder], _opened);
        Assert.Empty(Directory.EnumerateFiles(ExportFolder, "*.rhi-tmp-*"));
    }

    [Fact]
    public async Task Export_WithNothingTicked_WritesNothing()
    {
        Assert.Equal("Tick at least one pack or pick a profile to export.", await Presenter().ExportAsync(null, []));
        Assert.False(Directory.Exists(ExportFolder));
    }

    private string ProfileZip(string name, string profileJson, params (string Entry, string Content)[] extra)
    {
        var path = Path.Combine(_root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, content) in extra)
            using (var s = zip.CreateEntry(entry).Open()) s.Write(Encoding.UTF8.GetBytes(content));
        using (var s = zip.CreateEntry("shader_import.json").Open()) s.Write(Encoding.UTF8.GetBytes(profileJson));
        return path;
    }

    [Fact]
    public void Import_AddsOnlyTheProfile_InUpstreamsFormat_RenamedWhenTaken_AndUnpacksNoFiles()
    {
        var presenter = Presenter();
        presenter.Save("Friend", [Custom]);
        var zip = ProfileZip("friend.zip", """{"Name":"Friend","SelectedPacks":["__custom__","SweetFX"],"FileExclusions":{"SweetFX":["Bloom.fx"]}}""",
            ("Shaders/__custom__/Evil.fx", "x"), ("Shaders/ReShade.fxh", "y"));

        var (line, imported) = presenter.Import(zip);

        Assert.Equal("Friend (2)", imported);
        Assert.Equal("Profile \"Friend (2)\" imported (2 packs). Load it and use Apply.", line);
        var profile = new ShaderSelectionProfileStore(StorePath).Load().Single(p => p.Name == "Friend (2)");
        Assert.Equal(["Bloom.fx"], profile.FileExclusions["sweetfx"]);
        Assert.False(File.Exists(Path.Combine(CustomRoot, "Shaders", "Evil.fx")));
        Assert.False(File.Exists(Path.Combine(CustomRoot, "Shaders", "ReShade.fxh")));
    }

    [Fact]
    public void Import_ZipWithoutAProfile_InPortuguese_IsRefused()
    {
        UiLocalizationService.Apply("pt-BR");
        var path = Path.Combine(_root, "random.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) zip.CreateEntry("readme.txt");

        var (line, imported) = Presenter().Import(path);

        Assert.Null(imported);
        Assert.Equal("O perfil não pôde ser importado: Este zip não é um perfil de shaders (não tem shader_import.json).", line);
    }

    [Fact]
    public void UnreadableProfilesFile_IsNeverOverwritten()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, "{ not json");

        var (line, saved) = Presenter().Save("New", [Custom]);

        Assert.Null(saved);
        Assert.StartsWith("Not saved: shader-profiles.json could not", line, StringComparison.Ordinal);
        Assert.Equal("{ not json", File.ReadAllText(StorePath));
    }

    private sealed class Refuse : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("offline test");
    }
}
