using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// w14/nr (#274): GITC Uplift as a Neural Rendering method (upstream RHI 2.9.2–2.9.4 UpliftService). The release listing and
/// the 2.0.0 zip's entry names and SHA256SUMS.txt are pinned from the real release (2026-11-16); the payloads are synthetic
/// files in that layout.
/// </summary>
public sealed class GitcUpliftDeploymentServiceTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void ParseReleases_ThePinnedListing_NewestFirst_WithDigestAndSums()
    {
        var releases = GitcUpliftDeploymentService.ParseReleases(Fixture("gitc-uplift-releases-2026-11-16.json"));

        Assert.Equal(["2.0.0", "1.8.0", "1.7.0"], releases.Select(r => r.Version));
        var newest = releases[0];
        Assert.Equal("v2.0.0", newest.Tag);
        Assert.Equal("GITC-Uplift-2.0.0.zip", newest.AssetName);
        Assert.Equal("1eac38a62339e993cc98fd4f4773dd8a327b098b47c4d21fc070a9d3d6aac255", newest.AssetSha256);
        Assert.EndsWith("/v2.0.0/SHA256SUMS.txt", newest.SumsUrl);
    }

    [Fact]
    public void ParseReleases_SkipsPreReleasesDraftsAndReleasesWithoutAnyDigest()
    {
        var json = JsonSerializer.Serialize(new object[]
        {
            new { tag_name = "v3.0.0-beta", prerelease = true, draft = false, assets = new[] { new { name = "GITC-Uplift-3.0.0.zip", browser_download_url = "https://github.com/x/a.zip", digest = "sha256:" + new string('a', 64) } } },
            new { tag_name = "v2.1.0", prerelease = false, draft = false, assets = new[] { new { name = "GITC-Uplift-2.1.0.zip", browser_download_url = "https://github.com/x/b.zip", digest = (string?)null } } },
            new { tag_name = "v2.0.0", prerelease = false, draft = false, assets = new[] { new { name = "GITC-Uplift-2.0.0.zip", browser_download_url = "https://github.com/x/c.zip", digest = "sha256:" + new string('c', 64) } } },
        });

        Assert.Equal(["2.0.0"], GitcUpliftDeploymentService.ParseReleases(json).Select(r => r.Version));
    }

    [Fact]
    public void ThePinnedZip_HoldsEveryFileRhiTakes_UnderTheNamesItLooksFor()
    {
        var entries = Fixture("gitc-uplift-2.0.0.entries.txt").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sums = Fixture("gitc-uplift-2.0.0-SHA256SUMS.txt");
        foreach (var (entry, _) in GitcUpliftDeploymentService.PayloadEntries)
        {
            Assert.Contains(entry, entries);
            Assert.NotNull(Rtx40MfgStandaloneDeploymentService.TryFindChecksum(sums, entry)); // each file has its own line
        }
        Assert.NotNull(Rtx40MfgStandaloneDeploymentService.TryFindChecksum(sums, "GITC-Uplift-2.0.0.zip"));
    }

    [Fact]
    public async Task Install_X64WithoutDlss_PlacesEverything_LaunchpadFirst_ThenRemovalPutsTheGameBack()
    {
        using var f = new UpliftFixture();
        var iniBefore = File.ReadAllBytes(f.IniPath);

        var result = await f.Service.InstallAsync(f.Game, new GitcUpliftOptions(null, null, Launchpad: true, EnableKey: 0x78));

        Assert.True(result.Success, result.Message);
        foreach (var path in new[] { "gitc-uplift.addon64", "gitc-uplift-helper64.exe", "GITC-Uplift/nvngx_dlssnr.dll",
                     "reshade-shaders/RHI/GITC-Uplift/Shaders/Uplift.fx", "reshade-shaders/RHI/GITC-Uplift/Shaders/UpliftMask.fx",
                     "reshade-shaders/RHI/iMMERSE-Launchpad/Shaders/MartysMods_LAUNCHPAD.fx",
                     "reshade-shaders/RHI/iMMERSE-Launchpad/Shaders/MartysMods/mmx_global.fxh",
                     "reshade-shaders/RHI/iMMERSE-Launchpad/Textures/iMMERSE_bluenoise_opt.png" })
            Assert.True(File.Exists(Path.Combine(f.Bin, path)), path);
        Assert.False(File.Exists(Path.Combine(f.Bin, "gitc-uplift.addon32")));
        var preset = File.ReadAllText(Path.Combine(f.Bin, "ReShadePreset.ini"));
        Assert.Equal(ImmerseLaunchpadService.Token + "," + GitcUpliftDeploymentService.TechniqueToken, ReShadePresetText.GetKey(preset, "", "Techniques"));
        var ini = File.ReadAllText(f.IniPath);
        Assert.Contains(GitcUpliftDeploymentService.EffectSearchPath, ini);
        Assert.Contains(ImmerseLaunchpadService.TextureSearchPath, ini);
        Assert.Equal("120", ReShadePresetText.GetKey(ini, "Uplift", "EnableKey"));
        var record = f.Records.Find(f.Game, ManagedComponentKind.GitcUplift)!;
        Assert.Equal("2.0.0", record.Version);
        Assert.Equal("1", record.Metadata["Uplift.Launchpad"]);
        Assert.Equal("1.0", record.Metadata[NeuralRenderingDllSwap.MetaDllVersion]);
        Assert.Equal(VerifiedDownload.PublishedDigest, record.Metadata[VerifiedDownload.MetadataIntegrityKey]);

        var remove = await f.Service.RemoveAsync(f.Game);

        Assert.True(remove.Success, remove.Message);
        Assert.Null(f.Records.Find(f.Game, ManagedComponentKind.GitcUplift));
        Assert.Equal(iniBefore, File.ReadAllBytes(f.IniPath));
        Assert.True(!File.Exists(Path.Combine(f.Bin, "ReShadePreset.ini")), remove.Message + " | " + string.Join(";", record.Files.Select(x => x.Path + "=" + x.AllowUserChanges))); // RHI created it, nobody changed it
        Assert.False(Directory.Exists(Path.Combine(f.Bin, "GITC-Uplift")));
        Assert.False(Directory.Exists(Path.Combine(f.Bin, "reshade-shaders", "RHI")));
        Assert.Equal(["Game.exe", "ReShade.ini", "dxgi.dll"], Directory.GetFiles(f.Bin).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Install_OpenGl_SwitchesUpliftOnFirst_InTheUsersPreset_AndRemovalTakesOnlyThatOut()
    {
        // Upstream's OpenGL MSAA set-up: NR runs at the Uplift technique, on ReShade's resolved single-sampled copy.
        using var f = new UpliftFixture(api: GraphicsApi.OpenGL, preset: "Techniques=SMAA@SMAA.fx\nTechniqueSorting=SMAA@SMAA.fx\n\n[SMAA.fx]\nEdgeDetectionThreshold=0.050000\n");
        var presetBefore = File.ReadAllBytes(f.PresetPath);

        var result = await f.Service.InstallAsync(f.Game, new GitcUpliftOptions(null, null, Launchpad: false, EnableKey: 0));

        Assert.True(result.Success, result.Message);
        Assert.Equal("Uplift@Uplift.fx,SMAA@SMAA.fx", ReShadePresetText.GetKey(File.ReadAllText(f.PresetPath), "", "Techniques"));
        Assert.Equal("off", f.Records.Find(f.Game, ManagedComponentKind.GitcUplift)!.Metadata["Uplift.Launchpad"]);
        Assert.False(Directory.Exists(ImmerseLaunchpadService.TreeRoot(f.Bin)));

        Assert.True((await f.Service.RemoveAsync(f.Game)).Success);
        Assert.Equal(presetBefore, File.ReadAllBytes(f.PresetPath));
    }

    [Fact]
    public async Task Install_ATechniqueTheUserHadOn_StaysOnAfterRemoval()
    {
        using var f = new UpliftFixture(api: GraphicsApi.OpenGL, preset: "Techniques=Uplift@Uplift.fx,SMAA@SMAA.fx\nTechniqueSorting=Uplift@Uplift.fx,SMAA@SMAA.fx\n");
        Assert.True((await f.Service.InstallAsync(f.Game, new GitcUpliftOptions(null, null, false, 0))).Success);
        Assert.Equal("user", f.Records.Find(f.Game, ManagedComponentKind.GitcUplift)!.Metadata["Uplift.Technique"]);

        Assert.True((await f.Service.RemoveAsync(f.Game)).Success);

        Assert.Equal("Uplift@Uplift.fx,SMAA@SMAA.fx", ReShadePresetText.GetKey(File.ReadAllText(f.PresetPath), "", "Techniques"));
    }

    [Fact]
    public async Task Install_32Bit_GetsTheAddon32AndTheHelper()
    {
        using var f = new UpliftFixture(x86: true, api: GraphicsApi.DirectX9);

        var result = await f.Service.InstallAsync(f.Game, new GitcUpliftOptions(null, null, false, 0));

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(Path.Combine(f.Bin, "gitc-uplift.addon32")));
        Assert.True(File.Exists(Path.Combine(f.Bin, "gitc-uplift-helper64.exe")));
        Assert.False(File.Exists(Path.Combine(f.Bin, "gitc-uplift.addon64")));
    }

    [Fact]
    public async Task Install_KeepsTheUsersOwnNrDll_InTheGitcUpliftFolder()
    {
        using var f = new UpliftFixture();
        var userDll = Path.Combine(f.Bin, "GITC-Uplift", "nvngx_dlssnr.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(userDll)!);
        var bytes = UpliftFixture.Pe(200 * 1024);
        File.WriteAllBytes(userDll, bytes);

        var result = await f.Service.InstallAsync(f.Game, new GitcUpliftOptions(null, null, false, 0));

        Assert.True(result.Success, result.Message);
        Assert.Contains(GitcUpliftDeploymentService.UserNrDllKeptNote.Trim(), result.Message);
        Assert.DoesNotContain(f.Records.Find(f.Game, ManagedComponentKind.GitcUplift)!.Files, file => file.Path == userDll);
        Assert.True((await f.Service.RemoveAsync(f.Game)).Success);
        Assert.Equal(bytes, File.ReadAllBytes(userDll));
    }

    [Fact]
    public async Task Install_AManualUpliftInTheGame_IsRefused_AndNothingChanges()
    {
        using var f = new UpliftFixture();
        File.WriteAllBytes(Path.Combine(f.Bin, "gitc-uplift.addon64"), UpliftFixture.Pe(100 * 1024));
        var iniBefore = File.ReadAllBytes(f.IniPath);

        var result = await f.Service.InstallAsync(f.Game, new GitcUpliftOptions(null, null, true, 0));

        Assert.False(result.Success);
        Assert.StartsWith(GitcUpliftDeploymentService.ExternalPrefix, result.Message);
        Assert.Equal(iniBefore, File.ReadAllBytes(f.IniPath));
        Assert.Empty(f.Requests);
    }

    [Fact]
    public async Task Install_AZipThatDoesNotMatchItsSha256Sums_IsRefusedBeforeAnyChange()
    {
        using var f = new UpliftFixture(tamperSums: true);
        var iniBefore = File.ReadAllBytes(f.IniPath);

        var result = await f.Service.InstallAsync(f.Game, new GitcUpliftOptions(null, null, false, 0));

        Assert.False(result.Success);
        Assert.Contains(GitcUpliftDeploymentService.SumsMismatchPrefix, result.Message);
        Assert.Equal(iniBefore, File.ReadAllBytes(f.IniPath));
        Assert.False(File.Exists(Path.Combine(f.Bin, "gitc-uplift.addon64")));
    }

    [Fact]
    public async Task Install_WhenTheRecordCannotBeSaved_RollsEverythingBack()
    {
        using var f = new UpliftFixture(preset: "Techniques=SMAA@SMAA.fx\n");
        var iniBefore = File.ReadAllBytes(f.IniPath);
        var presetBefore = File.ReadAllBytes(f.PresetPath);
        ComponentOperationResult result;
        using (LedgerWriteBlocker.Block(f.Records, f.RecordsPath))
            result = await f.Service.InstallAsync(f.Game, new GitcUpliftOptions(null, null, true, 0x78));

        Assert.False(result.Success);
        Assert.Equal(iniBefore, File.ReadAllBytes(f.IniPath));
        Assert.Equal(presetBefore, File.ReadAllBytes(f.PresetPath));
        Assert.False(File.Exists(Path.Combine(f.Bin, "gitc-uplift.addon64")));
        Assert.False(File.Exists(Path.Combine(f.Bin, "gitc-uplift-helper64.exe")));
    }

    [Fact]
    public async Task Update_MovesAPinnedOlderInstall_AddonHelperAndEffectsTogether()
    {
        using var f = new UpliftFixture();
        Assert.True((await f.Service.InstallAsync(f.Game, new GitcUpliftOptions("1.8.0", null, false, 0))).Success);
        Assert.Equal("1.8.0", f.Records.Find(f.Game, ManagedComponentKind.GitcUplift)!.Version);
        Assert.True((await f.Service.CheckUpdateAsync(f.Game, null)).UpdateAvailable);
        Assert.False((await f.Service.CheckUpdateAsync(f.Game, "1.8.0")).UpdateAvailable); // the pin is current

        var result = await f.Service.UpdateAsync(f.Game, null);

        Assert.True(result.Success, result.Message);
        foreach (var name in new[] { "gitc-uplift.addon64", "gitc-uplift-helper64.exe" })
            Assert.Equal(f.PayloadOf("2.0.0", name), File.ReadAllBytes(Path.Combine(f.Bin, name)));
        Assert.Equal(f.PayloadOf("2.0.0", "Uplift.fx"), File.ReadAllBytes(Path.Combine(GitcUpliftDeploymentService.TreeRoot(f.Bin), "Shaders", "Uplift.fx")));
        var record = f.Records.Find(f.Game, ManagedComponentKind.GitcUplift)!;
        Assert.Equal("2.0.0", record.Version);
        Assert.Equal("", record.Metadata["GitHub.ReleaseTagPin"]);
    }

    [Fact]
    public async Task Update_NeverWhileTheGameRuns_AndNeverHalfWay()
    {
        using var f = new UpliftFixture();
        Assert.True((await f.Service.InstallAsync(f.Game, new GitcUpliftOptions("1.8.0", null, false, 0))).Success);
        var addon = Path.Combine(f.Bin, "gitc-uplift.addon64");
        var helper = Path.Combine(f.Bin, "gitc-uplift-helper64.exe");
        var addonBefore = File.ReadAllBytes(addon);

        f.Running.Value = true;
        var running = await f.Service.UpdateAsync(f.Game, null);
        Assert.False(running.Success);
        Assert.Equal(GitcUpliftDeploymentService.RunningMessage, running.Message);

        // The helper changed outside RHI: neither file is replaced (Uplift 2.0 refuses a mismatched pair).
        f.Running.Value = false;
        File.AppendAllText(helper, "changed");
        var refused = await f.Service.UpdateAsync(f.Game, null);
        Assert.False(refused.Success);
        Assert.Equal(addonBefore, File.ReadAllBytes(addon));
        Assert.Equal("1.8.0", f.Records.Find(f.Game, ManagedComponentKind.GitcUplift)!.Version);
    }

    [Fact]
    public async Task Launchpad_OffThenOn_TakesOutOnlyWhatRhiPutIn()
    {
        using var f = new UpliftFixture(preset: "Techniques=SMAA@SMAA.fx\nTechniqueSorting=SMAA@SMAA.fx\n");
        Assert.True((await f.Service.InstallAsync(f.Game, new GitcUpliftOptions(null, null, true, 0))).Success);

        var off = await f.Service.SetLaunchpadAsync(f.Game, false);

        Assert.True(off.Success, off.Message);
        Assert.Equal("Uplift@Uplift.fx,SMAA@SMAA.fx", ReShadePresetText.GetKey(File.ReadAllText(f.PresetPath), "", "Techniques"));
        Assert.False(File.Exists(Path.Combine(ImmerseLaunchpadService.TreeRoot(f.Bin), "Shaders", "MartysMods_LAUNCHPAD.fx")));
        Assert.DoesNotContain(f.Records.Find(f.Game, ManagedComponentKind.GitcUplift)!.Files, file => ImmerseLaunchpadService.IsLaunchpadFile(file.Path));
        Assert.False(f.Service.LaunchpadOn(f.Game));

        var on = await f.Service.SetLaunchpadAsync(f.Game, true);

        Assert.True(on.Success, on.Message);
        Assert.StartsWith(ImmerseLaunchpadService.Token + ",Uplift@Uplift.fx", ReShadePresetText.GetKey(File.ReadAllText(f.PresetPath), "", "Techniques"));
        Assert.True(File.Exists(Path.Combine(ImmerseLaunchpadService.TreeRoot(f.Bin), "Shaders", "MartysMods_LAUNCHPAD.fx")));

        Assert.True((await f.Service.RemoveAsync(f.Game)).Success);
        Assert.Equal("SMAA@SMAA.fx", ReShadePresetText.GetKey(File.ReadAllText(f.PresetPath), "", "Techniques"));
    }

    [Fact]
    public async Task Launchpad_FilesAnotherComponentAlsoUses_StayWhenUpliftGoes()
    {
        using var f = new UpliftFixture();
        Assert.True((await f.Service.InstallAsync(f.Game, new GitcUpliftOptions(null, null, true, 0))).Success);
        var shared = f.Records.Find(f.Game, ManagedComponentKind.GitcUplift)!.Files.Where(file => ImmerseLaunchpadService.IsLaunchpadFile(file.Path)).ToList();
        // HSF (#277) lists the same Launchpad files in its record.
        f.Records.Upsert(new LinuxDeploymentRecord { GameKey = LinuxDeploymentRecordStore.GameKey(f.Game), GameName = f.Game.Name,
            Component = ManagedComponentKind.ReLimiter, BinaryDirectory = f.Bin, Version = "stand-in", Files = shared });

        Assert.True((await f.Service.RemoveAsync(f.Game)).Success);

        Assert.All(shared, file => Assert.True(File.Exists(file.Path), file.Path));
    }

    [Fact]
    public async Task KeepLaunchpadFirst_MovesItBackToTheTop_OnlyWhileTheGameIsNotRunning()
    {
        using var f = new UpliftFixture(preset: "Techniques=SMAA@SMAA.fx\nTechniqueSorting=SMAA@SMAA.fx\n");
        Assert.True((await f.Service.InstallAsync(f.Game, new GitcUpliftOptions(null, null, true, 0))).Success);
        // HSF switched Launchpad on again at the bottom of the list (ReShade appends).
        var lower = ReShadePresetText.SetKey(File.ReadAllText(f.PresetPath), "", "Techniques", "Uplift@Uplift.fx,SMAA@SMAA.fx,HSF@HSF.fx," + ImmerseLaunchpadService.Token);
        File.WriteAllText(f.PresetPath, lower);

        f.Running.Value = true;
        Assert.False(await f.Service.KeepLaunchpadFirstAsync(f.Game));
        Assert.Equal(lower, File.ReadAllText(f.PresetPath));

        f.Running.Value = false;
        Assert.True(await f.Service.KeepLaunchpadFirstAsync(f.Game));
        Assert.Equal(ImmerseLaunchpadService.Token + ",Uplift@Uplift.fx,SMAA@SMAA.fx,HSF@HSF.fx",
            ReShadePresetText.GetKey(File.ReadAllText(f.PresetPath), "", "Techniques"));
    }

    [Fact]
    public async Task WriteEnableKey_ThenRemoval_PutsTheGamesOwnKeyBack()
    {
        using var f = new UpliftFixture(iniExtra: "\n[Uplift]\nEnableKey=114\nResolution=75\n");
        Assert.True((await f.Service.InstallAsync(f.Game, new GitcUpliftOptions(null, null, false, 0))).Success);
        Assert.Equal("114", ReShadePresetText.GetKey(File.ReadAllText(f.IniPath), "Uplift", "EnableKey")); // None leaves it

        var written = await f.Service.WriteEnableKeyAsync(f.Game, 0x7A);
        Assert.True(written.Success, written.Message);
        Assert.Equal("122", ReShadePresetText.GetKey(File.ReadAllText(f.IniPath), "Uplift", "EnableKey"));

        Assert.True((await f.Service.RemoveAsync(f.Game)).Success);
        var ini = File.ReadAllText(f.IniPath);
        Assert.Equal("114", ReShadePresetText.GetKey(ini, "Uplift", "EnableKey"));
        Assert.Equal("75", ReShadePresetText.GetKey(ini, "Uplift", "Resolution"));
    }

    [Fact]
    public async Task Remove_RefusesAChangedAddon_AndChangesNothing()
    {
        using var f = new UpliftFixture();
        Assert.True((await f.Service.InstallAsync(f.Game, new GitcUpliftOptions(null, null, false, 0))).Success);
        File.AppendAllText(Path.Combine(f.Bin, "gitc-uplift.addon64"), "x");

        var result = await f.Service.RemoveAsync(f.Game);

        Assert.False(result.Success);
        Assert.StartsWith(GitcUpliftDeploymentService.ModifiedPrefix, result.Message);
        Assert.NotNull(f.Records.Find(f.Game, ManagedComponentKind.GitcUplift));
        Assert.True(File.Exists(Path.Combine(f.Bin, "gitc-uplift-helper64.exe")));
    }

    internal sealed class FakeRunning : IGameRunningProbe
    {
        public bool Value { get; set; }
        public bool IsRunning(DetectedGame game) => Value;
    }

    internal sealed class UpliftFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-uplift-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _http;
        private readonly Dictionary<string, byte[]> _responses = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Version, string Name), byte[]> _payloads = new();
        public string Bin { get; }
        public string IniPath => Path.Combine(Bin, "ReShade.ini");
        public string PresetPath => Path.Combine(Bin, "ReShadePreset.ini");
        public string RecordsPath { get; }
        public DetectedGame Game { get; }
        public LinuxDeploymentRecordStore Records { get; }
        public GitcUpliftDeploymentService Service { get; }
        public FakeRunning Running { get; } = new();
        public List<string> Requests { get; } = [];

        public UpliftFixture(bool x86 = false, GraphicsApi api = GraphicsApi.DirectX12, string? preset = null, bool tamperSums = false, string iniExtra = "")
        {
            Bin = Path.Combine(_root, "game");
            Directory.CreateDirectory(Bin);
            var exe = Path.Combine(Bin, "Game.exe");
            File.WriteAllBytes(exe, Pe(128 * 1024, x86 ? (ushort)0x14c : (ushort)0x8664));
            var reshade = Pe(2 * 1024 * 1024, x86 ? (ushort)0x14c : (ushort)0x8664);
            Encoding.ASCII.GetBytes("ReShade reshade.me crosire").CopyTo(reshade, 4096);
            File.WriteAllBytes(Path.Combine(Bin, "dxgi.dll"), reshade);
            File.WriteAllText(IniPath, "[GENERAL]\nEffectSearchPaths=.\\reshade-shaders\\Shaders\\**\nTextureSearchPaths=.\\reshade-shaders\\Textures\\**\n" + iniExtra);
            if (preset is not null) File.WriteAllText(PresetPath, preset);
            Game = new DetectedGame { Name = "Synthetic Uplift game", InstallPath = Bin, BinaryDirectory = Bin, ExecutablePath = exe,
                PrefixPath = Path.Combine(_root, "prefix"), Source = "Test", Runner = "Wine", GraphicsApi = api };
            Directory.CreateDirectory(Game.PrefixPath);
            RecordsPath = Path.Combine(_root, "deployments.json");
            Records = new LinuxDeploymentRecordStore(RecordsPath);

            var releases = new List<object>();
            foreach (var version in new[] { "2.0.0", "1.8.0" })
            {
                var zipUrl = $"https://github.com/ghostinthecamera/GITC-Uplift/releases/download/v{version}/GITC-Uplift-{version}.zip";
                var sumsUrl = $"https://github.com/ghostinthecamera/GITC-Uplift/releases/download/v{version}/SHA256SUMS.txt";
                var files = new List<(string, byte[])>();
                foreach (var entry in Fixture("gitc-uplift-2.0.0.entries.txt").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var name = Path.GetFileName(entry);
                    var bytes = name switch
                    {
                        "gitc-uplift.addon64" or "gitc-uplift-helper64.exe" => Pe(96 * 1024, 0x8664, version),
                        "gitc-uplift.addon32" => Pe(96 * 1024, 0x14c, version),
                        _ => Encoding.UTF8.GetBytes($"// {entry} {version}\n")
                    };
                    _payloads[(version, name)] = bytes;
                    files.Add((entry, bytes));
                }
                var zip = Zip(files.ToArray());
                var sums = new StringBuilder();
                foreach (var (entry, bytes) in files) sums.Append(Hash(bytes)).Append(" *").Append(entry).Append('\n');
                sums.Append(tamperSums ? new string('0', 64) : Hash(zip)).Append(" *GITC-Uplift-").Append(version).Append(".zip\n");
                _responses[zipUrl] = zip;
                _responses[sumsUrl] = Encoding.UTF8.GetBytes(sums.ToString());
                releases.Add(new { tag_name = "v" + version, draft = false, prerelease = false, assets = new object[]
                {
                    new { name = $"GITC-Uplift-{version}.zip", browser_download_url = zipUrl, digest = tamperSums ? null : "sha256:" + Hash(zip).ToLowerInvariant() },
                    new { name = "SHA256SUMS.txt", browser_download_url = sumsUrl, digest = (string?)null }
                } });
            }
            _responses[GitcUpliftDeploymentService.ReleasesApiUrl] = JsonSerializer.SerializeToUtf8Bytes(releases);
            _responses[DlssManagerService.ManifestUrl] = Encoding.UTF8.GetBytes("{\"dlss\":[{\"version\":\"1.0\",\"url\":\"https://github.com/fixtures/sr.zip\"}],\"dlssnr\":[{\"version\":\"1.0\",\"url\":\"https://github.com/fixtures/nr.zip\"}]}");
            _responses["https://github.com/fixtures/nr.zip"] = Zip(("nvngx_dlssnr.dll", Pe(128 * 1024)));
            _responses[ImmerseLaunchpadService.ArchiveUrl] = Zip(
                ("iMMERSE-main/Shaders/MartysMods_LAUNCHPAD.fx", Encoding.UTF8.GetBytes("#include \".\\MartysMods\\mmx_global.fxh\"\ntechnique MartysMods_Launchpad { }\n")),
                ("iMMERSE-main/Shaders/MartysMods/mmx_global.fxh", Encoding.UTF8.GetBytes("// global\n")),
                ("iMMERSE-main/Shaders/MartysMods_MXAO.fx", Encoding.UTF8.GetBytes("// not Launchpad\n")),
                ("iMMERSE-main/Textures/iMMERSE_bluenoise_opt.png", new byte[2048]),
                ("iMMERSE-main/Textures/AreaLUT.png", new byte[512]));

            _http = new HttpClient(new Handler(_responses, Requests));
            var dlss = new DlssManagerService(_http, new TransactionalFileDeploymentService(), Path.Combine(_root, "cache-dlss"));
            Service = new GitcUpliftDeploymentService(_http, Records, dlss, new ImmerseLaunchpadService(_http, Running),
                Path.Combine(_root, "cache-uplift"), running: Running);
        }

        public byte[] PayloadOf(string version, string name) => _payloads[(version, name)];

        public static byte[] Pe(int length, ushort machine = 0x8664, string? tag = null)
        {
            var bytes = new byte[length];
            bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
            BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3c);
            bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E';
            BitConverter.GetBytes(machine).CopyTo(bytes, 0x84);
            if (tag is not null) Encoding.ASCII.GetBytes(tag).CopyTo(bytes, 1024);
            return bytes;
        }

        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

        private static byte[] Zip(params (string Name, byte[] Bytes)[] entries)
        {
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var (name, bytes) in entries)
                {
                    using var output = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                    output.Write(bytes);
                }
            return stream.ToArray();
        }

        public void Dispose()
        {
            _http.Dispose();
            Directory.Delete(_root, recursive: true);
        }

        private sealed class Handler(Dictionary<string, byte[]> responses, List<string> requests) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var url = request.RequestUri!.AbsoluteUri;
                requests.Add(url);
                return Task.FromResult(responses.TryGetValue(url, out var payload)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        }
    }
}
