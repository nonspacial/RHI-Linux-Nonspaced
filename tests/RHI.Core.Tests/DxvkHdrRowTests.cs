using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Hdr;

namespace RHI.Core.Tests;

/// <summary>
/// #200 DX9 HDR in the app: the row exists only for Windows DirectX 9 games outside Xbox / Game Pass (or where its
/// d3d9.dll already is), its ⚙ only while RHI's DX9 HDR is installed, the row says when HDR launch is Off, and the install
/// asks first — saying that ReShade moves to its Vulkan layer when it will.
/// </summary>
[Collection(UiLanguageCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class DxvkHdrRowTests
{
    private static DetectedGame Game(ViewModelFixture fx, string name, GraphicsApi api = GraphicsApi.DirectX9, string runner = "Proton")
    {
        var bin = Path.Combine(fx.Root, "games", name, "bin");
        var prefix = Path.Combine(fx.Root, "games", name, "pfx");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(prefix);
        PeArchitectureDetectorTests.WritePe(Path.Combine(bin, "Game.exe"), 0x8664, 4096);
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n");
        return new DetectedGame
        {
            Name = name, InstallPath = Path.GetDirectoryName(bin)!, BinaryDirectory = bin, ExecutablePath = Path.Combine(bin, "Game.exe"),
            PrefixPath = prefix, Runner = runner, Source = "Test", GraphicsApi = api
        };
    }

    private static IReadOnlyList<ManagedComponent> Decorated(ViewModelFixture fx, DetectedGame game)
    {
        var files = Directory.EnumerateFiles(game.InstallPath!, "*", SearchOption.AllDirectories).ToArray();
        var components = ComponentProbe.Probe(game, files);
        fx.Managed.Decorate(game, components);
        return components;
    }

    private static ManagedComponent? Row(IReadOnlyList<ManagedComponent> components) => components.SingleOrDefault(c => c.Kind == ManagedComponentKind.DxvkHdr);

    private static void Installed(ViewModelFixture fx, DetectedGame game)
    {
        var d3d9 = Path.Combine(game.BinaryDirectory!, "d3d9.dll");
        File.WriteAllBytes(d3d9, ReShadeChannelTests.DxvkHdrDll(0x8664));
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.DxvkHdr,
            BinaryDirectory = game.BinaryDirectory!, Version = "3.1-HDR-mod-v0.3.4",
            Files = [new LinuxDeployedFile { Path = d3d9, Sha256 = FileHash.Sha256Hex(d3d9) }]
        });
    }

    [Fact]
    public async Task Dx9WindowsGame_ShowsTheRow_InstallableWithItsNote_AndNoGearBeforeInstall()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var row = Row(Decorated(fx, Game(fx, "Dx9")))!;

        Assert.Equal(ComponentAvailability.Available, row.Availability);
        Assert.Equal("DX9 HDR", row.DisplayName);
        Assert.True(row.ActionAllowed, row.Note);
        Assert.StartsWith(DxvkHdrDeploymentService.RowNote, row.Note, StringComparison.Ordinal);
        Assert.True(ComponentRowViewModel.IsShown(row));
        Assert.False(new ComponentRowViewModel(row, null!).HasDxvkHdrSettings);
    }

    [Theory]
    [InlineData("dx11")]
    [InlineData("native")]
    [InlineData("xbox")]
    [InlineData("dx8")]
    public async Task WhereItCannotApply_ThereIsNoRow(string kind)
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = kind switch
        {
            "dx11" => Game(fx, "Dx11", GraphicsApi.DirectX11),
            "native" => Game(fx, "Native", runner: "Native Linux"),
            "dx8" => Game(fx, "Dx8", GraphicsApi.DirectX8),
            _ => Game(fx, "Xbox")
        };
        if (kind == "xbox") game.XboxPackage = new XboxPackageInfo("9TEST", "installer", null, null);

        // Listed hidden (Unsupported) at most: RHI's record could bring it back for Remove; without one nothing shows.
        Assert.False(Row(Decorated(fx, game)) is { } row && ComponentRowViewModel.IsShown(row));
    }

    [Fact]
    public async Task UserOverrideToDx9_ShowsTheRow_AndAPlainDxvkBesideTheGameIsNotTakenForIt()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = Game(fx, "Override", GraphicsApi.DirectX11);
        game.UserGraphicsApiOverride = GraphicsApi.DirectX9;
        PeArchitectureDetectorTests.WritePe(Path.Combine(game.BinaryDirectory!, "d3d9.dll"), 0x8664, 8192); // a DXVK without the HDR build's marker

        var row = Row(Decorated(fx, game))!;

        Assert.Equal(ComponentAvailability.Available, row.Availability);
    }

    [Fact]
    public async Task InstalledByRhi_HasTheGear_AndRemoveStaysOffered_EvenWhenTheGameIsNoLongerDx9()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = Game(fx, "Installed");
        Installed(fx, game);
        game.UserGraphicsApiOverride = GraphicsApi.DirectX11;

        var row = Row(Decorated(fx, game))!;
        var vm = new ComponentRowViewModel(row, null!);

        Assert.Equal(ComponentAvailability.Installed, row.Availability);
        Assert.True(row.ManagedByRhi);
        Assert.True(row.ActionAllowed, row.Note);
        Assert.True(vm.HasDxvkHdrSettings);
        Assert.True(vm.IsRemoveAction);
        Assert.Equal(UiLocalizationService.Text("Dxvk.SettingsTip"), vm.DxvkHdrSettingsTip);
    }

    [Fact]
    public async Task ADxvkHdrBuildRhiDidNotInstall_IsShownAsExternal_LockedAndWithoutTheGear()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = Game(fx, "External");
        File.WriteAllBytes(Path.Combine(game.BinaryDirectory!, "d3d9.dll"), ReShadeChannelTests.DxvkHdrDll(0x8664));

        var row = Row(Decorated(fx, game))!;

        Assert.Equal(ComponentAvailability.Installed, row.Availability);
        Assert.False(row.ActionAllowed);
        Assert.Equal(DxvkHdrDeploymentService.ExternalNote, row.Note);
        Assert.False(new ComponentRowViewModel(row, null!).HasDxvkHdrSettings);
    }

    // Review nit 8: the note reads the view model's in-memory library (what its HDR controls and the ⚙ dialog read),
    // never a fresh read of the library file: a change kept in memory shows; one only on disk does not.
    [Fact]
    public async Task HdrLaunchOff_TheRowSaysSoAndNamesTheControl_OnTurnsTheLineOff()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = Game(fx, "Hdr");
        var key = RHI.Platform.Linux.Settings.LinuxGameLibraryStore.GameKey(game);
        Assert.EndsWith(DxvkHdrDeploymentService.HdrLaunchOffNote, Row(Decorated(fx, game))!.Note, StringComparison.Ordinal);

        var onDisk = fx.Library.Load();
        onDisk.HdrProfiles[key] = new LinuxHdrGameProfile { Mode = LinuxHdrModes.NativeWayland };
        fx.Library.Save(onDisk);
        Assert.EndsWith(DxvkHdrDeploymentService.HdrLaunchOffNote, Row(Decorated(fx, game))!.Note, StringComparison.Ordinal);

        ((IPanelHost)fx.Vm).LibraryState.HdrProfiles[key] = new LinuxHdrGameProfile { Mode = LinuxHdrModes.NativeWayland };
        Assert.DoesNotContain(DxvkHdrDeploymentService.HdrLaunchOffNote, Row(Decorated(fx, game))!.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithADllReShade_TheRowAndTheConfirmationSayReShadeMovesToItsVulkanLayer()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = Game(fx, "WithReShade");
        fx.Records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.ReShade,
            BinaryDirectory = game.BinaryDirectory!, Files = [new LinuxDeployedFile { Path = Path.Combine(game.BinaryDirectory!, "d3d9.dll"), Sha256 = "00" }]
        });
        (string Title, string Body, string Accept)? asked = null;
        fx.Vm.ConfirmAsync = (title, body, accept) => { asked = (title, body, accept); return Task.FromResult(false); };

        var note = Row(Decorated(fx, game))!.Note!;
        var accepted = await fx.Vm.DxvkHdr.ConfirmInstallAsync(game);

        Assert.Contains(DxvkHdrDeploymentService.RowReShadeNote, note, StringComparison.Ordinal);
        Assert.False(accepted);
        Assert.Equal(UiLocalizationService.Format("Dxvk.ConfirmTitle", "WithReShade"), asked!.Value.Title);
        Assert.Equal(UiLocalizationService.Text("Dxvk.ConfirmBody") + "\n\n" + UiLocalizationService.Text("Dxvk.ConfirmReShade"), asked.Value.Body);
        Assert.StartsWith(UiLocalizationService.Format("Vm.InstallationCancelled", "DX9 HDR"), fx.Vm.StatusText, StringComparison.Ordinal);
    }

    // The component row's Install goes through the confirmation first (w7/dxvk-hdr hook in ManageComponentAsync):
    // declining it installs nothing.
    [Fact]
    public async Task RowInstall_AsksFirst_AndDecliningChangesNothing()
    {
        var root = Path.Combine(Path.GetTempPath(), "rhi-dxvk-row-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bin = Path.Combine(root, "Row Dx9", "bin");
            var prefix = Path.Combine(root, "Row Dx9", "pfx");
            Directory.CreateDirectory(bin);
            Directory.CreateDirectory(prefix);
            PeArchitectureDetectorTests.WritePe(Path.Combine(bin, "Game.exe"), 0x8664, 4096);
            File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n");
            var game = new DetectedGame
            {
                Name = "Row Dx9", InstallPath = Path.GetDirectoryName(bin)!, BinaryDirectory = bin, ExecutablePath = Path.Combine(bin, "Game.exe"),
                PrefixPath = prefix, Runner = "Proton", Source = "Test", GraphicsApi = GraphicsApi.DirectX9
            };
            using var fx = await ViewModelFixture.StartAsync([game]);
            var selected = fx.Vm.Games.Single(row => row.Game.Name == game.Name).Game;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnCompleted(AnalysisOutcome outcome) { if (outcome.Game.Name == game.Name) done.TrySetResult(); }
            fx.Vm.Session.Completed += OnCompleted;
            try
            {
                fx.Vm.SelectedGame = selected;
                if (!fx.Vm.Session.TryGetCached(selected, out _)) await done.Task.WaitAsync(TimeSpan.FromMinutes(2));
            }
            finally { fx.Vm.Session.Completed -= OnCompleted; }
            var row = fx.Vm.Session.Analysis!.Components.Single(c => c.Kind == ManagedComponentKind.DxvkHdr);
            Assert.True(row.ActionAllowed, row.Note);
            string? asked = null;
            fx.Vm.ConfirmAsync = (_, text, _) => { asked = text; return Task.FromResult(false); };

            await fx.Vm.ManageComponentCommand.ExecuteAsync(ManagedComponentKind.DxvkHdr);

            Assert.Equal(UiLocalizationService.Text("Dxvk.ConfirmBody"), asked);
            Assert.Null(fx.Records.Find(selected, ManagedComponentKind.DxvkHdr));
            Assert.False(File.Exists(Path.Combine(bin, "d3d9.dll")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    // Review major 1, at the VM: RHI's record whose d3d9.dll the user deleted (also on a game no longer DirectX 9) still
    // lists the row with Remove, and the row's command removes the rest (dxvk.conf, the record).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordWhoseD3d9IsGone_TheRowStillOffersRemove_AndRemoveClearsTheRest(bool noLongerDx9)
    {
        var root = Path.Combine(Path.GetTempPath(), "rhi-dxvk-gone-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bin = Path.Combine(root, "Gone Dx9", "bin");
            var prefix = Path.Combine(root, "Gone Dx9", "pfx");
            Directory.CreateDirectory(bin);
            Directory.CreateDirectory(prefix);
            PeArchitectureDetectorTests.WritePe(Path.Combine(bin, "Game.exe"), 0x8664, 4096);
            File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n");
            var conf = Path.Combine(bin, "dxvk.conf");
            File.WriteAllText(conf, DxvkHdrPresets.ConfFor(0));
            var game = new DetectedGame
            {
                Name = "Gone Dx9", InstallPath = Path.GetDirectoryName(bin)!, BinaryDirectory = bin, ExecutablePath = Path.Combine(bin, "Game.exe"),
                PrefixPath = prefix, Runner = "Proton", Source = "Test", GraphicsApi = noLongerDx9 ? GraphicsApi.DirectX11 : GraphicsApi.DirectX9
            };
            using var fx = await ViewModelFixture.StartAsync([game]);
            fx.Records.Upsert(new LinuxDeploymentRecord
            {
                GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.DxvkHdr,
                BinaryDirectory = bin, Version = "3.1-HDR-mod-v0.3.4",
                Files =
                [
                    new LinuxDeployedFile { Path = Path.Combine(bin, "d3d9.dll"), Sha256 = FileHash.Sha256HexOf(ReShadeChannelTests.DxvkHdrDll(0x8664)) },
                    new LinuxDeployedFile { Path = conf, Sha256 = FileHash.Sha256Hex(conf), AllowUserChanges = true }
                ]
            });
            var selected = fx.Vm.Games.Single(row => row.Game.Name == game.Name).Game;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnCompleted(AnalysisOutcome outcome) { if (outcome.Game.Name == game.Name) done.TrySetResult(); }
            // Start-up may already have analysed the game before the record existed: analyse it afresh.
            fx.Vm.SelectedGame = null;
            fx.Vm.Session.Invalidate(selected);
            fx.Vm.Session.Completed += OnCompleted;
            try
            {
                fx.Vm.SelectedGame = selected;
                if (!fx.Vm.Session.TryGetCached(selected, out _)) await done.Task.WaitAsync(TimeSpan.FromMinutes(2));
            }
            finally { fx.Vm.Session.Completed -= OnCompleted; }

            var shown = fx.Vm.Components.Single(row => row.Kind == ManagedComponentKind.DxvkHdr);
            Assert.True(shown.IsRemoveAction);
            Assert.Equal(ComponentAvailability.Installed, shown.Availability);
            fx.Vm.ConfirmAsync = (_, _, _) => Task.FromResult(true);

            await fx.Vm.ManageComponentCommand.ExecuteAsync(ManagedComponentKind.DxvkHdr);

            Assert.Null(fx.Records.Find(selected, ManagedComponentKind.DxvkHdr));
            Assert.False(File.Exists(conf));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task WithoutReShade_TheConfirmationDoesNotMentionIt()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var game = Game(fx, "NoReShade");
        string? body = null;
        fx.Vm.ConfirmAsync = (_, text, _) => { body = text; return Task.FromResult(true); };

        Assert.True(await fx.Vm.DxvkHdr.ConfirmInstallAsync(game));
        Assert.Equal(UiLocalizationService.Text("Dxvk.ConfirmBody"), body);
    }

    [Fact]
    public void PresetLabels_ArePlain_InBothLanguages()
    {
        try
        {
            foreach (var language in new[] { "en-US", "pt-BR" })
            {
                UiLocalizationService.Apply(language);
                Assert.Equal(6, OptionLists.DxvkHdrPreset.Items.Count);
                Assert.All(OptionLists.DxvkHdrPreset.Items, label => Assert.DoesNotMatch("(?i)scrgb|rgba|sfloat|unorm|hdr10", label));
            }
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }
}
