using System.Net;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #279 (upstream 2.8.9 / 2.9.2): MFG Unlock (formerly MFG Ada Unlock) — the row name, the ⚙'s frame generation method
/// and DLSS-SR input in MFGAdaUnlock.ini, Intel's XeSS files in the game folder with the game's own copies backed up and
/// restored, and the graphics-API warning.
/// </summary>
public sealed class MfgUnlockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-mfgu-" + Guid.NewGuid().ToString("N"));

    public MfgUnlockTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private string Bin(string name) => Directory.CreateDirectory(Path.Combine(_root, name, "bin")).FullName;

    // The ⚙ rows' writers (the app passes the managed service's journaled writes, w14 final review M5).
    private static Func<MfgUnlockMethod, ComponentOperationResult> WriteMethod(string bin)
        => method => { MfgUnlockIni.WriteMethod(bin, method); return ComponentOperationResult.Ok(""); };

    private static Func<bool, ComponentOperationResult> WriteDlssSr(string bin)
        => on => { MfgUnlockIni.WriteDlssSr(bin, on); return ComponentOperationResult.Ok(""); };

    [Fact]
    public void RowIsCalledMfgUnlock_InternalKeyUnchanged()
    {
        var game = new DetectedGame { Name = "Row", InstallPath = Bin("row"), BinaryDirectory = Path.Combine(_root, "row", "bin"), Source = "Test" };
        var row = ComponentProbe.Probe(game, []).Single(c => c.Kind == ManagedComponentKind.MfgAdaUnlock);
        Assert.Equal("MFG Unlock", row.DisplayName);
        Assert.Equal(5, (int)ManagedComponentKind.MfgAdaUnlock);
    }

    // Upstream's keys and values (BuildMfgAdaCogContent): [Backend] Selected + [DLSSG] XmxGenerator, [Adapters] DlssSr.
    [Fact]
    public void Ini_MethodAndDlssSr_AsUpstreamWritesThem_KeepingTheRest()
    {
        var bin = Bin("ini");
        Assert.Equal(MfgUnlockMethod.DlssG, MfgUnlockIni.ReadMethod(bin));
        Assert.False(MfgUnlockIni.ReadDlssSr(bin));
        File.WriteAllText(MfgUnlockIni.PathIn(bin), "; written by the mod\n[Backend]\nSelected=DLSSG\nOther=7\n");

        MfgUnlockIni.WriteMethod(bin, MfgUnlockMethod.XeMfgCuda);
        var lines = IniLines.Read(MfgUnlockIni.PathIn(bin));
        Assert.Equal("XeMFG", IniLines.Get(lines, "Backend", "Selected"));
        Assert.Equal("0", IniLines.Get(lines, "DLSSG", "XmxGenerator"));
        Assert.Equal("7", IniLines.Get(lines, "Backend", "Other"));
        Assert.Contains("; written by the mod", lines);
        Assert.Equal(MfgUnlockMethod.XeMfgCuda, MfgUnlockIni.ReadMethod(bin));

        MfgUnlockIni.WriteMethod(bin, MfgUnlockMethod.XeMfgViaDlssG);
        lines = IniLines.Read(MfgUnlockIni.PathIn(bin));
        Assert.Equal(("DLSSG", "1"), (IniLines.Get(lines, "Backend", "Selected"), IniLines.Get(lines, "DLSSG", "XmxGenerator")));
        Assert.Equal(MfgUnlockMethod.XeMfgViaDlssG, MfgUnlockIni.ReadMethod(bin));

        MfgUnlockIni.WriteDlssSr(bin, true);
        Assert.Equal("1", IniLines.Get(IniLines.Read(MfgUnlockIni.PathIn(bin)), "Adapters", "DlssSr"));
        Assert.True(MfgUnlockIni.ReadDlssSr(bin));
    }

    [Fact]
    public void ApiSupport_Dx12OrDualApiOk_VulkanExperimental_OlderNot()
    {
        DetectedGame Game(GraphicsApi primary, params GraphicsApi[] all) => new() { Name = "api", InstallPath = _root, Source = "Test", GraphicsApi = primary, GraphicsApis = all };
        Assert.Equal(MfgUnlockApiSupport.Supported, MfgUnlockIni.ApiSupport(Game(GraphicsApi.DirectX12)));
        Assert.Equal(MfgUnlockApiSupport.Supported, MfgUnlockIni.ApiSupport(Game(GraphicsApi.DirectX11, GraphicsApi.DirectX11, GraphicsApi.DirectX12)));
        Assert.Equal(MfgUnlockApiSupport.VulkanExperimental, MfgUnlockIni.ApiSupport(Game(GraphicsApi.Vulkan)));
        Assert.Equal(MfgUnlockApiSupport.NotSupported, MfgUnlockIni.ApiSupport(Game(GraphicsApi.DirectX11)));
        Assert.Equal(MfgUnlockApiSupport.Supported, MfgUnlockIni.ApiSupport(Game(GraphicsApi.Unknown)));
        Assert.Equal("DirectX 11", FgSettingsBuilder.ApiName(Game(GraphicsApi.DirectX11)));
    }

    private (IntelXessFiles Xess, byte[] Fg, byte[] Xell) FakeXess()
    {
        var fgPath = Path.Combine(_root, "fg.dll");
        var xellPath = Path.Combine(_root, "xell.dll");
        PeArchitectureDetectorTests.WritePe(fgPath, 0x8664, 3000);
        PeArchitectureDetectorTests.WritePe(xellPath, 0x8664, 3001);
        var fg = File.ReadAllBytes(fgPath);
        var xell = File.ReadAllBytes(xellPath);
        var http = new HttpClient(new Handler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith("libxess_fg.dll", StringComparison.Ordinal) ? fg : xell),
            RequestMessage = request
        }));
        var xess = new IntelXessFiles(http, Path.Combine(_root, "cache"))
        {
            FrameGenerationFiles =
            [
                IntelXessFiles.LibXessFg with { Size = fg.Length, Sha256 = FileHash.Sha256Hex(fgPath) },
                IntelXessFiles.LibXell with { Size = xell.Length, Sha256 = FileHash.Sha256Hex(xellPath) }
            ]
        };
        return (xess, fg, xell);
    }

    private static DetectedGame GameIn(string bin) => new() { Name = "Of Ash and Steel " + Path.GetFileName(Path.GetDirectoryName(bin)), InstallPath = Path.GetDirectoryName(bin)!, BinaryDirectory = bin, Source = "Test" };

    private static LinuxDeploymentRecord AddonRecord(DetectedGame game, string bin) => new()
    {
        GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.MfgAdaUnlock, BinaryDirectory = bin,
        Version = "1.10.0", Files = [new LinuxDeployedFile { Path = Path.Combine(bin, "renodx-mfgunlock.addon64"), Sha256 = "00" }]
    };

    // "If the game ships its own older copies (for example Of Ash and Steel), RHI backs them up and puts them back when you
    // remove the mod" (upstream 2.8.9); the files RHI added go on removal.
    [Fact]
    public async Task Files_BackUpTheGamesCopies_AndPutThemBack()
    {
        var bin = Bin("oas");
        var game = GameIn(bin);
        File.WriteAllText(Path.Combine(bin, "libxell.dll"), "the game's libxell 1.3.0.4");
        var records = new LinuxDeploymentRecordStore(Path.Combine(_root, "oas", "deployments.json"));
        records.Upsert(AddonRecord(game, bin));
        var (xess, fg, xell) = FakeXess();
        var files = new MfgUnlockFiles(xess, records, journal: null);

        Assert.True(files.NeedsFiles(records.Find(game, ManagedComponentKind.MfgAdaUnlock)!));
        var placed = await files.PlaceAsync(game);

        Assert.StartsWith(MfgUnlockFiles.PlacedMessage, placed, StringComparison.Ordinal);
        Assert.Equal(fg, File.ReadAllBytes(Path.Combine(bin, "libxess_fg.dll")));
        Assert.Equal(xell, File.ReadAllBytes(Path.Combine(bin, "libxell.dll")));
        Assert.Equal("the game's libxell 1.3.0.4", File.ReadAllText(Path.Combine(bin, "libxell.dll.mfgada-original")));
        Assert.False(files.NeedsFiles(records.Find(game, ManagedComponentKind.MfgAdaUnlock)!));
        Assert.All(files.State(bin), file => Assert.Equal(IntelXessFileState.Accepted, file.State));
        Assert.Equal("", await files.PlaceAsync(game)); // done once; never placed again by an update

        var (refusal, _) = await files.RestoreAsync(game);

        Assert.Null(refusal);
        Assert.Equal("the game's libxell 1.3.0.4", File.ReadAllText(Path.Combine(bin, "libxell.dll")));
        Assert.False(File.Exists(Path.Combine(bin, "libxell.dll.mfgada-original")));
        Assert.False(File.Exists(Path.Combine(bin, "libxess_fg.dll")));
    }

    // An update rebuilds the add-on's record: the XeSS list goes with it, so removal still knows what to put back.
    [Fact]
    public void ARebuiltRecord_KeepsTheXessList()
    {
        var previous = new LinuxDeploymentRecord();
        IntelXessFiles.Save(previous.Metadata, "MfgUnlock", [new IntelXessEntry { File = "libxell.dll", Path = "/g/libxell.dll", Sha256 = "AA" }]);
        var rebuilt = new LinuxDeploymentRecord();
        IntelXessFiles.CarryMetadata(previous, rebuilt);
        Assert.Equal("/g/libxell.dll", Assert.Single(IntelXessFiles.Load(rebuilt.Metadata, "MfgUnlock")).Path);
    }

    [Fact]
    public void Settings_MethodThenDlssSr_WriteTheIni_AndDlssSrNeedsXeMfgCuda()
    {
        var bin = Bin("cog");
        var game = new DetectedGame { Name = "Cog", InstallPath = bin, BinaryDirectory = bin, Source = "Test", GraphicsApi = GraphicsApi.DirectX12 };
        string? shown = null;
        var content = FgSettingsBuilder.MfgUnlock(game, bin, [], installed: true, line => shown = line, WriteMethod(bin), WriteDlssSr(bin));
        var (method, dlssSr) = (content.Sections[0].Rows[0], content.Sections[0].Rows[1]);
        foreach (var row in content.Sections[0].Rows) { row.Changed = line => shown = line; row.Refresh(); }

        Assert.Equal(UiLocalizationService.Text("Win.FrameGeneration"), content.Sections[0].Title);
        Assert.Equal(FgSettingsBuilder.MfgUnlockMethods, method.Options);
        Assert.False(dlssSr.IsEnabled);
        Assert.False(content.Files[0].Exists);

        method.SelectedIndex = (int)MfgUnlockMethod.XeMfgCuda;
        foreach (var row in content.Sections[0].Rows) row.Refresh();

        Assert.Equal(UiLocalizationService.Text("Fg.Saved"), shown);
        Assert.Equal(MfgUnlockMethod.XeMfgCuda, MfgUnlockIni.ReadMethod(bin));
        Assert.True(dlssSr.IsEnabled);
        dlssSr.SelectedIndex = 1;
        Assert.True(MfgUnlockIni.ReadDlssSr(bin));
        content.Files[0].Refresh();
        Assert.True(content.Files[0].Exists);
        Assert.Equal(UiLocalizationService.Format("Fg.OpenFile", "MFGAdaUnlock.ini"), content.Files[0].Label);
        Assert.Equal(UiLocalizationService.Text("Mfgu.OtherSettings"), content.Lines[^1].Text);
    }

    // w14 final review M5: without RHI's MFG Unlock the rows are greyed (they would write a file no record lists).
    [Fact]
    public void Settings_AreGreyedWhileMfgUnlockIsNotInstalled()
    {
        var bin = Bin("not-installed");
        var game = new DetectedGame { Name = "None", InstallPath = bin, BinaryDirectory = bin, Source = "Test", GraphicsApi = GraphicsApi.DirectX12 };
        var rows = FgSettingsBuilder.MfgUnlock(game, bin, [], installed: false, _ => { }, WriteMethod(bin), WriteDlssSr(bin)).Sections[0].Rows;
        foreach (var row in rows) row.Refresh();

        Assert.All(rows, row => Assert.False(row.IsEnabled));
        Assert.False(File.Exists(MfgUnlockIni.PathIn(bin)));
    }

    [Fact]
    public void Settings_StatusLines_ForXessFilesAndOlderApis()
    {
        var bin = Bin("lines");
        var dx11 = new DetectedGame { Name = "Old", InstallPath = bin, BinaryDirectory = bin, Source = "Test", GraphicsApi = GraphicsApi.DirectX11 };
        var lines = FgSettingsBuilder.MfgUnlock(dx11, bin,
            [new MfgUnlockXessFile("libxess_fg.dll", IntelXessFileState.Missing, null), new MfgUnlockXessFile("libxell.dll", IntelXessFileState.OtherVersion, "1.3.0.4")],
            installed: true, _ => { }, WriteMethod(bin), WriteDlssSr(bin)).Lines;

        Assert.Equal(ComponentDialogLine.Warning(UiLocalizationService.Format("Mfgu.XessMissing", "libxess_fg.dll")), lines[0]);
        Assert.Equal(ComponentDialogLine.Warning(UiLocalizationService.Format("Mfgu.ApiNotSupported", "DirectX 11")), lines[1]);

        var other = FgSettingsBuilder.MfgUnlock(dx11, bin, [new MfgUnlockXessFile("libxell.dll", IntelXessFileState.OtherVersion, "1.3.0.4")], installed: true, _ => { }, WriteMethod(bin), WriteDlssSr(bin)).Lines;
        Assert.Equal(UiLocalizationService.Format("Mfgu.XessOtherVersion", "libxell.dll 1.3.0.4"), other[0].Text);
        Assert.Equal("This game uses DirectX 11. As of MFG Unlock 1.8.0, it only works in DirectX 12 games (and experimentally in Vulkan games), so it won't work here.",
            UiLocalizationService.FormatFor("en-US", "Mfgu.ApiNotSupported", "DirectX 11"));
    }

    // The row's Remove puts the game's own copies back first (w14/extras-fg hook in the managed service).
    [Fact]
    public async Task RowRemove_PutsTheGamesCopiesBackFirst()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var bin = Bin("remove");
        var game = GameIn(bin);
        PeArchitectureDetectorTests.WritePe(Path.Combine(bin, "Game.exe"), 0x8664, 4096);
        game.ExecutablePath = Path.Combine(bin, "Game.exe");
        var addon = Path.Combine(bin, "renodx-mfgunlock.addon64");
        File.WriteAllText(addon, "addon");
        File.WriteAllText(Path.Combine(bin, "libxell.dll"), "the game's libxell");
        var record = AddonRecord(game, bin);
        record.Files[0].Sha256 = FileHash.Sha256Hex(addon);
        fx.Records.Upsert(record);
        var (xess, _, _) = FakeXess();
        fx.Managed.MfgUnlockFiles = new MfgUnlockFiles(xess, fx.Records, journal: null);
        await fx.Managed.MfgUnlockFiles.PlaceAsync(game);

        var removed = await fx.Managed.RemoveAsync(game, ManagedComponentKind.MfgAdaUnlock);

        Assert.True(removed.Success, removed.Message);
        Assert.Equal("the game's libxell", File.ReadAllText(Path.Combine(bin, "libxell.dll")));
        Assert.False(File.Exists(Path.Combine(bin, "libxess_fg.dll")));
        Assert.Null(fx.Records.Find(game, ManagedComponentKind.MfgAdaUnlock));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    // The ⚙ template lays the settings out by section (#297: a card each; label, dropdown or number box, the explanation
    // under it — or two per row with it as the tooltip in RTX Encore's compact sections) and the file buttons after them;
    // the MFG Unlock row has its ⚙ in the row's ⚙ column.
    [Fact]
    public void Templates_ShowSettingsRowsFileButtonsAndTheMfgUnlockGear()
    {
        var app = System.Xml.Linq.XDocument.Load(Path.Combine(LocalizationKeyUsageTests.RepoRoot(), "src", "RHI.Desktop", "App.axaml"));
        var templates = app.Descendants().Where(e => e.Name.LocalName == "DataTemplate").ToArray();
        var rows = templates.Where(t => (string?)t.Attribute(System.Xml.Linq.XName.Get("DataType", "http://schemas.microsoft.com/winfx/2006/xaml")) == "vm:ComponentSettingRow").ToArray();
        Assert.Equal(["CogRow", "CogCell"], rows.Select(r => r.Elements().Single().Name.LocalName));
        foreach (var row in rows)
        {
            Assert.Equal("{Binding Tip}", (string?)row.Elements().Single().Attribute("Help"));
            Assert.Contains(row.Descendants(), e => e.Name.LocalName == "ComboBox" && (string?)e.Attribute("SelectedIndex") == "{Binding SelectedIndex}");
            Assert.Contains(row.Descendants(), e => e.Name.LocalName == "TextBox" && ((string?)e.Attribute("Text"))!.Contains("UpdateSourceTrigger=LostFocus", StringComparison.Ordinal));
        }
        var button = templates.Single(t => (string?)t.Attribute(System.Xml.Linq.XName.Get("DataType", "http://schemas.microsoft.com/winfx/2006/xaml")) == "vm:ComponentFileButton");
        Assert.Equal("{Binding Exists}", (string?)button.Elements().Single().Attribute("IsEnabled"));

        var gear = LumaNexusRowLayoutTests.ComponentRow().Elements().Single(e => (string?)e.Attribute("IsVisible") == "{Binding HasMfgUnlockSettings}");
        Assert.Equal("4", (string?)gear.Attribute("Grid.Column"));
        Assert.Equal("MfgUnlockSettings_Click", (string?)gear.Attribute("Click"));
    }
}
