using System.Text;
using System.Text.Json;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Core.Services;
using RHI.Platform.Linux.Deployment;
using RHI.Platform.Linux.Settings;

namespace RHI.Core.Tests;

/// <summary>
/// Refs #90, w3-scan review findings 1–2: the one Xbox write gate covers every path that changes an Xbox title. Job
/// rule (the installer app has a job queued for the title, its job list cannot be read, or its instance is unknown) on every
/// entry point; protected-file rule in the shared write primitives, so a writer other than ReShade is refused too.
/// </summary>
public sealed class XboxWriteGateTests : IDisposable
{
    private const string StoreId = "9GATE";
    private readonly DetectionTestTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private static readonly byte[] Encrypted = [0x13, 0x37, .. Enumerable.Repeat((byte)0xA5, 4096)];
    private static readonly byte[] Dll = [(byte)'M', (byte)'Z', .. Enumerable.Repeat((byte)0x11, 2048)];

    public enum Mode { Busy, Garbled, UnknownInstance }

    private sealed record Fixture(DetectedGame Game, string Dir, string Jobs, string Expected);

    /// <summary>An installer-app title with DLSS, Streamline and ReShade files, registered as a scan would, its job list in <paramref name="mode"/>.</summary>
    private Fixture Title(Mode mode)
    {
        var dir = _tree.Dir("payloads", StoreId);
        _tree.File(JsonSerializer.Serialize(new { storeId = StoreId }), "payloads", StoreId, XboxInstallerApp.OwnedMarker);
        _tree.File("msixvc", "payloads", StoreId, ".xodus-streaming.msixvc");
        _tree.Bytes(Encrypted, "payloads", StoreId, "Game.exe");
        _tree.Bytes(Dll, "payloads", StoreId, "nvngx_dlss.dll");
        _tree.Bytes(Dll, "payloads", StoreId, "nvngx_dlss.dll.original");
        _tree.Bytes(Dll, "payloads", StoreId, "sl", "sl.interposer.dll");
        _tree.Bytes(Dll, "payloads", StoreId, "sl", "sl.interposer.dll.original");
        _tree.File("[INPUT]\nKeyOverlay=1,0,0,0\n", "payloads", StoreId, "ReShade.ini");
        var env = _tree.Dir("envs", "default", StoreId);
        _tree.Dir("envs", "default", StoreId, "prefix", "drive_c");
        _tree.File("{}", "envs", "default", StoreId, XboxInstallerApp.EnvironmentFile);
        var jobs = _tree.Path_("state", "jobs.json");
        _tree.File(mode == Mode.Garbled
            ? "{ \"queue\": [ trunc"
            : JsonSerializer.Serialize(new { queue = new[] { new { kind = "Update", titleId = StoreId, state = "Running" } } }), "state", "jobs.json");
        var package = new XboxPackageInfo(StoreId, XboxWriteGate.InstallerApp, null, mode == Mode.UnknownInstance ? null : jobs);
        XboxWriteGate.Register(package, dir, env);
        var game = new DetectedGame
        {
            Name = "Gate", Source = "Xbox", InstallPath = dir, BinaryDirectory = dir, ExecutablePath = Path.Combine(dir, "Game.exe"),
            PrefixPath = Path.Combine(env, "prefix"), XboxPackage = package
        };
        var expected = mode switch
        {
            Mode.Busy => XboxWriteGate.BusyMessage,
            Mode.Garbled => XboxWriteGate.UnreadablePrefix + jobs,
            _ => XboxWriteGate.UnknownInstancePrefix
        };
        return new Fixture(game, dir, jobs, expected);
    }

    private static void AssertRefusal(Fixture fx, string? message)
    {
        Assert.NotNull(message);
        if (fx.Expected == XboxWriteGate.UnknownInstancePrefix) Assert.StartsWith(fx.Expected, message);
        else Assert.Equal(fx.Expected, message);
    }

    private string Snapshot(string dir)
        => string.Join("\n", Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(f => f + "=" + FileHash.Sha256Hex(f)));

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            throw new HttpRequestException("offline test");
        }
    }

    private DlssManagerService Dlss(CountingHandler handler)
        => new(new HttpClient(handler), new TransactionalFileDeploymentService(), _tree.Path_("cache-" + Guid.NewGuid().ToString("N")));

    private sealed class NotRunning : IGameRunningProbe
    {
        public bool IsRunning(DetectedGame game) => false;
    }

    // ---- job rule: one row per entry point, each with a queued job, a garbled job list and an unknown instance ------------

    [Theory]
    [InlineData(Mode.Busy)]
    [InlineData(Mode.Garbled)]
    [InlineData(Mode.UnknownInstance)]
    public async Task ManagedComponentChange_IsRefused(Mode mode)
    {
        var fx = Title(mode);
        var service = new LinuxManagedComponentService(new LinuxDeploymentRecordStore(_tree.Path_("deployments.json")), reshade: null!,
            addonManager: null!, renodx: null!, renodxUpgrade: null!, optiscaler: null!, relimiter: null!, displayCommander: null!,
            mfgAdaUnlock: null!, dlssg2030: null!, dlssEnablerStandalone: null!, rtx40Mfg: null!, dlss5Feeder: null!, dlss5Bridge: null!,
            d3dCompilerTools: null!, ultimateAsiLoader: null!, shortFuse: null!, dofFix: null!, reFramework: null!, lumaFramework: null!,
            lumaPolicy: null!);

        var result = await service.ApplyReShadeAddonSelectionAsync(fx.Game, []);

        Assert.False(result.Success);
        AssertRefusal(fx, result.Message);
    }

    [Theory]
    [InlineData(Mode.Busy)]
    [InlineData(Mode.Garbled)]
    [InlineData(Mode.UnknownInstance)]
    public async Task DlssSwap_IsRefused_BeforeItsDownload(Mode mode)
    {
        var fx = Title(mode);
        var before = Snapshot(fx.Dir);
        var handler = new CountingHandler();

        var result = await Dlss(handler).SwapAsync(DlssComponentKind.SuperResolution, [Path.Combine(fx.Dir, "nvngx_dlss.dll")], "310.1.0");

        Assert.False(result.Success);
        AssertRefusal(fx, result.Error);
        Assert.Equal(0, handler.Requests);
        Assert.Equal(before, Snapshot(fx.Dir));
    }

    [Theory]
    [InlineData(Mode.Busy)]
    [InlineData(Mode.Garbled)]
    [InlineData(Mode.UnknownInstance)]
    public async Task DlssRestore_IsRefused(Mode mode)
    {
        var fx = Title(mode);
        var before = Snapshot(fx.Dir);
        using var cancel = new OperationCancellation(CancellationToken.None);

        var result = await Dlss(new CountingHandler()).RestoreAsync(DlssComponentKind.SuperResolution, [Path.Combine(fx.Dir, "nvngx_dlss.dll")], cancel.Token);

        Assert.False(result.Success);
        AssertRefusal(fx, result.Error);
        Assert.False(cancel.WritesStarted); // refused before the write phase, not by a rollback
        Assert.Equal(before, Snapshot(fx.Dir));
    }

    [Theory]
    [InlineData(Mode.Busy)]
    [InlineData(Mode.Garbled)]
    [InlineData(Mode.UnknownInstance)]
    public async Task StreamlineSwap_IsRefused_BeforeItsDownload(Mode mode)
    {
        var fx = Title(mode);
        var before = Snapshot(fx.Dir);
        var handler = new CountingHandler();

        var result = await Dlss(handler).SwapStreamlineAsync([Path.Combine(fx.Dir, "sl")], "2.7.2");

        Assert.False(result.Success);
        AssertRefusal(fx, result.Error);
        Assert.Equal(0, handler.Requests);
        Assert.Equal(before, Snapshot(fx.Dir));
    }

    [Theory]
    [InlineData(Mode.Busy)]
    [InlineData(Mode.Garbled)]
    [InlineData(Mode.UnknownInstance)]
    public async Task StreamlineRestore_IsRefused(Mode mode)
    {
        var fx = Title(mode);
        var before = Snapshot(fx.Dir);
        using var cancel = new OperationCancellation(CancellationToken.None);

        var result = await Dlss(new CountingHandler()).RestoreStreamlineAsync([Path.Combine(fx.Dir, "sl")], cancel.Token);

        Assert.False(result.Success);
        AssertRefusal(fx, result.Error);
        Assert.False(cancel.WritesStarted);
        Assert.Equal(before, Snapshot(fx.Dir));
    }

    [Theory]
    [InlineData(Mode.Busy)]
    [InlineData(Mode.Garbled)]
    [InlineData(Mode.UnknownInstance)]
    public async Task DlssDefaults_QuickApplyAndRestoreAll_AreRefused_AndBatchesReportTheGameAsFailed(Mode mode)
    {
        var fx = Title(mode);
        var before = Snapshot(fx.Dir);
        var handler = new CountingHandler();
        var service = new LinuxDlssDefaultsService(Dlss(handler), new LinuxDeploymentRecordStore(_tree.Path_("deployments.json")), new NotRunning());
        var defaults = new LinuxDlssVersionDefaults(SuperResolution: "310.1.0", Streamline: "2.7.2");

        var quick = await Assert.ThrowsAsync<XboxTitleBusyException>(() => service.ApplyDefaultsAsync(fx.Game, defaults));
        AssertRefusal(fx, quick.Message);
        var restoreAll = await Assert.ThrowsAsync<XboxTitleBusyException>(() => service.RestoreAllAsync(fx.Game));
        AssertRefusal(fx, restoreAll.Message);

        var batch = Assert.Single((await service.ApplyBatchAsync([fx.Game], defaults)).Items);
        Assert.False(batch.Success);
        Assert.Contains(quick.Message, batch.Message, StringComparison.Ordinal);
        var restoreBatch = Assert.Single((await service.RestoreBatchAsync([fx.Game])).Items);
        Assert.False(restoreBatch.Success);
        Assert.Contains(quick.Message, restoreBatch.Message, StringComparison.Ordinal);

        Assert.Equal(0, handler.Requests);
        Assert.Equal(before, Snapshot(fx.Dir));
    }

    [Theory]
    [InlineData(Mode.Busy)]
    [InlineData(Mode.Garbled)]
    [InlineData(Mode.UnknownInstance)]
    public void ReShadeGlobalSettings_AreNotWritten(Mode mode)
    {
        var fx = Title(mode);
        var before = Snapshot(fx.Dir);

        var error = Assert.Throws<XboxTitleBusyException>(() => ReShadeGlobalSettingsApplier.ApplyToGame(new LinuxAppSettings(), fx.Game));

        AssertRefusal(fx, error.Message);
        Assert.Equal(before, Snapshot(fx.Dir));
    }

    [Theory]
    [InlineData(Mode.Busy)]
    [InlineData(Mode.Garbled)]
    [InlineData(Mode.UnknownInstance)]
    public void RepairAfterMove_IsRefused(Mode mode)
    {
        var fx = Title(mode);
        var records = new LinuxDeploymentRecordStore(_tree.Path_("deployments.json"));

        var result = new DeploymentRelocation(records).Repair(fx.Game, [], out var repaired);

        Assert.False(result.Success);
        AssertRefusal(fx, result.Message);
        Assert.Empty(repaired);
    }

    [Theory]
    [InlineData(Mode.Busy)]
    [InlineData(Mode.Garbled)]
    [InlineData(Mode.UnknownInstance)]
    public async Task APathTakenIntoADeployment_IsRefused_InTheGameFolderAndThePrefix(Mode mode)
    {
        var fx = Title(mode);
        var set = new OwnedFileSet();

        var inGame = await Assert.ThrowsAsync<XboxTitleBusyException>(() => set.TrackAsync(Path.Combine(fx.Dir, "dxgi.dll"), CancellationToken.None));
        AssertRefusal(fx, inGame.Message);
        var inPrefix = await Assert.ThrowsAsync<XboxTitleBusyException>(() =>
            set.TrackAsync(Path.Combine(fx.Game.PrefixPath!, "drive_c", "windows", "system32", "d3dcompiler_47.dll"), CancellationToken.None));
        AssertRefusal(fx, inPrefix.Message);
        Assert.Empty(set.Files);
    }

    [Fact]
    public async Task AJobQueuedMidOperation_StopsTheNextFile_ButNeverTheRollback()
    {
        var fx = Title(Mode.Busy);
        File.WriteAllText(fx.Jobs, "{\"queue\":[]}");
        var first = Path.Combine(fx.Dir, "nvngx_dlss.dll");
        var original = FileHash.Sha256Hex(first);
        var set = new OwnedFileSet();
        await set.TrackAsync(first, CancellationToken.None);
        AtomicFile.WriteAllBytes(first, [(byte)'M', (byte)'Z', 1, 2, 3]);

        // Installer-app queues an update now: the next file is refused, and putting the first one back still works.
        File.WriteAllText(fx.Jobs, JsonSerializer.Serialize(new { queue = new[] { new { kind = "Update", titleId = StoreId } } }));
        await Assert.ThrowsAsync<XboxTitleBusyException>(() => set.TrackAsync(Path.Combine(fx.Dir, "dxgi.dll"), CancellationToken.None));
        var rollback = await set.RollbackAsync();

        Assert.True(rollback.Success, string.Join("; ", rollback.Failures));
        Assert.Equal(original, FileHash.Sha256Hex(first));
    }

    [Fact]
    public void IdleTitle_AndOtherGames_PassTheGate()
    {
        var fx = Title(Mode.Busy);
        File.WriteAllText(fx.Jobs, JsonSerializer.Serialize(new { queue = new[] { new { kind = "Install", titleId = "9OTHER" } } }));

        Assert.Null(XboxWriteGate.Refusal(fx.Game));
        Assert.Null(XboxWriteGate.Refusal(Path.Combine(fx.Dir, "dxgi.dll")));
        var steam = _tree.Dir("steam", "common", "Game");
        Assert.Null(XboxWriteGate.Refusal(new DetectedGame { Name = "Steam", InstallPath = steam, BinaryDirectory = steam }));
        Assert.Null(XboxWriteGate.Refusal(Path.Combine(steam, "dxgi.dll")));
        // An Xodus-only install has no job list to read: only its protected files are refused.
        var xodus = _tree.Dir("Games", "xodus-game");
        _tree.File("m", "Games", "xodus-game", ".xodus-streaming.msixvc");
        XboxWriteGate.Register(new XboxPackageInfo("9XO", "Xodus", null, null), xodus);
        Assert.Null(XboxWriteGate.Refusal(new DetectedGame { Name = "X", InstallPath = xodus, XboxPackage = new XboxPackageInfo("9XO", "Xodus", null, null) }));
        Assert.Null(XboxWriteGate.Refusal(Path.Combine(xodus, "dxgi.dll")));
    }

    [Fact]
    public void AManualCardInsideAnUnregisteredInstallerAppFolder_IsRefusedAsUnknown()
    {
        var dir = _tree.Dir("elsewhere", "9LOOSE");
        _tree.File(JsonSerializer.Serialize(new { storeId = "9LOOSE" }), "elsewhere", "9LOOSE", XboxInstallerApp.OwnedMarker);
        var bin = _tree.Dir("elsewhere", "9LOOSE", "Binaries", "Win64");

        var refusal = XboxWriteGate.Refusal(new DetectedGame { Name = "Manual", Source = "Manual", InstallPath = bin, BinaryDirectory = bin });

        Assert.Equal(XboxWriteGate.UnknownInstancePrefix + PathIdentity.Canonicalize(dir), refusal);
    }

    // ---- protected-file rule in the shared primitives ----------------------------------------------------------------------

    /// <summary>An Xodus package (no job rule) with its protected files.</summary>
    private (string Dir, string Exe, string Config) XodusPackage()
    {
        var dir = _tree.Dir("Games", "pkg");
        _tree.File("msixvc", "Games", "pkg", ".xodus-streaming.msixvc");
        var config = _tree.File("<Game />", "Games", "pkg", "MicrosoftGame.config");
        var exe = _tree.Bytes(Encrypted, "Games", "pkg", "Bin", "Game.exe");
        return (dir, exe, config);
    }

    public static TheoryData<string> Primitives => new()
    {
        "WriteAllBytes", "WriteAllBytesAsync", "WriteAllText", "ReplaceUserFile", "Copy", "CopyAsync", "StageCopyAsync",
        "StageStreamAsync", "MoveIntoPlace", "RenameFrom", "RenameTo", "MoveDurableFrom", "MoveDurableTo", "Delete"
    };

    [Theory]
    [MemberData(nameof(Primitives))]
    public async Task EveryAtomicFilePrimitive_RefusesAProtectedPackageFile(string primitive)
    {
        var (dir, exe, config) = XodusPackage();
        var source = _tree.Bytes(Dll, "src", "payload.dll");
        var before = Snapshot(dir);
        var aside = Path.Combine(dir, "Bin", "moved.bin");

        Task Run() => primitive switch
        {
            "WriteAllBytes" => Task.Run(() => AtomicFile.WriteAllBytes(exe, Dll)),
            "WriteAllBytesAsync" => AtomicFile.WriteAllBytesAsync(exe, Dll),
            "WriteAllText" => Task.Run(() => AtomicFile.WriteAllText(config, "<Game x='1' />")),
            "ReplaceUserFile" => Task.Run(() => AtomicFile.ReplaceUserFile(config, Encoding.UTF8.GetBytes("<Game />"))),
            "Copy" => Task.Run(() => AtomicFile.Copy(source, exe)),
            "CopyAsync" => AtomicFile.CopyAsync(source, exe),
            "StageCopyAsync" => AtomicFile.StageCopyAsync(source, exe, null, null, CancellationToken.None),
            "StageStreamAsync" => AtomicFile.StageStreamAsync(new MemoryStream(Dll), exe, null, null, long.MaxValue, CancellationToken.None),
            "MoveIntoPlace" => Task.Run(() => AtomicFile.MoveIntoPlace(source, exe)),
            "RenameFrom" => Task.Run(() => AtomicFile.Rename(exe, aside)),
            "RenameTo" => Task.Run(() => AtomicFile.Rename(source, config, overwrite: true)),
            "MoveDurableFrom" => Task.Run(() => AtomicFile.MoveDurable(exe, aside)),
            "MoveDurableTo" => Task.Run(() => AtomicFile.MoveDurable(source, Path.Combine(dir, "appxmanifest.xml"))),
            "Delete" => Task.Run(() => AtomicFile.Delete(exe)),
            _ => throw new ArgumentOutOfRangeException(nameof(primitive))
        };

        var error = await Assert.ThrowsAsync<ProtectedPackageFileException>(Run);

        Assert.StartsWith(PackageFileProtection.RefusalPrefix, error.Message);
        Assert.Equal(before, Snapshot(dir));
        Assert.True(File.Exists(source));
    }

    /// <summary>
    /// Contract: every public AtomicFile member is either one of the primitives checked above or classified here as one
    /// that writes no game file (or only delegates to a checked one). A new public writer fails until it is placed.
    /// </summary>
    [Fact]
    public void EveryPublicAtomicFileMember_IsAClassifiedPrimitive()
    {
        string[] checkedAbove = ["WriteAllBytes", "WriteAllBytesAsync", "WriteAllText", "ReplaceUserFile", "Copy", "CopyAsync", "StageCopyAsync",
            "StageStreamAsync", "MoveIntoPlace", "Rename", "MoveDurable", "Delete"];
        string[] notGameWriters =
        [
            "TempPathFor", "ResolveUserFileTarget", "FsyncDirectoryOf", "FsyncDirectory", "CreateDirectoryDurable",
            "CanCreateFilesIn", // asks access(2); writes nothing
            "WriteStoreFile", "WriteNew", // RHI's own store files and quarantine copies
            "WriteAllTextAsync", "ReplaceUserFileLines" // delegate to WriteAllBytesAsync / ReplaceUserFile
        ];
        var members = typeof(AtomicFile).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(method => method.Name).Distinct().Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(checkedAbove.Concat(notGameWriters).Order(StringComparer.Ordinal), members);
        Assert.All(checkedAbove.Where(name => name != "Rename" && name != "MoveDurable"), name => Assert.Contains(name, ((IEnumerable<object[]>)Primitives).Select(row => (string)row[0])));
    }

    [Fact]
    public async Task TheDelegatingWriters_AreRefusedToo()
    {
        var (dir, exe, config) = XodusPackage();
        var before = Snapshot(dir);

        await Assert.ThrowsAsync<ProtectedPackageFileException>(() => AtomicFile.WriteAllTextAsync(exe, "x"));
        Assert.Throws<ProtectedPackageFileException>(() => AtomicFile.ReplaceUserFileLines(config, ["<Game />"]));

        Assert.Equal(before, Snapshot(dir));
    }

    [Fact]
    public void Cleanup_NeverDeletesAProtectedFile()
    {
        var (_, exe, _) = XodusPackage();

        Assert.False(FileCleanup.TryDelete(exe, out var error));

        Assert.IsType<ProtectedPackageFileException>(error);
        Assert.True(File.Exists(exe));
    }

    [Fact]
    public async Task ADllSwapOntoAProtectedFile_IsRefused_NotOnlyReShade()
    {
        var (dir, exe, config) = XodusPackage();
        var source = _tree.Bytes(Dll, "src", "payload.dll");
        var before = Snapshot(dir);
        var deployment = new TransactionalFileDeploymentService();
        using var singleCancel = new OperationCancellation(CancellationToken.None);
        using var batchCancel = new OperationCancellation(CancellationToken.None);

        var single = await deployment.ReplaceAsync(source, config, singleCancel.Token);
        var batch = await deployment.ReplaceBatchAsync([(source, Path.Combine(dir, "Bin", "dxgi.dll")), (source, exe)], batchCancel.Token);

        Assert.False(single.Success);
        Assert.Equal(PackageFileProtection.RefusalPrefix + Path.GetFullPath(config), single.Error);
        Assert.False(batch.Success);
        Assert.StartsWith(PackageFileProtection.RefusalPrefix, batch.Error);
        // Refused whole, before the write phase: the batch's first, harmless file was never written.
        Assert.False(singleCancel.WritesStarted);
        Assert.False(batchCancel.WritesStarted);
        Assert.Equal(before, Snapshot(dir));
    }

    [Fact]
    public async Task AnInPlaceSwapOfAProtectedRecordedFile_IsRefused()
    {
        var (dir, exe, _) = XodusPackage();
        var source = _tree.Bytes(Dll, "src", "payload.dll");
        var before = Snapshot(dir);
        var record = new LinuxDeploymentRecord
        {
            GameKey = dir, Component = ManagedComponentKind.OptiScaler, BinaryDirectory = Path.GetDirectoryName(exe)!,
            Files = [new LinuxDeployedFile { Path = exe, Sha256 = FileHash.Sha256Hex(exe) }]
        };

        await Assert.ThrowsAsync<ProtectedPackageFileException>(() => ManagedFileSwap.ReplaceAsync(record, [(exe, source)], _ => { }));

        Assert.Equal(before, Snapshot(dir));
    }
}
