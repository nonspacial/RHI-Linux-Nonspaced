using System.Text.Json;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// #271: the OptiScaler version is an identity (upstream <c>OsVariants</c>: a built-in, <c>Fork:&lt;id&gt;</c> or
/// <c>Custom:&lt;slug&gt;</c>), no longer a fixed enum. Every value an older build saved keeps its meaning: the built-in
/// names, their numbers, and the four fork enum names the first wave-14 lane appended.
/// </summary>
public sealed class OptiScalerVersionIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-os-identity-" + Guid.NewGuid().ToString("N"));

    public OptiScalerVersionIdentityTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    [Theory]
    [InlineData("Stable", "Stable")]
    [InlineData("nightly", "Nightly")]
    [InlineData("DLSSNR", "DlssNr")]
    [InlineData("F5", "F5")]
    // The first lane's enum members (appended after F5, so stored as names or as 4–7).
    [InlineData("Venom", "Fork:venom")]
    [InlineData("NrPreSrMfg", "Fork:nr-presr-mfg")]
    [InlineData("FeederNr", "Fork:feeder-nr")]
    [InlineData("OpenNr", "Fork:opennr")]
    [InlineData("0", "Stable")]
    [InlineData("3", "F5")]
    [InlineData("4", "Fork:venom")]
    [InlineData("7", "Fork:opennr")]
    // Today's ids.
    [InlineData("Fork:some-new-fork", "Fork:some-new-fork")]
    [InlineData("Custom:my-build-2", "Custom:my-build-2")]
    public void StoredValues_KeepTheirMeaning(string stored, string id)
    {
        Assert.True(OptiScalerVariant.TryParse(stored, out var variant));
        Assert.Equal(id, variant.Id);
        Assert.Equal(id, variant.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("8")]
    [InlineData("-1")]
    [InlineData("Custom:../../etc")]
    [InlineData("Custom:My Build")]
    [InlineData("Fork:")]
    [InlineData("Fork:venom/../x")]
    [InlineData("SomethingElse")]
    public void ValuesThatAreNoVersion_AreRefused_SoNoValueCanNameAFolderOutsideRhis(string stored)
        => Assert.False(OptiScalerVariant.TryParse(stored, out _));

    [Fact]
    public void TheDefaultValue_IsStable_AsTheEnumsWas()
    {
        Assert.Equal(OptiScalerVariant.Stable, default);
        Assert.Equal("Stable", default(OptiScalerVariant).Id);
        Assert.True(OptiScalerVariant.Fork("venom") == OptiScalerVariant.Fork("venom"));
        Assert.Equal("fork-venom", OptiScalerVariant.Fork("venom").CacheFolder);
        Assert.Equal("dlssnr", OptiScalerVariant.DlssNr.CacheFolder); // the built-ins keep their cache folders
    }

    [Fact]
    public void PreferenceStore_ReadsTheOldFile_AndWritesTheIds()
    {
        var path = Path.Combine(_root, "optiscaler-variants.json");
        var venomGame = Game("Venom game");
        var nrGame = Game("NR game");
        var stableGame = Game("Stable game");
        // As the first lane (JsonStringEnumConverter) and earlier builds wrote it: names, and a number from a hand edit.
        File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [GameRecordIdentity.Key(venomGame)] = "Venom",
            [GameRecordIdentity.Key(nrGame)] = 2,
            [GameRecordIdentity.Key(stableGame)] = "Stable",
        }));
        var store = new OptiScalerVariantPreferenceStore(path);

        Assert.Equal(OptiScalerVariant.Fork("venom"), store.Get(venomGame));
        Assert.Equal(OptiScalerVariant.DlssNr, store.Get(nrGame));
        Assert.True(store.TryGet(stableGame, out var stable));
        Assert.Equal(OptiScalerVariant.Stable, stable);

        store.Set(stableGame, OptiScalerVariant.Custom("my-build"));
        var written = File.ReadAllText(path);
        Assert.Contains("\"Custom:my-build\"", written, StringComparison.Ordinal);
        Assert.Contains("\"Fork:venom\"", written, StringComparison.Ordinal); // the other entries are rewritten as ids
        Assert.Equal(OptiScalerVariant.Custom("my-build"), new OptiScalerVariantPreferenceStore(path).Get(stableGame));
    }

    [Theory]
    [InlineData("FeederNr", "Fork:feeder-nr")]
    [InlineData("DlssNr", "DlssNr")]
    [InlineData("Custom:my-build", "Custom:my-build")]
    [InlineData("garbage", "Stable")]
    public void InstallRecord_OldForkNames_ReadAsTheirManifestIds(string stored, string expected)
    {
        var record = new LinuxDeploymentRecord { Component = ManagedComponentKind.OptiScaler };
        record.Metadata[OptiScalerDeploymentService.VariantMetadataKey] = stored;

        Assert.Equal(expected, OptiScalerDeploymentService.GetInstalledVariant(record).Id);
    }

    [Fact]
    public void DefaultVersionSetting_KeepsAnyVersionId()
    {
        var settings = new LinuxAppSettings { OptiScalerDefaultVersion = "Custom:my-build" };
        settings.Normalize();
        Assert.Equal("Custom:my-build", settings.OptiScalerDefaultVersion);
        settings.OptiScalerDefaultVersion = "OpenNr";
        settings.Normalize();
        Assert.Equal("Fork:opennr", settings.OptiScalerDefaultVersion);
    }

    [Fact]
    public void ReleasePin_TheFirstLaneSavedForAFork_StillAppliesToThatFork()
    {
        var pins = new NeuralAddonVersionPreferenceStore(Path.Combine(_root, "pins.json"));
        var game = Game("Pinned fork");
        pins.Set(game, ManagedComponentKind.OptiScaler, "Venom+opti-venom-v0.4.1"); // as the first wave-14 lane wrote it
        var service = new LinuxManagedComponentService(
            records: new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json")), reshade: null!, addonManager: null!, renodx: null!,
            renodxUpgrade: null!, optiscaler: null!, relimiter: null!, displayCommander: null!, mfgAdaUnlock: null!, dlssg2030: null!,
            dlssEnablerStandalone: null!, rtx40Mfg: null!, dlss5Feeder: null!, dlss5Bridge: null!, d3dCompilerTools: null!,
            ultimateAsiLoader: null!, shortFuse: null!, dofFix: null!, reFramework: null!, lumaFramework: null!, lumaPolicy: null!,
            neuralVersionPins: pins);

        Assert.Equal("opti-venom-v0.4.1", service.GetOptiScalerReleasePin(game, OptiScalerVariant.Fork("venom")));
        Assert.Null(service.GetOptiScalerReleasePin(game, OptiScalerVariant.Fork("opennr")));

        service.SetOptiScalerReleasePin(game, OptiScalerVariant.Fork("venom"), "opti-venom-v0.4.2");
        Assert.Equal("fork.venom+opti-venom-v0.4.2", pins.Get(game, ManagedComponentKind.OptiScaler)); // the pin store takes no ":"
        Assert.Equal("opti-venom-v0.4.2", service.GetOptiScalerReleasePin(game, OptiScalerVariant.Fork("venom")));
    }

    private static DetectedGame Game(string name)
        => new() { Name = name, Source = "Steam", InstallPath = "/games/" + name, ExecutablePath = $"/games/{name}/game.exe" };
}
