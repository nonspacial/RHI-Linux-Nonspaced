using System.Collections.Concurrent;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using Xunit.Abstractions;
using static RHI.Core.Tests.DroppedArchiveExtractorTests;

namespace RHI.Core.Tests;

/// <summary>
/// #216 from the shell: the RenoDX or Luma add-on inside a dropped or watched archive is offered in the drop dialog and
/// installed exactly as the same file dropped alone (RenoDX mod, an add-on beside it, or the Luma package install), every
/// refusal kept; Cancel and refusals leave the game and RHI's archive folder as they were; a watched archive is removed
/// only after a successful install while it still holds the bytes RHI read, a dropped one never. Lines EN + PT-BR.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class DroppedArchiveOfferTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-archive-offer-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        UiLocalizationService.Apply("en-US");
        DroppedArchiveExtractor.DeleteFolder(_root);
    }

    private static string Scratch(ViewModelFixture fx) => Path.Combine(fx.Root, "cache", "dropped-archives");
    private static bool ScratchIsEmpty(ViewModelFixture fx) => !Directory.Exists(Scratch(fx)) || !Directory.EnumerateFileSystemEntries(Scratch(fx)).Any();
    private static string Folder(ViewModelFixture fx, string name) => Directory.CreateDirectory(Path.Combine(fx.Root, name)).FullName;

    private async Task<(ViewModelFixture Fx, DetectedGame Game)> StartAsync(string language = "en-US", params string[] more)
    {
        var games = new[] { "Foo Game" }.Concat(more).Select(name => RenoDxLocalAddonTests.Game(Path.Combine(_root, "shell"), name, 0x8664)).ToArray();
        var fx = await ViewModelFixture.StartAsync(games, language);
        UiLocalizationService.Apply(language);
        fx.Managed.DroppedArchiveRoot = Scratch(fx);
        return (fx, fx.Vm.AllLibraryGames.Single(g => g.Name == "Foo Game"));
    }

    private static ConcurrentQueue<string> Lines(ViewModelFixture fx)
    {
        var lines = new ConcurrentQueue<string>();
        fx.Vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(fx.Vm.StatusText)) lines.Enqueue(fx.Vm.StatusText); };
        return lines;
    }

    [Fact]
    public async Task ADroppedZip_WithOneRenoDxAddon_IsOffered_AndInstalledLikeTheFileDroppedAlone_TheArchiveStays()
    {
        var (fx, game) = await StartAsync();
        using var _ = fx;
        var addon = AddonBytes();
        var zip = Zip(Path.Combine(Folder(fx, "Downloads"), "Foo Game HDR.zip"), ("readme.txt", "r"u8.ToArray()), ("renodx-foogame.addon64", addon));

        var found = await fx.Vm.DroppedRenoDx.OfferForArchiveAsync(fx.Vm, zip);

        var archive = found.Offer!;
        Assert.Null(found.Line);
        var offer = Assert.Single(archive.AddOns);
        Assert.Equal("renodx-foogame.addon64", offer.FileName);
        Assert.Equal(zip, offer.SourcePath);
        Assert.Equal(["Foo Game"], offer.GameNames);
        Assert.Equal(0, offer.MatchedIndex);
        Assert.False(offer.DeleteSourceAfterInstall); // dropped, not watched
        Assert.True(offer.IsRenoDx);
        Assert.Equal("Install renodx-foogame.addon64, from the archive Foo Game HDR.zip, as this game's RenoDX mod:", offer.Intro);
        Assert.Equal(UiLocalizationService.Text("Sm.DropTitle"), offer.Title);

        var lines = Lines(fx);
        Assert.True(await fx.Vm.DroppedRenoDx.InstallAsync(fx.Vm, offer, offer.Games[0]), string.Join(" | ", lines));

        Assert.Equal(addon, File.ReadAllBytes(Path.Combine(game.BinaryDirectory!, "renodx-foogame.addon64")));
        var record = fx.Records.Find(game, ManagedComponentKind.RenoDX)!;
        Assert.True(RenoDxDeploymentService.IsLocalPackage(record)); // recorded like a dropped add-on
        Assert.Contains(lines, line => line.StartsWith(RenoDxDeploymentService.LocalInstalledPrefix, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains(UiLocalizationService.Text("Sm.DropSourceRemoved"), StringComparison.Ordinal));
        Assert.True(File.Exists(zip)); // a dropped archive is never removed
        Assert.True(ScratchIsEmpty(fx));
        var history = new ComponentUpdateLogStore(Path.Combine(Path.GetDirectoryName(fx.Settings.SettingsPath)!, "prefs", "update-log.json")).GetAll();
        Assert.Contains(history, e => e.Component == ManagedComponentKind.RenoDX && e.Action == ComponentUpdateAction.Install);
    }

    [Fact]
    public async Task AWatched7z_IsRemovedAfterASuccessfulInstall_WhileItHoldsTheBytesRhiRead()
    {
        if (SevenZip() is not { } sevenZip) { output.WriteLine("SKIP: no 7z on PATH"); return; }
        var (fx, game) = await StartAsync();
        using var _ = fx;
        var watch = Folder(fx, "watch");
        var archivePath = SevenZipArchive(sevenZip, Path.Combine(watch, "renodx-foogame.7z"), ("renodx-foogame.addon64", AddonBytes()));

        var offer = (await fx.Vm.DroppedRenoDx.OfferForArchiveAsync(fx.Vm, archivePath, watch)).Offer!.AddOns.Single();
        Assert.True(offer.DeleteSourceAfterInstall);
        var lines = Lines(fx);
        Assert.True(await fx.Vm.DroppedRenoDx.InstallAsync(fx.Vm, offer, offer.Games[0]), string.Join(" | ", lines));

        Assert.False(File.Exists(archivePath));
        Assert.Contains(lines, line => line.StartsWith(RenoDxDeploymentService.LocalInstalledPrefix, StringComparison.Ordinal)
                                       && line.Contains(" " + UiLocalizationService.Text("Sm.DropSourceRemoved"), StringComparison.Ordinal)); // a pending notice may ride along
        Assert.NotNull(fx.Records.Find(game, ManagedComponentKind.RenoDX));
        Assert.True(ScratchIsEmpty(fx));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    public async Task AWatchedArchive_ThatChangedSinceRhiReadIt_IsKept_AndTheLineSaysSo(string language)
    {
        var (fx, _) = await StartAsync(language);
        using var __ = fx;
        var watch = Folder(fx, "watch");
        var zip = Zip(Path.Combine(watch, "renodx-foogame.zip"), ("renodx-foogame.addon64", AddonBytes()));
        var offer = (await fx.Vm.DroppedRenoDx.OfferForArchiveAsync(fx.Vm, zip, watch)).Offer!.AddOns.Single();
        // The browser saves a newer download under the same name while RHI installs the first one (at the commit point).
        var rewritten = false;
        fx.Records.SaveFaultForTesting = () =>
        {
            if (!rewritten) { rewritten = true; Zip(zip, ("renodx-foogame.addon64", AddonBytes(size: 4096))); }
            return null;
        };
        var lines = Lines(fx);
        Assert.True(await fx.Vm.DroppedRenoDx.InstallAsync(fx.Vm, offer, offer.Games[0]), string.Join(" | ", lines));
        fx.Records.SaveFaultForTesting = null;

        Assert.True(rewritten);
        Assert.True(File.Exists(zip)); // the newer download is kept
        var kept = lines.First(line => line.Contains(" " + UiLocalizationService.Text("Arc.SourceKept"), StringComparison.Ordinal));
        Assert.Equal(language == "pt-BR" ? "O arquivo compactado continua na pasta observada: ele mudou depois que o RHI o leu."
            : "The archive stays in the watch folder: it changed after RHI read it.", UiLocalizationService.Text("Arc.SourceKept"));
        Assert.DoesNotContain(UiLocalizationService.Text("Sm.DropSourceRemoved"), kept, StringComparison.Ordinal);
        Assert.True(ScratchIsEmpty(fx));
    }

    [Fact]
    public async Task AWatchedArchive_IsKeptAfterARefusedInstall_AndNothingIsLeftBehind()
    {
        var (fx, game) = await StartAsync();
        using var _ = fx;
        var watch = Folder(fx, "watch");
        var zip = Zip(Path.Combine(watch, "renodx-foogame.zip"), ("renodx-foogame.addon32", AddonBytes(machine: 0x14c)));
        var offer = (await fx.Vm.DroppedRenoDx.OfferForArchiveAsync(fx.Vm, zip, watch)).Offer!.AddOns.Single();
        var before = CancelAtWritePhase.Snapshot(game.InstallPath);

        var lines = Lines(fx);
        Assert.False(await fx.Vm.DroppedRenoDx.InstallAsync(fx.Vm, offer, offer.Games[0]));

        Assert.Contains(lines, line => line.StartsWith(RenoDxDeploymentService.Local32BitOn64BitMessage, StringComparison.Ordinal)); // the dropped file's own refusal
        Assert.True(File.Exists(zip));
        Assert.Null(fx.Records.Find(game, ManagedComponentKind.RenoDX));
        CancelAtWritePhase.AssertUnchanged(before, game.InstallPath);
        Assert.True(ScratchIsEmpty(fx));
    }

    // w10 follow-up D16: the archive dialog's "Replaces …" line for a Luma add-on speaks of Luma, never of the RenoDX
    // add-on the game has (a Luma offer is built as a RenoDX one); a RenoDX add-on still names RHI's RenoDX add-on.
    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    public async Task ALumaAddon_FromAnArchive_SaysWhatItReplacesInLumasWords_NotTheRenoDxAddons(string language)
    {
        var (fx, game) = await StartAsync(language);
        using var _ = fx;
        var record = (ManagedComponentKind kind, string file) => new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = kind, BinaryDirectory = game.BinaryDirectory!,
            Files = [new LinuxDeployedFile { Path = Path.Combine(game.BinaryDirectory!, file), Sha256 = new string('0', 64) }]
        };
        fx.Records.Upsert(record(ManagedComponentKind.RenoDX, "renodx-foogame.addon64"));
        var zip = Zip(Path.Combine(Folder(fx, "Downloads"), "pacote.zip"), ("renodx-bargame.addon64", AddonBytes()), ("Luma-Foo.addon64", AddonBytes()));
        using var archive = (await fx.Vm.DroppedRenoDx.OfferForArchiveAsync(fx.Vm, zip)).Offer!;
        var renodx = archive.AddOns.Single(offer => !offer.IsLuma);
        var luma = archive.AddOns.Single(offer => offer.IsLuma);
        var pt = language == "pt-BR";

        Assert.Equal(pt ? "Substitui o add-on do RenoDX que o RHI instalou: renodx-foogame.addon64" : "Replaces the RenoDX add-on RHI installed: renodx-foogame.addon64",
            fx.Vm.DroppedRenoDx.ReplacesFor(renodx, game));
        Assert.Null(fx.Vm.DroppedRenoDx.ReplacesFor(luma, game)); // no Luma install yet: it replaces nothing

        fx.Records.Upsert(record(ManagedComponentKind.LumaFramework, "Luma-Foo.addon64"));
        Assert.Equal(pt ? "Substitui o Luma que o RHI instalou neste jogo." : "Replaces the Luma install RHI made in this game.", fx.Vm.DroppedRenoDx.ReplacesFor(luma, game));
    }

    // w10 follow-up D5: two add-ons of one file name in different folders read apart in the "Add-on" dropdown (their
    // path inside the archive); a name no other add-on has stays the bare file name.
    [Fact]
    public async Task TwoAddonsOfOneFileName_ShowTheirFolderInTheAddonDropdown()
    {
        var (fx, _) = await StartAsync();
        using var _ = fx;
        var zip = Zip(Path.Combine(Folder(fx, "Downloads"), "renodx-pack.zip"),
            ("v1/renodx-foogame.addon64", AddonBytes()), ("v2\\RenoDX-FooGame.addon64", AddonBytes(size: 3072)), ("renodx-mfg_unlock.addon64", AddonBytes()));

        using var archive = (await fx.Vm.DroppedRenoDx.OfferForArchiveAsync(fx.Vm, zip)).Offer!;

        Assert.Equal(["v1/renodx-foogame.addon64", "v2/RenoDX-FooGame.addon64", "renodx-mfg_unlock.addon64"], archive.AddOnNames);
        Assert.Equal(archive.AddOnNames.Count, archive.AddOnNames.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task TwoAddons_AreBothOffered_EachWithItsOwnMatch_AndOnlyTheChosenOneIsInstalled()
    {
        var (fx, foo) = await StartAsync("en-US", "Bar Game");
        using var _ = fx;
        var bar = fx.Vm.AllLibraryGames.Single(g => g.Name == "Bar Game");
        var zip = Zip(Path.Combine(Folder(fx, "Downloads"), "renodx-pack.zip"),
            ("foo/renodx-foogame.addon64", AddonBytes()), ("bar/renodx-bargame.addon64", AddonBytes(size: 3072)), ("renodx-mfg_unlock.addon64", AddonBytes()));

        using var archive = (await fx.Vm.DroppedRenoDx.OfferForArchiveAsync(fx.Vm, zip)).Offer!;

        Assert.Equal(["renodx-foogame.addon64", "renodx-bargame.addon64", "renodx-mfg_unlock.addon64"], archive.AddOnNames);
        Assert.Equal("Foo Game", archive.AddOns[0].Games[archive.AddOns[0].MatchedIndex].Name);
        Assert.Equal("Bar Game", archive.AddOns[1].Games[archive.AddOns[1].MatchedIndex].Name);
        Assert.False(archive.AddOns[2].IsRenoDx); // another add-on: beside RenoDX, through Shaders/Add-ons
        Assert.Equal(UiLocalizationService.Text("Sm.DropAddonTitle"), archive.AddOns[2].Title);
        Assert.Equal(UiLocalizationService.Text("Arc.AddonChoiceTip"), archive.AddOnTip);

        Assert.True(await fx.Vm.DroppedRenoDx.InstallAsync(fx.Vm, archive.AddOns[1], bar));

        Assert.Equal(3072, new FileInfo(Path.Combine(bar.BinaryDirectory!, "renodx-bargame.addon64")).Length);
        Assert.NotNull(fx.Records.Find(bar, ManagedComponentKind.RenoDX));
        Assert.Null(fx.Records.Find(foo, ManagedComponentKind.RenoDX));
        Assert.False(File.Exists(Path.Combine(foo.BinaryDirectory!, "renodx-foogame.addon64")));
        Assert.True(ScratchIsEmpty(fx));
    }

    [Fact]
    public async Task AnotherAddonInTheArchive_GoesInBesideRenoDx_ThroughTheDroppedAddonPath()
    {
        var (fx, game) = await StartAsync();
        using var _ = fx;
        var zip = Zip(Path.Combine(Folder(fx, "Downloads"), "tools.zip"), ("renodx-mfg_unlock.addon64", AddonBytes()));
        var offer = (await fx.Vm.DroppedRenoDx.OfferForArchiveAsync(fx.Vm, zip)).Offer!.AddOns.Single();
        Assert.Equal("Install renodx-mfg_unlock.addon64, from the archive tools.zip, into this game, beside its other add-ons:", offer.Intro);

        var lines = Lines(fx);
        Assert.True(await fx.Vm.DroppedRenoDx.InstallAsync(fx.Vm, offer, game), string.Join(" | ", lines));

        Assert.Contains(lines, line => line.StartsWith(LinuxManagedComponentService.DroppedAddonInstalledPrefix, StringComparison.Ordinal));
        Assert.Contains("custom-renodx-mfg_unlock", fx.Managed.EffectiveAddons(game)!.Ids);
        Assert.Null(fx.Records.Find(game, ManagedComponentKind.RenoDX)); // RenoDX is left alone
        Assert.True(ScratchIsEmpty(fx));
    }

    [Fact]
    public async Task ALumaAddonInAZip_SendsRhisCopyOfTheWholeArchive_ToTheLumaPackageInstall()
    {
        var (fx, game) = await StartAsync();
        using var _ = fx;
        var watch = Folder(fx, "watch");
        var zip = Zip(Path.Combine(watch, "luma-foo.zip"), ("Luma/Luma-Foo.addon64", AddonBytes()), ("Luma/shaders/a.hlsl", "s"u8.ToArray()));
        (DetectedGame Game, string Name, byte[] Bytes)? called = null;
        fx.Managed.ArchiveLumaInstallForTesting = (target, path, _) =>
        {
            called = (target, Path.GetFileName(path), File.ReadAllBytes(path));
            Assert.NotEqual(zip, path); // RHI's own copy, never the user's file
            return Task.FromResult(ComponentOperationResult.Ok("Luma Framework installed."));
        };

        var offer = (await fx.Vm.DroppedRenoDx.OfferForArchiveAsync(fx.Vm, zip, watch)).Offer!.AddOns.Single();
        Assert.True(offer.IsLuma);
        Assert.Equal(UiLocalizationService.Text("Arc.LumaTitle"), offer.Title);
        Assert.Equal("Install the Luma package luma-foo.zip (it holds Luma-Foo.addon64) into this game:", offer.Intro);
        Assert.True(await fx.Vm.DroppedRenoDx.InstallAsync(fx.Vm, offer, game));

        Assert.Same(game, called!.Value.Game);
        Assert.Equal("luma-foo.zip", called.Value.Name);
        Assert.False(File.Exists(zip)); // watched, unchanged, installed: removed
        Assert.True(ScratchIsEmpty(fx));
    }

    [Theory]
    [InlineData(ExternalArchiveToolKind.SevenZip)]
    [InlineData(ExternalArchiveToolKind.BsdTar)]
    public async Task ALumaAddonInA7z_IsTakenOutWhole_ZippedAgainInRhisFolder_AndGivenToTheLumaInstall(ExternalArchiveToolKind kind)
    {
        if (SevenZip() is not { } sevenZip || BsdTar() is not { } bsdtar) { output.WriteLine("SKIP: needs 7z and bsdtar on PATH"); return; }
        var (fx, game) = await StartAsync();
        using var _ = fx;
        var watch = Folder(fx, "watch");
        var files = new (string Name, byte[] Data)[]
        {
            ("Luma/Luma-Foo.addon64", AddonBytes()), ("Luma/shaders/a.hlsl", "shader a"u8.ToArray()), ("Luma/readme.txt", "r"u8.ToArray()),
        };
        var archivePath = SevenZipArchive(sevenZip, Path.Combine(watch, "luma-foo.7z"), files);
        var userBytes = File.ReadAllBytes(archivePath);
        (string Path, UnixFileMode FolderMode, UnixFileMode FileMode, Dictionary<string, byte[]> Entries, int Planned)? called = null;
        fx.Managed.ArchiveLumaInstallForTesting = async (target, path, ct) =>
        {
            Assert.Same(game, target);
            using (var zip = System.IO.Compression.ZipFile.OpenRead(path))
                called = (path, File.GetUnixFileMode(Path.GetDirectoryName(path)!), File.GetUnixFileMode(path),
                    zip.Entries.ToDictionary(e => e.FullName, e => { using var r = e.Open(); using var m = new MemoryStream(); r.CopyTo(m); return m.ToArray(); }), 0);
            // The Luma install's own reader takes it: every file planned, the add-on beside ReShade.
            var staging = Directory.CreateDirectory(Path.Combine(fx.Root, "luma-staging")).FullName;
            var plan = await LumaFrameworkDeploymentService.BuildExtractionPlanAsync(path, game.BinaryDirectory!, staging, ct);
            called = called!.Value with { Planned = plan.Count };
            return ComponentOperationResult.Ok("Luma Framework installed.");
        };
        ExternalArchiveRunner.FindToolForTesting.Value = () => new ExternalArchiveTool(kind == ExternalArchiveToolKind.SevenZip ? sevenZip : bsdtar, kind);
        bool installed;
        try
        {
            var offer = (await fx.Vm.DroppedRenoDx.OfferForArchiveAsync(fx.Vm, archivePath, watch)).Offer!.AddOns.Single();
            Assert.True(offer.IsLuma);
            Assert.Equal("Install the Luma package luma-foo.7z (it holds Luma-Foo.addon64) into this game:", offer.Intro);
            installed = await fx.Vm.DroppedRenoDx.InstallAsync(fx.Vm, offer, game);
        }
        finally
        {
            ExternalArchiveRunner.FindToolForTesting.Value = null;
        }

        Assert.True(installed, fx.Vm.StatusText);
        var (package, folderMode, fileMode, entries, planned) = called!.Value;
        Assert.Equal("luma-foo.zip", Path.GetFileName(package));
        Assert.StartsWith(Scratch(fx) + Path.DirectorySeparatorChar, package, StringComparison.Ordinal); // RHI's private folder, never beside the user's file
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, folderMode);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, fileMode);
        Assert.Equal(files.Select(f => f.Name).Order(StringComparer.Ordinal), entries.Keys.Order(StringComparer.Ordinal)); // every file
        foreach (var (name, data) in files) Assert.Equal(data, entries[name]);
        Assert.Equal(files.Length, planned);
        Assert.False(File.Exists(archivePath)); // watched, unchanged, installed: removed (decision 3)
        Assert.True(ScratchIsEmpty(fx));
        Assert.NotEmpty(userBytes);
    }

    [Fact]
    public async Task ALumaPackageThatUnpacksToMoreThanItLists_IsStoppedAtTheListedSize_AndNeverReachesTheLumaInstall()
    {
        if (SevenZip() is null) { output.WriteLine("SKIP: no 7z on PATH"); return; }
        var (fx, game) = await StartAsync();
        using var _ = fx;
        // A gzip stream behind a .7z name: 7-Zip lists the size its trailer gives (1 KiB) and writes all 8 MiB.
        var archivePath = LyingGzip(Path.Combine(Folder(fx, "Downloads"), "luma-foo.7z"), "Luma-Foo.addon64", 8 << 20, 1024);
        var called = false;
        fx.Managed.ArchiveLumaInstallForTesting = (_, _, _) => { called = true; return Task.FromResult(ComponentOperationResult.Ok("x")); };
        using var archive = (await fx.Managed.InspectDroppedArchiveAsync(archivePath)).Archive!;
        Assert.Equal(1024, archive.Addons.Single().Size);
        long largest = 0;
        using var watch = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!watch.IsCancellationRequested)
            {
                foreach (var file in Directory.EnumerateFiles(archive.Folder, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".7z", StringComparison.Ordinal)))
                    try { largest = Math.Max(largest, new FileInfo(file).Length); } catch (IOException) { }
                await Task.Delay(1);
            }
        });

        var result = await fx.Managed.InstallArchivedAddonAsync(game, archive, archive.Addons.Single());
        watch.Cancel();
        await sampler;

        Assert.False(result.Success);
        Assert.Equal(LinuxManagedComponentService.ArchiveExtractFailedPrefix + DroppedArchiveExtractor.PackageMismatchRefusal + "luma-foo.7z", result.Message);
        Assert.False(called);
        Assert.InRange(largest, 0, 1024 + 512); // the kernel's file-size bound: never past the listed size (plus a block)
        Assert.Equal(["archive"], Directory.EnumerateFileSystemEntries(archive.Folder).Select(Path.GetFileName));
        Assert.True(File.Exists(archivePath));
    }

    [Theory]
    [InlineData("en-US", false, "notes.zip holds no add-on RHI can install (a RenoDX or Luma add-on), so nothing was installed.")]
    [InlineData("pt-BR", false, "notes.zip não contém nenhum add-on que o RHI possa instalar (um add-on do RenoDX ou do Luma), então nada foi instalado.")]
    [InlineData("en-US", true, "The watcher found notes.zip, but it holds no add-on RHI can install (a RenoDX or Luma add-on); nothing was installed and it stays in the folder.")]
    [InlineData("pt-BR", true, "O monitor detectou notes.zip, mas ele não contém nenhum add-on que o RHI possa instalar (um add-on do RenoDX ou do Luma); nada foi instalado e o arquivo continua na pasta.")]
    public async Task AnArchiveWithoutAnAddon_KeepsItsLine_AndNothingIsExtractedOrImported(string language, bool watched, string expected)
    {
        var (fx, _) = await StartAsync(language);
        using var __ = fx;
        var watch = Folder(fx, "watch");
        var zip = Zip(Path.Combine(watch, "notes.zip"), ("ShaderToggler.addon64", AddonBytes()), ("renodx-foo.zip", [1, 2, 3]), ("readme.txt", "r"u8.ToArray()));

        var found = await fx.Vm.DroppedRenoDx.OfferForArchiveAsync(fx.Vm, zip, watched ? watch : null);

        Assert.Null(found.Offer);
        Assert.Equal(expected, found.Line);
        Assert.True(ScratchIsEmpty(fx));
        Assert.False(Directory.Exists(fx.Managed.CustomAddonRoot) && Directory.EnumerateFiles(fx.Managed.CustomAddonRoot).Any()); // never an import
        Assert.True(File.Exists(zip));
    }

    [Theory]
    [InlineData("en-US", "RHI did not open hostile.zip, so nothing was installed: a file inside climbs out of the archive's folder with \"..\": ../renodx-foogame.addon64")]
    [InlineData("pt-BR", "O RHI não abriu hostile.zip, então nada foi instalado: um arquivo interno sai da pasta do arquivo compactado com \"..\": ../renodx-foogame.addon64")]
    public async Task AHostileArchive_IsRefusedWithItsReason_BeforeAnyGameWrite(string language, string expected)
    {
        var (fx, game) = await StartAsync(language);
        using var _ = fx;
        var before = CancelAtWritePhase.Snapshot(game.InstallPath);
        var zip = Zip(Path.Combine(Folder(fx, "Downloads"), "hostile.zip"), ("../renodx-foogame.addon64", AddonBytes()));

        var found = await fx.Vm.DroppedRenoDx.OfferForArchiveAsync(fx.Vm, zip);

        Assert.Null(found.Offer);
        Assert.Equal(expected, found.Line);
        CancelAtWritePhase.AssertUnchanged(before, game.InstallPath);
        Assert.True(ScratchIsEmpty(fx));
        Assert.False(File.Exists(Path.Combine(fx.Root, "renodx-foogame.addon64")));
    }

    [Fact]
    public async Task CancelAtTheWriteGate_RunsToTheEnd_OrRollsBackFully_AndTheAddonIsNeverLeftExtracted()
    {
        var (fx, game) = await StartAsync();
        using var _ = fx;
        var zip = Zip(Path.Combine(Folder(fx, "Downloads"), "renodx-foogame.zip"), ("renodx-foogame.addon64", AddonBytes()));
        using var archive = (await fx.Managed.InspectDroppedArchiveAsync(zip)).Archive!;
        string[] Extracted() => Directory.EnumerateDirectories(archive.Folder, "addon-*").ToArray();

        // Cancel pressed as the install starts writing: honoured anyway, the operation completes as one.
        var completed = await CancelAtWritePhase.RunAsync(ct => fx.Managed.InstallArchivedAddonAsync(game, archive, archive.Addons[0], ct), [game.InstallPath]);
        Assert.True(completed.Result.Success, completed.Result.Message);
        Assert.True(File.Exists(Path.Combine(game.BinaryDirectory!, "renodx-foogame.addon64")));
        Assert.Empty(Extracted());

        // The same with the commit refused: the game folder is exactly as it was before.
        var removal = await fx.Managed.RemoveAsync(game, ManagedComponentKind.RenoDX);
        Assert.True(removal.Success, removal.Message);
        var rolledBack = await CancelAtWritePhase.RunAsync(ct => fx.Managed.InstallArchivedAddonAsync(game, archive, archive.Addons[0], ct), [game.InstallPath],
            atWritePhase: () => fx.Records.SaveFaultForTesting = () => throw new IOException("ledger write refused (test)"));
        fx.Records.SaveFaultForTesting = null;
        Assert.False(rolledBack.Result.Success);
        CancelAtWritePhase.AssertUnchanged(rolledBack.Before, game.InstallPath);
        Assert.Empty(Extracted());
    }

    [Fact]
    public async Task CancelWhileTheAddonIsExtracted_SaysCancelled_TheGameIsUnchanged_AndNothingIsLeft()
    {
        var (fx, game) = await StartAsync();
        using var _ = fx;
        var watch = Folder(fx, "watch");
        var zip = Zip(Path.Combine(watch, "renodx-foogame.zip"), ("renodx-foogame.addon64", AddonBytes()));
        var offer = (await fx.Vm.DroppedRenoDx.OfferForArchiveAsync(fx.Vm, zip, watch)).Offer!.AddOns.Single();
        var before = CancelAtWritePhase.Snapshot(game.InstallPath);
        var lines = Lines(fx);
        DroppedArchiveExtractor.BeforeExtractForTesting.Value = fx.Vm.Operations.Cancel;
        bool installed;
        try
        {
            installed = await fx.Vm.DroppedRenoDx.InstallAsync(fx.Vm, offer, game);
        }
        finally
        {
            DroppedArchiveExtractor.BeforeExtractForTesting.Value = null;
        }

        Assert.False(installed);
        Assert.StartsWith(MainWindowViewModel.OperationCancelledText(), lines.Last(), StringComparison.Ordinal);
        CancelAtWritePhase.AssertUnchanged(before, game.InstallPath);
        Assert.Null(fx.Records.Find(game, ManagedComponentKind.RenoDX));
        Assert.True(File.Exists(zip));
        Assert.True(ScratchIsEmpty(fx));
    }

    [Fact]
    public async Task AnInstallRefusal_ComesBeforeAnythingIsExtracted_AndTheWatchedArchiveStays()
    {
        var (fx, game) = await StartAsync();
        using var _ = fx;
        var watch = Folder(fx, "watch");
        var zip = Zip(Path.Combine(watch, "renodx-foogame.zip"), ("renodx-foogame.addon64", AddonBytes()));
        var offer = (await fx.Vm.DroppedRenoDx.OfferForArchiveAsync(fx.Vm, zip, watch)).Offer!.AddOns.Single();
        game.RequiresExplicitPatchTarget = true; // the install guard refuses: no confirmed patch target
        game.ExecutablePath = null;
        var extracted = false;
        var lines = Lines(fx);
        DroppedArchiveExtractor.BeforeExtractForTesting.Value = () => extracted = true;
        bool installed;
        try
        {
            installed = await fx.Vm.DroppedRenoDx.InstallAsync(fx.Vm, offer, game);
        }
        finally
        {
            DroppedArchiveExtractor.BeforeExtractForTesting.Value = null;
        }

        Assert.False(installed);
        Assert.False(extracted);
        Assert.Contains(lines, line => line.StartsWith("Modification target not set. Use Edit game", StringComparison.Ordinal)); // the guard, presented
        Assert.True(File.Exists(zip));
        Assert.True(ScratchIsEmpty(fx));
    }

    [Fact]
    public async Task ABusyShell_RefusesTheInstall_AndStillRemovesRhisCopy()
    {
        var (fx, game) = await StartAsync();
        using var _ = fx;
        var zip = Zip(Path.Combine(Folder(fx, "Downloads"), "renodx-foogame.zip"), ("renodx-foogame.addon64", AddonBytes()));
        var offer = (await fx.Vm.DroppedRenoDx.OfferForArchiveAsync(fx.Vm, zip)).Offer!.AddOns.Single();
        using (fx.Vm.Operations.TryBegin("test"))
            Assert.False(await fx.Vm.DroppedRenoDx.InstallAsync(fx.Vm, offer, game).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.StartsWith(UiLocalizationService.Text("Vm.AnotherRHIOperationAlreadyRunning"), fx.Vm.StatusText, StringComparison.Ordinal);
        Assert.True(ScratchIsEmpty(fx));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    public async Task TheDialogTexts_NameTheArchiveAndTheAddon_InBothLanguages(string language)
    {
        var (fx, _) = await StartAsync(language);
        using var __ = fx;
        var zip = Zip(Path.Combine(Folder(fx, "Downloads"), "pacote.zip"),
            ("renodx-foogame.addon64", AddonBytes()), ("renodx-mfg_unlock.addon64", AddonBytes()), ("Luma-Foo.addon64", AddonBytes()));
        fx.Managed.ArchiveLumaInstallForTesting = (_, _, _) => Task.FromResult(ComponentOperationResult.Ok("ok"));
        using var archive = (await fx.Vm.DroppedRenoDx.OfferForArchiveAsync(fx.Vm, zip)).Offer!;
        var pt = language == "pt-BR";

        Assert.Equal(3, archive.AddOns.Count);
        Assert.Equal(pt ? "Instalar renodx-foogame.addon64, do arquivo compactado pacote.zip, como o mod RenoDX deste jogo:"
            : "Install renodx-foogame.addon64, from the archive pacote.zip, as this game's RenoDX mod:", archive.AddOns[0].Intro);
        Assert.Equal(pt ? "Instalar renodx-mfg_unlock.addon64, do arquivo compactado pacote.zip, neste jogo, ao lado dos outros add-ons:"
            : "Install renodx-mfg_unlock.addon64, from the archive pacote.zip, into this game, beside its other add-ons:", archive.AddOns[1].Intro);
        Assert.Equal(pt ? "Instalar add-on do Luma" : "Install Luma add-on", archive.AddOns[2].Title);
        Assert.Equal(pt ? "Instalar o pacote Luma pacote.zip (ele contém Luma-Foo.addon64) neste jogo:"
            : "Install the Luma package pacote.zip (it holds Luma-Foo.addon64) into this game:", archive.AddOns[2].Intro);
        Assert.Equal(pt ? "O RHI usa a mesma instalação gerenciada de um pacote Luma solto sobre ele; o botão Remover da linha do Luma o retira."
            : "RHI uses the same managed install as for a Luma package dropped on it; the Luma row's Remove button takes it out.", archive.AddOns[2].Note);
        Assert.Equal(pt ? "Os add-ons encontrados no arquivo compactado; escolha o que será instalado."
            : "The add-ons found in the archive; choose the one to install.", archive.AddOnTip);
        Assert.Equal(pt ? "Lendo o arquivo compactado pacote.zip…" : "Reading the archive pacote.zip…", UiLocalizationService.Format("Arc.Reading", "pacote.zip"));
    }

    public static TheoryData<string, string> ArchiveFixedTexts => new()
    {
        { DroppedArchiveExtractor.ParentRefusal + "../x", "climbs out" },
        { DroppedArchiveExtractor.AbsoluteRefusal + "/x", "absolute path" },
        { DroppedArchiveExtractor.EmptyPartRefusal + "a//b", "empty part" },
        { DroppedArchiveExtractor.DuplicateRefusal + "x", "listed twice" },
        { DroppedArchiveExtractor.LinkRefusal + "x", "link or a device" },
        { DroppedArchiveExtractor.NameRefusal + "x", "read safely" },
        { DroppedArchiveExtractor.OptionLikeRefusal + "-C/x", "take for an option" }, // w10 follow-up D1
        { DroppedArchiveExtractor.AddonTooLargeRefusal + "x", "larger than 256" },
        { DroppedArchiveExtractor.TotalTooLargeRefusal + "x.zip", "together are larger" },
        { DroppedArchiveExtractor.RatioRefusal + "x.zip", "times its own size" },
        { DroppedArchiveExtractor.ArchiveTooLargeRefusal + "x.zip", "larger than 1 GiB" },
        { DroppedArchiveExtractor.NotAFileRefusal + "x.zip", "not a plain file" },
        { DroppedArchiveExtractor.UnreadableRefusal + "x.zip", "could not be read" },
        { DroppedArchiveExtractor.TooManyFilesRefusal + "x.zip", "too many files" },
        { DroppedArchiveExtractor.SizeMismatchRefusal + "x", "different size" },
        { DroppedArchiveExtractor.NotExtractedRefusal + "x", "did not give back" },
        { LinuxManagedComponentService.ArchiveExtractFailedPrefix + DroppedArchiveExtractor.SizeMismatchRefusal + "x", "taken out of the archive" },
        { DroppedArchiveExtractor.ChangedWhileReadRefusal + "x.zip", "changed while RHI read it" },
        { DroppedArchiveExtractor.PackageMismatchRefusal + "x.7z", "unpack differently" },
        { ReShadeArchiveExtractor.ArchiveRefusedPrefix + ExternalArchiveRunner.OutputLimitRefusal + "1024", "the archive tool was stopped" },
    };

    // The English marker list does not know every word: the fixed English text itself must be gone from the PT-BR
    // presentation (the IntegrationMessage_LeavesNoEnglishFixedText_InPortuguese rule, kept in this lane's file).
    [Theory]
    [MemberData(nameof(ArchiveFixedTexts))]
    public void ArchiveMessage_LeavesNoEnglishFixedText_InPortuguese(string message, string englishFixedText)
    {
        var pt = UiLocalizationService.PresentFor("pt-BR", message);
        Assert.DoesNotContain(englishFixedText, pt, StringComparison.Ordinal);
        Assert.NotEqual(UiLocalizationService.UnlocalizedFallbackFor("pt-BR"), pt);
        Assert.Equal(message, UiLocalizationService.PresentFor("en-US", message));
    }
}
