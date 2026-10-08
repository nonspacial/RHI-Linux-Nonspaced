using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Desktop.Localization;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Paths;

namespace RHI.Core.Tests;

/// <summary>#135 (upstream 2.8.1): nvngx_dlssg.dll in the OptiScaler FG stack and the Reflex INI keys.</summary>
public sealed partial class OptiScalerFgStackDlssgTests : IDisposable
{
    private const string DlssgRelative = "OptiScaler/Streamline/nvngx_dlssg.dll";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-optiscaler-fg-dlssg-tests-" + Guid.NewGuid().ToString("N"));

    public OptiScalerFgStackDlssgTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task EnableFgStack_DeploysNewestManifestDlssgAndDisableRemovesIt()
    {
        var fx = CreateFixture("fresh");
        var dlssg = Path.Combine(fx.Bin, "OptiScaler", "Streamline", "nvngx_dlssg.dll");

        var enabled = await fx.Service.SetAdvancedSettingAsync(fx.Game, OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId, "true");

        Assert.True(enabled.Success, enabled.Message);
        Assert.Equal(Dlssg(0x51), File.ReadAllBytes(dlssg)); // first (newest) manifest entry, not 310.1.0
        Assert.Contains("nvngx_dlssg.dll 310.5.0", enabled.Message);
        var record = fx.Store.Find(fx.Game, ManagedComponentKind.OptiScaler)!;
        Assert.Equal("310.5.0", record.Metadata[OptiScalerDeploymentService.FgStackDlssgVersionMetadataKey]);
        Assert.Contains(DlssgRelative, OwnedStackFiles(record));
        var owned = record.Files.Single(file => file.Path == dlssg);
        Assert.False(owned.ExistedBeforeInstall);
        Assert.Equal(Sha256(Dlssg(0x51)), owned.Sha256);

        var disabled = await fx.Service.SetAdvancedSettingAsync(fx.Game, OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId, "false");

        Assert.True(disabled.Success, disabled.Message);
        Assert.False(File.Exists(dlssg));
        Assert.False(Directory.Exists(Path.Combine(fx.Bin, "OptiScaler", "Streamline")));
        record = fx.Store.Find(fx.Game, ManagedComponentKind.OptiScaler)!;
        Assert.DoesNotContain(record.Files, file => file.Path == dlssg);
        Assert.False(record.Metadata.ContainsKey(OptiScalerDeploymentService.FgStackDlssgVersionMetadataKey));
    }

    [Fact]
    public async Task EnableFgStack_KeepsForeignDlssgAsOriginalAndDisableRestoresIt()
    {
        var fx = CreateFixture("foreign");
        var dlssg = Path.Combine(fx.Bin, "OptiScaler", "Streamline", "nvngx_dlssg.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dlssg)!);
        var foreign = Dlssg(0x7A);
        File.WriteAllBytes(dlssg, foreign);

        var enabled = await fx.Service.SetAdvancedSettingAsync(fx.Game, OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId, "true");

        Assert.True(enabled.Success, enabled.Message);
        Assert.Equal(Dlssg(0x51), File.ReadAllBytes(dlssg));
        Assert.Equal(foreign, File.ReadAllBytes(dlssg + ".original"));
        Assert.True(fx.Store.Find(fx.Game, ManagedComponentKind.OptiScaler)!.Files.Single(file => file.Path == dlssg).ExistedBeforeInstall);

        var disabled = await fx.Service.SetAdvancedSettingAsync(fx.Game, OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId, "false");

        Assert.True(disabled.Success, disabled.Message);
        Assert.Equal(foreign, File.ReadAllBytes(dlssg));
        Assert.False(File.Exists(dlssg + ".original"));
        Assert.False(File.Exists(Path.Combine(fx.Bin, "OptiScaler", "Streamline", "sl.interposer.dll")));
    }

    [Fact]
    public async Task RemoveOptiScaler_RestoresForeignDlssgKeptByTheFgStack()
    {
        var fx = CreateFixture("remove");
        var dlssg = Path.Combine(fx.Bin, "OptiScaler", "Streamline", "nvngx_dlssg.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dlssg)!);
        var foreign = Dlssg(0x7B);
        File.WriteAllBytes(dlssg, foreign);
        Assert.True((await fx.Service.SetAdvancedSettingAsync(fx.Game, OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId, "true")).Success);

        var removed = await fx.Service.RemoveAsync(fx.Game);

        Assert.True(removed.Success, removed.Message);
        Assert.Equal(foreign, File.ReadAllBytes(dlssg));
        Assert.False(File.Exists(dlssg + ".original"));
        Assert.False(File.Exists(Path.Combine(fx.Bin, "OptiScaler", DlssEnablerFile)));
        Assert.Null(fx.Store.Find(fx.Game, ManagedComponentKind.OptiScaler));
    }

    [Fact]
    public async Task EnableFgStack_FailureAfterKeepingAForeignFileRestoresIt()
    {
        var fx = CreateFixture("rollback");
        var streamline = Path.Combine(fx.Bin, "OptiScaler", "Streamline");
        Directory.CreateDirectory(streamline);
        var foreignCommon = Path.Combine(streamline, "sl.common.dll");
        File.WriteAllBytes(foreignCommon, Dlssg(0x33));
        // A directory where the enabler DLL goes passes preflight but makes the copy fail mid-deploy.
        Directory.CreateDirectory(Path.Combine(fx.Bin, "OptiScaler", DlssEnablerFile));
        var recordBefore = File.ReadAllText(fx.RecordsPath);

        var result = await fx.Service.SetAdvancedSettingAsync(fx.Game, OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId, "true");

        Assert.False(result.Success);
        Assert.Contains("rolled back", result.Message);
        Assert.Equal(Dlssg(0x33), File.ReadAllBytes(foreignCommon));
        Assert.False(File.Exists(foreignCommon + ".original"));
        Assert.False(File.Exists(Path.Combine(streamline, "sl.interposer.dll")));
        Assert.Equal(recordBefore, File.ReadAllText(fx.RecordsPath));
    }

    [Fact]
    public async Task EnableFgStack_WithoutManifestDlssgStillDeploysStreamlineAndSaysSo()
    {
        var fx = CreateFixture("no-dlssg", withDlssg: false);

        var enabled = await fx.Service.SetAdvancedSettingAsync(fx.Game, OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId, "true");

        Assert.True(enabled.Success, enabled.Message);
        Assert.Contains("nvngx_dlssg.dll was not deployed", enabled.Message);
        Assert.True(File.Exists(Path.Combine(fx.Bin, "OptiScaler", "Streamline", "sl.interposer.dll")));
        Assert.False(File.Exists(Path.Combine(fx.Bin, "OptiScaler", "Streamline", "nvngx_dlssg.dll")));
        var record = fx.Store.Find(fx.Game, ManagedComponentKind.OptiScaler)!;
        Assert.DoesNotContain(DlssgRelative, OwnedStackFiles(record));
        Assert.False(record.Metadata.ContainsKey(OptiScalerDeploymentService.FgStackDlssgVersionMetadataKey));
    }

    [Fact]
    public async Task FgStackMessages_ArePresentedInPortuguese()
    {
        var fx = CreateFixture("pt");
        var enabled = await fx.Service.SetAdvancedSettingAsync(fx.Game, OptiScalerDeploymentService.DeployStreamlineDlssEnablerSettingId, "true");

        var presented = UiLocalizationService.PresentFor("pt-BR", enabled.Message);

        Assert.Contains("implantados em OptiScaler/", presented);
        Assert.NotEqual(UiLocalizationService.UnlocalizedFallbackFor("pt-BR"), presented);
        foreach (var setting in fx.Service.GetAdvancedSettings(fx.Game).Settings.Where(s => s.Id is "ForceReflex" or "UseGamesReflexMarkers"))
        {
            Assert.NotEqual(setting.Label, UiLocalizationService.PresentFor("pt-BR", setting.Label));
            Assert.NotEqual(setting.Help, UiLocalizationService.PresentFor("pt-BR", setting.Help));
        }
    }

    [Fact]
    public void ReflexSettings_ShownForNightlyAfterNvngxAndHiddenForStable()
    {
        var nightly = CreateFixture("reflex-rows");
        var ids = nightly.Service.GetAdvancedSettings(nightly.Game).Settings.Select(setting => setting.Id).ToList();
        var stable = CreateFixture("reflex-stable", variant: OptiScalerVariant.Stable);
        var stableIds = stable.Service.GetAdvancedSettings(stable.Game).Settings.Select(setting => setting.Id).ToList();

        Assert.Equal(ids.IndexOf("FGNvngxReplacement") + 1, ids.IndexOf(OptiScalerDeploymentService.ForceReflexSettingId));
        Assert.Equal(ids.IndexOf("FGNvngxReplacement") + 2, ids.IndexOf(OptiScalerDeploymentService.UseGamesReflexMarkersSettingId));
        Assert.DoesNotContain(OptiScalerDeploymentService.ForceReflexSettingId, stableIds);
        Assert.DoesNotContain(OptiScalerDeploymentService.UseGamesReflexMarkersSettingId, stableIds);
    }

    [Fact]
    public void ReflexSettings_ReadAndWriteTheirIniKeys()
    {
        var fx = CreateFixture("reflex-ini");
        File.WriteAllText(fx.Ini, "[fakenvapi]\nForceReflex=auto\n[DLSSG]\nUseGamesReflexMarkers=auto\n[User]\nCustom=42\n");

        var before = fx.Service.GetAdvancedSettings(fx.Game).Settings;
        Assert.Equal("auto", before.Single(s => s.Id == "ForceReflex").CurrentValue);
        Assert.Equal(new[] { "auto", "0", "1", "2" }, before.Single(s => s.Id == "ForceReflex").Options.Select(o => o.Value));
        Assert.Equal("auto", before.Single(s => s.Id == "UseGamesReflexMarkers").CurrentValue);

        Assert.True(fx.Service.SetAdvancedSetting(fx.Game, OptiScalerDeploymentService.ForceReflexSettingId, "2").Success);
        Assert.True(fx.Service.SetAdvancedSetting(fx.Game, OptiScalerDeploymentService.UseGamesReflexMarkersSettingId, "false").Success);

        Assert.Equal("2", OptiScalerIniEditor.GetValue(fx.Bin, "fakenvapi", "ForceReflex"));
        Assert.Equal("false", OptiScalerIniEditor.GetValue(fx.Bin, "DLSSG", "UseGamesReflexMarkers"));
        Assert.Contains("Custom=42", File.ReadAllText(fx.Ini));
        var after = fx.Service.GetAdvancedSettings(fx.Game).Settings;
        Assert.Equal("2", after.Single(s => s.Id == "ForceReflex").CurrentValue);
        Assert.Equal("false", after.Single(s => s.Id == "UseGamesReflexMarkers").CurrentValue);

        var text = File.ReadAllText(fx.Ini);
        Assert.False(fx.Service.SetAdvancedSetting(fx.Game, OptiScalerDeploymentService.ForceReflexSettingId, "3").Success);
        Assert.False(fx.Service.SetAdvancedSetting(fx.Game, OptiScalerDeploymentService.UseGamesReflexMarkersSettingId, "maybe").Success);
        Assert.Equal(text, File.ReadAllText(fx.Ini));
    }

    private const string DlssEnablerFile = "dlss-enabler-headless.dll";

    private Fixture CreateFixture(string name, bool withDlssg = true, OptiScalerVariant variant = OptiScalerVariant.Nightly)
    {
        var root = Path.Combine(_root, name);
        var bin = Path.Combine(root, "game", "bin");
        var prefix = Path.Combine(root, "pfx");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(prefix);
        var exe = Path.Combine(bin, "Game.exe");
        PeArchitectureDetectorTests.WritePe(exe, 0x8664);
        File.WriteAllText(Path.Combine(prefix, "user.reg"), "WINE REGISTRY Version 2\n");
        var ini = Path.Combine(bin, "OptiScaler.ini");
        File.WriteAllText(ini, "[Plugins]\nLoadReshade=auto\n");
        var game = new DetectedGame
        {
            Name = "FG " + name,
            InstallPath = Path.Combine(root, "game"),
            BinaryDirectory = bin,
            ExecutablePath = exe,
            PrefixPath = prefix,
            Runner = "Proton",
            GraphicsApi = GraphicsApi.DirectX12
        };
        var recordsPath = Path.Combine(root, "records.json");
        var store = new LinuxDeploymentRecordStore(recordsPath);
        store.Upsert(new LinuxDeploymentRecord
        {
            GameKey = LinuxDeploymentRecordStore.GameKey(game),
            GameName = game.Name,
            Component = ManagedComponentKind.OptiScaler,
            BinaryDirectory = bin,
            Version = "1.0",
            SourceUrl = "https://example.invalid/OptiScaler.7z",
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [OptiScalerDeploymentService.VariantMetadataKey] = variant.ToString()
            },
            Files = [new LinuxDeployedFile { Path = ini, ExistedBeforeInstall = false, AllowUserChanges = true }]
        });

        var http = new HttpClient(new FixtureHandler(withDlssg));
        var files = new TransactionalFileDeploymentService();
        var dlss = new DlssManagerService(http, files, Path.Combine(root, "dlss-cache"));
        var enabler = new StandaloneDlssEnablerDeploymentService(
            http, store, new WineDllOverrideService(),
            new StandaloneDlssEnablerPreferenceStore(Path.Combine(root, "prefs.json")),
            Path.Combine(root, "enabler-cache"));
        var service = new OptiScalerDeploymentService(files, store, new WineDllOverrideService(), new NeverPackageProvider(), dlss, enabler);
        return new Fixture(bin, ini, recordsPath, game, store, service, dlss);
    }

    private static string[] OwnedStackFiles(LinuxDeploymentRecord record)
        => record.Metadata.TryGetValue(OptiScalerDeploymentService.FgStackFilesMetadataKey, out var json)
            ? JsonSerializer.Deserialize<string[]>(json) ?? []
            : [];

    private static byte[] Dlssg(byte marker) => Enumerable.Repeat(marker, 80 * 1024).ToArray();

    private static byte[] Pe64(byte marker, int size)
    {
        var bytes = Enumerable.Repeat(marker, size).ToArray();
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
        bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E'; bytes[0x82] = 0; bytes[0x83] = 0;
        BitConverter.GetBytes((ushort)0x8664).CopyTo(bytes, 0x84);
        return bytes;
    }

    private static byte[] Zip(params (string Name, byte[] Bytes)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in entries)
            {
                using var output = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                output.Write(bytes, 0, bytes.Length);
            }
        }
        return memory.ToArray();
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed record Fixture(
        string Bin,
        string Ini,
        string RecordsPath,
        DetectedGame Game,
        LinuxDeploymentRecordStore Store,
        OptiScalerDeploymentService Service,
        DlssManagerService Dlss);

    private sealed class NeverPackageProvider : IOptiScalerPackageProvider
    {
        public Task<OptiScalerPackage> GetLatestAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("FG stack tests must not resolve OptiScaler packages.");
    }

    /// <summary>Serves the DLSS manifest, Streamline/DLSSG zips and the DLSS Enabler release offline.</summary>
    private sealed class FixtureHandler(bool withDlssg) : HttpMessageHandler
    {
        private const string StreamlineUrl = "https://github.com/test/streamline-2.12.0.zip";
        private const string DlssgNewUrl = "https://github.com/test/dlssg-310.5.0.zip";
        private const string DlssgOldUrl = "https://github.com/test/dlssg-310.1.0.zip";
        private const string EnablerUrl = "https://example.invalid/DLSS-Enabler-4.10.0.zip";
        private static readonly byte[] EnablerZip = Zip(("version.dll", Pe64(0x44, 160 * 1024)));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            byte[]? body = url switch
            {
                DlssManagerService.ManifestUrl => Encoding.UTF8.GetBytes(Manifest()),
                StandaloneDlssEnablerDeploymentService.ReleasesApiUrl => Encoding.UTF8.GetBytes(Releases()),
                StreamlineUrl => Zip(("sl.interposer.dll", Dlssg(0x21)), ("sl.common.dll", Dlssg(0x22)), ("sl.dlss_g.dll", Dlssg(0x23))),
                DlssgNewUrl => Zip(("nvngx_dlssg.dll", Dlssg(0x51))),
                DlssgOldUrl => Zip(("nvngx_dlssg.dll", Dlssg(0x50))),
                EnablerUrl => EnablerZip,
                _ => null
            };
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }

        private string Manifest()
        {
            var dlssg = withDlssg
                ? $$"""[{ "version": "310.5.0", "url": "{{DlssgNewUrl}}" }, { "version": "310.1.0", "url": "{{DlssgOldUrl}}" }]"""
                : "[]";
            return $$"""{ "streamline": [{ "version": "2.12.0", "url": "{{StreamlineUrl}}" }], "dlssg": {{dlssg}} }""";
        }

        private static string Releases()
            => $$"""
               [{ "tag_name": "DLSS-Enabler-4.10.0", "draft": false, "prerelease": false,
                  "assets": [{ "name": "DLSS-Enabler-4.10.0.zip", "browser_download_url": "{{EnablerUrl}}",
                               "digest": "sha256:{{Sha256(EnablerZip).ToLowerInvariant()}}" }] }]
               """;
    }
}
