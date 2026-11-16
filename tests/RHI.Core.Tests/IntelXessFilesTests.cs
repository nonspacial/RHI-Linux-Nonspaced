using System.Net;
using RHI.Core.Deployment;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// w14/extras-fg: the shared Intel XeSS file service behind Universal MFG Unlocker (#278), MFG Unlock (#279) and RTX
/// Encore (#280). Downloads are pinned by SHA-256; placement adds missing files or replaces the game's copy after keeping
/// it as a backup; removal undoes only what still holds RHI's bytes.
/// </summary>
public sealed class IntelXessFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rhi-xess-" + Guid.NewGuid().ToString("N"));

    public IntelXessFilesTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    // Pinned at upstream RHI 2.9.4 (Rtx40MfgService.EncoreIntel.cs, RTX Encore Hub's catalogue) and checked against
    // intel/xess v3.0.2 on 2026-11-16 (contents API sizes, downloaded SHA-256).
    [Fact]
    public void Pins_MatchIntelsXessSdk302()
    {
        Assert.Equal("v3.0.2", IntelXessFiles.SdkTag);
        Assert.Equal(("libxess.dll", 77795704L, "251659DD84A3E84DE67C886A4186E01F3ECA49B00641906FE38BB6B807E5D5B7"),
            (IntelXessFiles.LibXess.Name, IntelXessFiles.LibXess.Size, IntelXessFiles.LibXess.Sha256));
        Assert.Equal(("libxess_fg.dll", 22957432L, "EC5E0C65E075570C6EDE72618BB666D0BE0C2E10B2EA9762C0FE8CB8E375AB27"),
            (IntelXessFiles.LibXessFg.Name, IntelXessFiles.LibXessFg.Size, IntelXessFiles.LibXessFg.Sha256));
        Assert.Equal(("libxell.dll", 415368L, "D2030DCD694FDA8F2EC7E044B13E6DB8F0B56D4BA9113A5EFAD334E3F3DED8C7"),
            (IntelXessFiles.LibXell.Name, IntelXessFiles.LibXell.Size, IntelXessFiles.LibXell.Sha256));
        Assert.Equal(("LICENSE.txt", 4194L, "784BE42C39A4A4D03CF85D88E15E59D75828FCA773CE45B8DBFFFBD7EAF208DF"),
            (IntelXessFiles.License.Name, IntelXessFiles.License.Size, IntelXessFiles.License.Sha256));
        Assert.Equal("https://raw.githubusercontent.com/intel/xess/v3.0.2/bin/libxell.dll", IntelXessFiles.LibXell.Url);
        Assert.Equal([IntelXessFiles.LibXessFg, IntelXessFiles.LibXell], IntelXessFiles.FrameGeneration);
        Assert.Equal([IntelXessFiles.LibXess, IntelXessFiles.LibXessFg, IntelXessFiles.LibXell], IntelXessFiles.Encore);
        Assert.True(DownloadSources.IntelXess.IsTrusted(IntelXessFiles.LibXess.Url));
        Assert.False(DownloadSources.IntelXess.IsTrusted("https://github.com/intel/xess/raw/v3.0.2/bin/libxess.dll"));
        Assert.Contains(DownloadSources.IntelXess, DownloadSources.All);
    }

    private (IntelXessFile File, byte[] Bytes) FakeDll(string name, byte marker, ushort machine = 0x8664)
    {
        var path = Path.Combine(_root, "src-" + name);
        PeArchitectureDetectorTests.WritePe(path, machine, 2048);
        var bytes = File.ReadAllBytes(path);
        bytes[^1] = marker;
        File.WriteAllBytes(path, bytes);
        return (new IntelXessFile(name, "bin/" + name, bytes.Length, FileHash.Sha256Hex(path)), bytes);
    }

    private static HttpClient Serve(IReadOnlyDictionary<string, byte[]> files, List<string>? requests = null)
        => new(new Handler(request =>
        {
            requests?.Add(request.RequestUri!.AbsoluteUri);
            var name = Path.GetFileName(request.RequestUri!.AbsolutePath);
            return files.TryGetValue(name, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes), RequestMessage = request }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
        }));

    [Fact]
    public async Task EnsureCached_DownloadsOnce_AndRefusesAWrongDigestOrA32BitDll()
    {
        var (fg, fgBytes) = FakeDll("libxess_fg.dll", 1);
        var (xell, xellBytes) = FakeDll("libxell.dll", 2);
        var requests = new List<string>();
        var service = new IntelXessFiles(Serve(new Dictionary<string, byte[]> { ["libxess_fg.dll"] = fgBytes, ["libxell.dll"] = xellBytes }, requests), Path.Combine(_root, "cache"));

        var cached = await service.EnsureCachedAsync([fg, xell]);
        await service.EnsureCachedAsync([fg, xell]);

        Assert.Equal(fgBytes, File.ReadAllBytes(cached[fg]));
        Assert.Equal(2, requests.Count);
        Assert.Equal("https://raw.githubusercontent.com/intel/xess/v3.0.2/bin/libxess_fg.dll", requests[0]);

        var wrong = fg with { Name = "libxess.dll", RepoPath = "bin/libxess.dll" }; // the served bytes do not hash to the pin
        var refused = await Assert.ThrowsAsync<InvalidDataException>(() => new IntelXessFiles(
            Serve(new Dictionary<string, byte[]> { ["libxess.dll"] = xellBytes }), Path.Combine(_root, "cache2")).EnsureCachedAsync([wrong]));
        Assert.StartsWith(IntelXessFiles.DownloadFailedPrefix + "libxess.dll", refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, "cache2", "v3.0.2", "libxess.dll")));

        var (x86, x86Bytes) = FakeDll("libxell.dll", 3, 0x14c);
        var notX64 = await Assert.ThrowsAsync<InvalidDataException>(() => new IntelXessFiles(
            Serve(new Dictionary<string, byte[]> { ["libxell.dll"] = x86Bytes }), Path.Combine(_root, "cache3")).EnsureCachedAsync([x86]));
        Assert.Contains(IntelXessFiles.NotX64Prefix + "libxell.dll", notX64.Message, StringComparison.Ordinal);
    }

    private async Task<IReadOnlyDictionary<IntelXessFile, string>> Cache(params (IntelXessFile File, byte[] Bytes)[] files)
        => await new IntelXessFiles(Serve(files.ToDictionary(f => f.File.Name, f => f.Bytes)), Path.Combine(_root, "cache-" + Guid.NewGuid().ToString("N")))
            .EnsureCachedAsync(files.Select(f => f.File));

    [Fact]
    public async Task Add_CreatesTheFolder_LeavesOtherFilesAlone_AndRemoveTakesBackOnlyRhisFiles()
    {
        var fg = FakeDll("libxess_fg.dll", 1);
        var xell = FakeDll("libxell.dll", 2);
        var cached = await Cache(fg, xell);
        var folder = Path.Combine(_root, "game", "RTXMFG-XeSS");

        await using (var scope = DeploymentScope.Unjournaled())
        {
            var placed = await IntelXessFiles.PlaceAsync(scope, [new(fg.File, Path.Combine(folder, fg.File.Name)), new(xell.File, Path.Combine(folder, xell.File.Name))], cached);
            await scope.CommitAsync();
            Assert.Equal(4, placed.Entries.Count); // both folder levels RHI created and both files
            Assert.Empty(placed.KeptUserFiles);
            Assert.Equal(fg.Bytes, File.ReadAllBytes(Path.Combine(folder, "libxess_fg.dll")));

            await using var remove = DeploymentScope.Unjournaled();
            var removed = await IntelXessFiles.RemoveAsync(remove, placed.Entries);
            await remove.CommitAsync();
            IntelXessFiles.DeleteEmptyFolders(placed.Entries);
            Assert.Empty(IntelXessFiles.Notes(removed));
            Assert.False(Directory.Exists(folder));
        }

        // The user's own copy already there (other bytes) stays and is named; RHI places only the missing one.
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "libxell.dll"), "user's own");
        await using var again = DeploymentScope.Unjournaled();
        var second = await IntelXessFiles.PlaceAsync(again, [new(fg.File, Path.Combine(folder, fg.File.Name)), new(xell.File, Path.Combine(folder, xell.File.Name))], cached);
        await again.CommitAsync();
        Assert.Equal([Path.Combine(folder, "libxell.dll")], second.KeptUserFiles);
        Assert.Equal(["libxess_fg.dll"], second.Entries.Select(e => e.File));
        Assert.Contains(IntelXessFiles.KeptUserFilesPrefix, IntelXessFiles.Notes(second), StringComparison.Ordinal);

        await using var removeAgain = DeploymentScope.Unjournaled();
        await IntelXessFiles.RemoveAsync(removeAgain, second.Entries);
        await removeAgain.CommitAsync();
        Assert.Equal("user's own", File.ReadAllText(Path.Combine(folder, "libxell.dll")));
        Assert.False(File.Exists(Path.Combine(folder, "libxess_fg.dll")));
    }

    [Fact]
    public async Task Replace_KeepsTheGamesCopyAsABackup_AndRemovalPutsItBack()
    {
        var fg = FakeDll("libxess_fg.dll", 1);
        var cached = await Cache(fg);
        var target = Path.Combine(_root, "game", "libxess_fg.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "game's older copy");
        var backup = target + ".mfgada-original";

        await using var scope = DeploymentScope.Unjournaled();
        var placed = await IntelXessFiles.PlaceAsync(scope, [new(fg.File, target, backup)], cached);
        await scope.CommitAsync();

        Assert.Equal(fg.Bytes, File.ReadAllBytes(target));
        Assert.Equal("game's older copy", File.ReadAllText(backup));
        var entry = Assert.Single(placed.Entries);
        Assert.Equal((IntelXessPlacement.Replaced, backup), (entry.Placement, entry.Backup));

        // Recorded in the component's metadata and read back.
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        IntelXessFiles.Save(metadata, "MfgUnlock", placed.Entries);
        var loaded = IntelXessFiles.Load(metadata, "MfgUnlock");
        Assert.Equal((entry.Path, entry.Sha256, entry.Backup), (loaded[0].Path, loaded[0].Sha256, loaded[0].Backup));

        await using var remove = DeploymentScope.Unjournaled();
        await IntelXessFiles.RemoveAsync(remove, loaded);
        await remove.CommitAsync();
        Assert.Equal("game's older copy", File.ReadAllText(target));
        Assert.False(File.Exists(backup));
    }

    [Fact]
    public async Task Replace_LeavesAnIdenticalCopy_AndNeverOverwritesABackup()
    {
        var fg = FakeDll("libxess_fg.dll", 1);
        var cached = await Cache(fg);
        var same = Path.Combine(_root, "same", "libxess_fg.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(same)!);
        File.WriteAllBytes(same, fg.Bytes);
        var taken = Path.Combine(_root, "taken", "libxess_fg.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(taken)!);
        File.WriteAllText(taken, "game's copy");
        File.WriteAllText(taken + ".encore-original", "an older backup");

        await using var scope = DeploymentScope.Unjournaled();
        var placed = await IntelXessFiles.PlaceAsync(scope, [new(fg.File, same, same + ".encore-original"), new(fg.File, taken, taken + ".encore-original")], cached);
        await scope.CommitAsync();

        Assert.Empty(placed.Entries);
        Assert.False(File.Exists(same + ".encore-original"));
        Assert.Equal("game's copy", File.ReadAllText(taken));
        Assert.Equal("an older backup", File.ReadAllText(taken + ".encore-original"));
        Assert.Equal([taken + ".encore-original"], placed.BackupInTheWay);
    }

    // A file the game replaced after RHI placed it (a game update) is left with its backup; the message names both.
    [Fact]
    public async Task Remove_LeavesAFileChangedSince_WithItsBackup()
    {
        var fg = FakeDll("libxess_fg.dll", 1);
        var cached = await Cache(fg);
        var target = Path.Combine(_root, "game", "libxess_fg.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "game's copy");
        await using var scope = DeploymentScope.Unjournaled();
        var placed = await IntelXessFiles.PlaceAsync(scope, [new(fg.File, target, target + ".bak")], cached);
        await scope.CommitAsync();
        File.WriteAllText(target, "patched by a game update");

        await using var remove = DeploymentScope.Unjournaled();
        var removed = await IntelXessFiles.RemoveAsync(remove, placed.Entries);
        await remove.CommitAsync();

        Assert.Equal("patched by a game update", File.ReadAllText(target));
        Assert.Equal("game's copy", File.ReadAllText(target + ".bak"));
        Assert.Equal([target], removed.ChangedLeft);
        Assert.Equal([target + ".bak"], removed.BackupsLeft);
    }

    // The placement is part of the component's operation: rolling that back puts the game's copy back and removes the backup.
    [Fact]
    public async Task ARolledBackOperation_UndoesTheReplacement()
    {
        var fg = FakeDll("libxess_fg.dll", 1);
        var cached = await Cache(fg);
        var target = Path.Combine(_root, "game", "libxess_fg.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "game's copy");

        await using var scope = DeploymentScope.Unjournaled();
        await IntelXessFiles.PlaceAsync(scope, [new(fg.File, target, target + ".bak")], cached);
        var rollback = await scope.RollbackAsync();

        Assert.True(rollback.RolledBack);
        Assert.Equal("game's copy", File.ReadAllText(target));
        Assert.False(File.Exists(target + ".bak"));
    }

    [Fact]
    public async Task StateOf_TellsRhisFileFromAnotherVersion()
    {
        var fg = FakeDll("libxess_fg.dll", 1);
        var path = Path.Combine(_root, "state.dll");
        Assert.Equal(IntelXessFileState.Missing, IntelXessFiles.StateOf(fg.File, path));
        await File.WriteAllBytesAsync(path, fg.Bytes);
        Assert.Equal(IntelXessFileState.Accepted, IntelXessFiles.StateOf(fg.File, path));
        await File.WriteAllTextAsync(path, "other");
        Assert.Equal(IntelXessFileState.OtherVersion, IntelXessFiles.StateOf(fg.File, path));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
