using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Ux;
using static RHI.Core.Tests.DroppedArchiveExtractorTests;

namespace RHI.Core.Tests;

/// <summary>
/// Wave-10 integration: where the three wave-10 features meet. A dropped Luma <c>.zip</c> takes the archive offer and
/// its drop dialog like every dropped or watched archive (one dialog for every archive); the archive install keeps the
/// landed note of the install it hands over to, and a held game's refusal reaches the archive install's line as is.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed partial class WaveTenIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-w10-int-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        UiLocalizationService.Apply("en-US");
        DroppedArchiveExtractor.DeleteFolder(_root);
    }

    private static string Scratch(ViewModelFixture fx) => Path.Combine(fx.Root, "cache", "dropped-archives");
    private static bool ScratchIsEmpty(ViewModelFixture fx) => !Directory.Exists(Scratch(fx)) || !Directory.EnumerateFileSystemEntries(Scratch(fx)).Any();
    private static string Folder(ViewModelFixture fx, string name) => Directory.CreateDirectory(Path.Combine(fx.Root, name)).FullName;

    private async Task<(ViewModelFixture Fx, DetectedGame Game)> StartAsync(string language = "en-US")
    {
        var fx = await ViewModelFixture.StartAsync([RenoDxLocalAddonTests.Game(Path.Combine(_root, "shell"), "Foo Game", 0x8664)], language);
        UiLocalizationService.Apply(language);
        fx.Managed.DroppedArchiveRoot = Scratch(fx);
        return (fx, fx.Vm.AllLibraryGames.Single(g => g.Name == "Foo Game"));
    }

    // Every dropped archive goes to the archive offer — a Luma .zip too (it used to get the old Luma package prompt for
    // the selected game); a single dropped add-on file keeps its own prompt.
    [Theory]
    [InlineData("Luma_Mod.zip", true)]
    [InlineData("luma-foo.zip", true)]
    [InlineData("renodx-foogame.zip", true)]
    [InlineData("Foo Game HDR.7z", true)]
    [InlineData("pack.rar", true)]
    [InlineData("Luma-Foo.addon64", false)]
    [InlineData("renodx-foogame.addon64", false)]
    [InlineData("Game.exe", false)]
    public void EveryDroppedArchive_GoesToTheArchiveOffer_ALumaZipIncluded(string fileName, bool archive)
        => Assert.Equal(archive, DroppedRenoDxPresenter.OffersDroppedArchive(LinuxDroppedFileClassifier.Classify(Path.Combine(_root, fileName))));

    // The dropped Luma .zip through that offer: the Luma add-on inside is offered in the drop dialog for the game it
    // names, RHI's copy of the whole archive goes to the Luma package install, and the dropped file stays.
    [Fact]
    public async Task ADroppedLumaZip_IsOfferedInTheArchiveDialog_AndInstalledFromRhisCopy_TheFileStays()
    {
        var (fx, game) = await StartAsync();
        using var _ = fx;
        var zip = Zip(Path.Combine(Folder(fx, "Downloads"), "Luma_FooGame.zip"), ("Luma/Luma-FooGame.addon64", AddonBytes()), ("Luma/shaders/a.hlsl", "s"u8.ToArray()));
        Assert.Equal(LinuxDroppedFileKind.LumaArchive, LinuxDroppedFileClassifier.Classify(zip));
        Assert.True(DroppedRenoDxPresenter.OffersDroppedArchive(LinuxDroppedFileClassifier.Classify(zip)));
        string? installed = null;
        fx.Managed.ArchiveLumaInstallForTesting = (target, path, _) =>
        {
            Assert.Same(game, target);
            Assert.NotEqual(zip, path); // RHI's own copy, never the user's file
            installed = Path.GetFileName(path);
            return Task.FromResult(ComponentOperationResult.Ok("Luma Framework installed."));
        };

        var offer = (await fx.Vm.DroppedRenoDx.OfferForArchiveAsync(fx.Vm, zip)).Offer!.AddOns.Single();

        Assert.True(offer.IsLuma);
        Assert.False(offer.DeleteSourceAfterInstall); // dropped, not watched
        Assert.Equal(UiLocalizationService.Text("Arc.LumaTitle"), offer.Title);
        Assert.Equal(0, offer.MatchedIndex);
        Assert.True(await fx.Vm.DroppedRenoDx.InstallAsync(fx.Vm, offer, game));
        Assert.Equal("Luma_FooGame.zip", installed);
        Assert.True(File.Exists(zip));
        Assert.True(ScratchIsEmpty(fx));
    }
}
