using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>#157: the one-owner rule for nvngx_dlssnr.dll (the rule parity's NR Cost Scaler, #96, adopts).</summary>
public sealed class NeuralRenderingRuntimeOwnershipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-nr-ownership-" + Guid.NewGuid().ToString("N"));
    private readonly LinuxDeploymentRecordStore _records;
    private readonly DetectedGame _game;

    public NeuralRenderingRuntimeOwnershipTests()
    {
        Directory.CreateDirectory(_root);
        _records = new LinuxDeploymentRecordStore(Path.Combine(_root, "records.json"));
        _game = new DetectedGame { Name = "G", InstallPath = _root, BinaryDirectory = _root, Source = "Test", Runner = "Wine" };
    }

    private void Record(ManagedComponentKind kind, OptiScalerVariant? variant = null) => _records.Upsert(new LinuxDeploymentRecord
    {
        GameKey = LinuxDeploymentRecordStore.GameKey(_game), GameName = _game.Name, Component = kind, BinaryDirectory = _root, Files = [],
        Metadata = variant is { } v
            ? new(StringComparer.OrdinalIgnoreCase) { [OptiScalerDeploymentService.VariantMetadataKey] = v.ToString() }
            : new(StringComparer.OrdinalIgnoreCase)
    });

    [Theory]
    [InlineData(OptiScalerVariant.Stable, false)]
    [InlineData(OptiScalerVariant.Nightly, false)]
    [InlineData(OptiScalerVariant.DlssNr, true)]
    [InlineData(OptiScalerVariant.F5, true)]
    public void OnlyTheNrVariantsOfOptiScalerOwnTheRuntime(OptiScalerVariant variant, bool owns)
    {
        Record(ManagedComponentKind.OptiScaler, variant);

        Assert.Equal(owns, NeuralRenderingRuntimeOwnership.OptiScalerVariantOwnsRuntime(variant));
        Assert.Equal(owns ? "OptiScaler " + OptiScalerDeploymentService.VariantLabel(variant) : null,
            NeuralRenderingRuntimeOwnership.OwnerOtherThan(_records, _game, ManagedComponentKind.Dlss5Feeder));
    }

    [Theory]
    [InlineData(ManagedComponentKind.Dlss5Feeder, "DLSS5 Feeder")]
    [InlineData(ManagedComponentKind.ShortFuse, "ShortFuse")]
    public void FeederAndShortFuseOwnIt_ButNeverForThemselves(ManagedComponentKind owner, string name)
    {
        Record(owner);

        Assert.Equal(name, NeuralRenderingRuntimeOwnership.OwnerOtherThan(_records, _game, ManagedComponentKind.OptiScaler));
        Assert.Null(NeuralRenderingRuntimeOwnership.OwnerOtherThan(_records, _game, owner));
    }

    /// <summary>The Feeder side of the rule: Feeder Full refuses while OptiScaler F5 owns the runtime, before any work.</summary>
    [Fact]
    public async Task FeederFull_IsRefusedWhileOptiScalerF5OwnsTheRuntime()
    {
        var bin = Path.Combine(_root, "game");
        var prefix = Path.Combine(_root, "pfx");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(prefix);
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664, 128 * 1024);
        var game = new DetectedGame
        {
            Name = "Feeder F5", InstallPath = _root, BinaryDirectory = bin, ExecutablePath = exe, PrefixPath = prefix,
            Source = "Test", Runner = "Wine", GraphicsApi = GraphicsApi.DirectX11
        };
        _records.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game), GameName = game.Name, Component = ManagedComponentKind.OptiScaler, BinaryDirectory = bin, Files = [],
            Metadata = new(StringComparer.OrdinalIgnoreCase) { [OptiScalerDeploymentService.VariantMetadataKey] = "F5" }
        });
        using var http = new HttpClient(new NoNetwork());
        var dlss = new RHI.Core.Services.DlssManagerService(http, new RHI.Core.Services.TransactionalFileDeploymentService(), Path.Combine(_root, "dlss"));
        var manifest = new UpstreamRhiManifestService(http, "https://manifest.invalid/rhi.json");
        var addon = new UpstreamRhiAddonDeploymentService(http, _records, manifest, new UpstreamRhiAddonDeploymentOptions(
            ManagedComponentKind.Dlss5Feeder, "dlss5-feed", "DLSS5 Feeder", "https://release.invalid/latest", 64 * 1024,
            DeployFileName64: "dlss5-feed.addon64"), Path.Combine(_root, "addon"));
        var feeder = new Dlss5FeederFullDeploymentService(http, _records, dlss, addon, Path.Combine(_root, "feeder"), new WineDllOverrideService());

        var result = await feeder.InstallAsync(game, Dlss5FeederConsumer.RenoDxDlss5, deepFriedChickenPackagePath: null, d3dCompiler47Path: null);

        Assert.Equal(NeuralRenderingRuntimeOwnership.RefusalFor("OptiScaler F5 DLSSNR"), result.Message);
        Assert.Null(_records.Find(game, ManagedComponentKind.Dlss5Feeder));
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("the conflict must be found before any request");
    }

    [Fact]
    public void NoOwner_WhenNothingDeploysTheRuntime()
        => Assert.Null(NeuralRenderingRuntimeOwnership.OwnerOtherThan(_records, _game, ManagedComponentKind.OptiScaler));

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
