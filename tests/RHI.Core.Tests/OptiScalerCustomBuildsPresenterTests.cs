using System.Text;
using RHI.Core.Models;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #271 Settings › Custom OptiScaler builds, as the card drives it (upstream 2.9.2 SettingsHandler.OsCustomBuilds): name
/// prompt, link checks, rows with facts, the DLSS NR DLL tick, Rename, Remove with its question, and Replace offering to
/// update the games that have the build.
/// </summary>
public sealed class OptiScalerCustomBuildsPresenterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-os-custom-vm-" + Guid.NewGuid().ToString("N"));

    public OptiScalerCustomBuildsPresenterTests() => Directory.CreateDirectory(_root);

    private readonly List<IDisposable> _flows = [];

    public void Dispose()
    {
        foreach (var flow in _flows) flow.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public async Task AddRenameTickAndRemove_ThroughTheCard()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var (presenter, prompts) = Presenter(fx);

        // A link must be pasted and must be a web link.
        Assert.False(await presenter.AddLinkAsync());
        Assert.Equal("Paste a download link first.", presenter.Status);
        presenter.LinkText = "/home/me/build.zip";
        Assert.False(await presenter.AddLinkAsync());
        Assert.Equal("That isn't a web link (it should start with https://).", presenter.Status);
        Assert.True(presenter.StatusIsError);

        // Add a folder: the name prompt offers the folder's name; cancelling it adds nothing.
        var folder = Build("Venom 1107");
        prompts.Names.Enqueue(null);
        Assert.False(await presenter.AddPathAsync(folder));
        Assert.Empty(presenter.Rows);
        prompts.Names.Enqueue("Venom");
        Assert.True(await presenter.AddPathAsync(folder));
        Assert.Equal("Venom 1107", prompts.Suggested[^1]);
        Assert.Equal("Added Venom. Pick it in a game's OptiScaler settings.", presenter.Status);
        var row = Assert.Single(presenter.Rows);
        Assert.Equal("Venom", row.Name);
        Assert.Contains("OptiScaler 10 layout", row.Facts, StringComparison.Ordinal);
        Assert.Contains("From: " + folder, row.Tip, StringComparison.Ordinal);
        Assert.Contains(row.Build.Variant, OptionLists.OptiScalerVariant.Values); // a version in every list at once

        prompts.Names.Enqueue("Venom 1108");
        await presenter.RenameAsync(row.Build);
        presenter.SetNeedsNrDll(row.Build, true);
        row = Assert.Single(presenter.Rows);
        Assert.Equal(("Venom 1108", true), (row.Name, row.NeedsNrDll));
        Assert.Equal("Custom: Venom 1108", OptionLists.OptiScalerVariant.Display(row.Build.Variant));

        prompts.Confirms.Enqueue(false);
        await presenter.RemoveAsync(row.Build);
        Assert.Single(presenter.Rows);
        Assert.Equal("Its files are deleted from RHI.", prompts.Messages[^1]);
        prompts.Confirms.Enqueue(true);
        await presenter.RemoveAsync(row.Build);
        Assert.Empty(presenter.Rows);
        Assert.Equal("Removed Venom 1108.", presenter.Status);
    }

    [Fact]
    public async Task Replace_OffersToUpdateTheGamesThatHaveTheBuild()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var (presenter, prompts) = Presenter(fx);
        prompts.Names.Enqueue("Mine");
        await presenter.AddPathAsync(Build("v1"));
        var build = Assert.Single(presenter.Rows).Build;

        // No game has it: Replace asks nothing more.
        Assert.True(await presenter.ReplaceFromFileAsync(build) is false); // no file picked
        prompts.Files.Enqueue(Build("v2"));
        Assert.True(await presenter.ReplaceFromFileAsync(build));
        Assert.Empty(prompts.Messages);
        Assert.Equal("Replaced Mine.", presenter.Status);

        // A library game has it installed: Replace offers the update; "Not now" changes nothing.
        var game = fx.Game("Uses it");
        var (added, message) = await fx.Vm.ManualGames.TryAddManualGameAsync(new()
        {
            Name = game.Name, InstallPath = game.InstallPath, ExecutablePath = game.ExecutablePath, Runner = "Manual Windows"
        });
        Assert.True(added, message);
        var libraryGame = fx.Vm.AllLibraryGames.Single(candidate => candidate.Name == game.Name);
        var record = new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(libraryGame), GameName = game.Name, Component = ManagedComponentKind.OptiScaler,
            BinaryDirectory = game.BinaryDirectory!
        };
        record.Metadata[OptiScalerDeploymentService.VariantMetadataKey] = build.Variant.Id;
        fx.Records.Upsert(record);
        Assert.Equal(1, Assert.Single(presenter.Rows).UsedBy);
        Assert.Contains("used by 1 game", presenter.Rows[0].Facts, StringComparison.Ordinal);

        prompts.Files.Enqueue(Build("v3"));
        prompts.Confirms.Enqueue(false);
        Assert.True(await presenter.ReplaceFromFileAsync(build));
        Assert.Equal("Uses it has Mine installed. Put the new files in now? Your OptiScaler.ini settings are kept.", prompts.Messages[^1]);
        Assert.Equal("Replaced Mine.", presenter.Status);

        // "Update": the game gets the new files through the update transaction, under the name its record says.
        prompts.Files.Enqueue(Build("v4"));
        prompts.Confirms.Enqueue(true);
        Assert.True(await presenter.ReplaceFromFileAsync(build));
        Assert.Equal("Updated 1 game(s).", presenter.Status);
        Assert.Contains("v4", File.ReadAllText(Path.Combine(game.BinaryDirectory!, "dxgi.dll")), StringComparison.Ordinal);
        Assert.Equal(build.Variant, fx.Managed.GetInstalledOptiScalerVariant(libraryGame));

        // Removing a build a game has says so.
        prompts.Confirms.Enqueue(false);
        await presenter.RemoveAsync(build);
        Assert.StartsWith("1 game(s) have it installed.", prompts.Messages[^1], StringComparison.Ordinal);
    }

    // w14 final review m6: Replace, Remove and the NR tick wait for the app's operation gate (an install copies from the
    // build's folder); adding a new build does not.
    [Fact]
    public async Task ReplaceRemoveAndTick_AreRefusedWhileAnotherOperationRuns()
    {
        using var fx = await ViewModelFixture.StartAsync([]);
        var (presenter, prompts) = Presenter(fx);
        prompts.Names.Enqueue("Mine");
        Assert.True(await presenter.AddPathAsync(Build("g1")));
        var build = Assert.Single(presenter.Rows).Build;
        var dll = Path.Combine(build.Directory, "OptiScaler.dll");
        var before = File.ReadAllBytes(dll);

        using (var running = ((IPanelHost)fx.Vm).BeginOperation("component"))
        {
            Assert.NotNull(running);
            prompts.Files.Enqueue(Build("g2"));
            Assert.False(await presenter.ReplaceFromFileAsync(build));
            Assert.Equal(RHI.Desktop.Localization.UiLocalizationService.Text("Vm.AnotherRHIOperationAlreadyRunning"), presenter.Status);
            prompts.Confirms.Enqueue(true);
            await presenter.RemoveAsync(build);
            presenter.SetNeedsNrDll(build, true);
            prompts.Names.Enqueue("Other");
            Assert.True(await presenter.AddPathAsync(Build("g3"))); // a new build is not gated
        }

        Assert.Equal(before, File.ReadAllBytes(dll));
        var mine = presenter.Rows.Single(row => row.Name == "Mine");
        Assert.False(mine.NeedsNrDll);
        prompts.Files.Enqueue(Build("g2"));
        Assert.True(await presenter.ReplaceFromFileAsync(mine.Build)); // free again
        Assert.Contains("g2", File.ReadAllText(dll), StringComparison.Ordinal);
    }

    private (OptiScalerCustomBuildsPresenter Presenter, FakePrompts Prompts) Presenter(ViewModelFixture fx)
    {
        var presenter = fx.Vm.OptiScalerCustomBuilds;
        var store = new OptiScalerCustomBuildStore(Path.Combine(_root, "store-" + Guid.NewGuid().ToString("N")));
        presenter.Store = store;
        _flows.Add(OptiScalerCustomBuilds.UseInThisFlow(store)); // the version lists read the same store, as in the app
        var prompts = new FakePrompts();
        presenter.Prompts = prompts;
        return (presenter, prompts);
    }

    private string Build(string name)
    {
        var root = Path.Combine(_root, "sources", name);
        Directory.CreateDirectory(Path.Combine(root, "OptiScaler"));
        File.WriteAllText(Path.Combine(root, "OptiScaler", "libxess.dll"), "MZ");
        var dll = new byte[96 * 1024];
        dll[0] = (byte)'M';
        dll[1] = (byte)'Z';
        Encoding.ASCII.GetBytes("optiscaler/OptiScaler " + name).CopyTo(dll, 128);
        File.WriteAllBytes(Path.Combine(root, "OptiScaler.dll"), dll);
        File.WriteAllText(Path.Combine(root, "OptiScaler.ini"), "[Plugins]\n");
        return root;
    }

    private sealed class FakePrompts : IOptiScalerCustomBuildPrompts
    {
        public Queue<string?> Names { get; } = new();
        public Queue<bool> Confirms { get; } = new();
        public Queue<string?> Files { get; } = new();
        public List<string> Suggested { get; } = [];
        public List<string> Messages { get; } = [];

        public Task<string?> AskNameAsync(string title, string suggested)
        {
            Suggested.Add(suggested);
            return Task.FromResult(Names.Count > 0 ? Names.Dequeue() : suggested);
        }

        public Task<bool> ConfirmAsync(string title, string message, string accept)
        {
            Messages.Add(message);
            return Task.FromResult(Confirms.Count > 0 && Confirms.Dequeue());
        }

        public Task<string?> PickFileAsync() => Task.FromResult(Files.Count > 0 ? Files.Dequeue() : null);

        public Task<string?> PickFolderAsync() => Task.FromResult(Files.Count > 0 ? Files.Dequeue() : null);
    }
}
