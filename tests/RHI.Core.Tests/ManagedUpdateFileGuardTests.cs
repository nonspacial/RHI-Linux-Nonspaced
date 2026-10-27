using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #236/#237/#238: the one rule every new Update All family's check uses before it plans a swap — the file must still
/// hold RHI's bytes; one that cannot be read, or that has no recorded hash, cannot show that, so no swap is planned.
/// </summary>
public sealed class ManagedUpdateFileGuardTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("rhi-update-file-guard-").FullName;

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root)) PermissionDenial.MakeWritable(file);
        try { Directory.Delete(_root, true); } catch { }
    }

    private static readonly ManagedUpdateCheck Planned = new(true, true, "update", "1.0", "2.0");

    private string File(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        System.IO.File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task MatchingFile_KeepsThePlannedUpdate()
    {
        var path = File("a.dll", [1, 2, 3]);

        var check = await ManagedUpdateFileGuard.BeforePlanningAsync(Planned, [(path, RHI.Core.Deployment.FileHash.Sha256Hex(path))], CancellationToken.None);

        Assert.Same(Planned, check);
    }

    [Fact]
    public async Task FileWithoutARecordedHash_IsNotSwapped()
    {
        var path = File("b.dll", [1, 2, 3]);

        var check = await ManagedUpdateFileGuard.BeforePlanningAsync(Planned, [(path, null)], CancellationToken.None);

        Assert.Equal((false, true), (check.UpdateAvailable, check.CouldNotCheck));
        Assert.Equal(ManagedUpdateFileGuard.ChangedOutsideRhiPrefix + path, check.Message);
    }

    // An unreadable file reads as not matching (never as "unchanged"), and a check that plans no update never reads it.
    [PermissionDenialFact]
    public async Task UnreadableFile_IsNotSwapped_AndIsNeverReadWhenNoUpdateIsPlanned()
    {
        var path = File("c.dll", [1, 2, 3]);
        var sha = RHI.Core.Deployment.FileHash.Sha256Hex(path);
        PermissionDenial.MakeUnreadable(path);

        var planned = await ManagedUpdateFileGuard.BeforePlanningAsync(Planned, [(path, sha)], CancellationToken.None);
        var current = await ManagedUpdateFileGuard.BeforePlanningAsync(Planned with { UpdateAvailable = false }, [(path, sha)], CancellationToken.None);

        Assert.Equal((false, true), (planned.UpdateAvailable, planned.CouldNotCheck));
        Assert.Equal(ManagedUpdateFileGuard.ChangedOutsideRhiPrefix + path, planned.Message);
        Assert.Equal((false, false), (current.UpdateAvailable, current.CouldNotCheck));
    }

    [Fact]
    public void Removed_NamesTheFirstMissingFile_OrNothing()
    {
        var present = File("d.dll", [1]);
        var missing = Path.Combine(_root, "gone.dll");

        Assert.Null(ManagedUpdateFileGuard.Removed([present], "1.0"));
        var removed = ManagedUpdateFileGuard.Removed([present, missing], "1.0")!;
        Assert.Equal((true, false, true, "1.0"), (removed.Managed, removed.UpdateAvailable, removed.CouldNotCheck, removed.InstalledVersion));
        Assert.Equal(ManagedUpdateFileGuard.RemovedOutsideRhiPrefix + missing, removed.Message);
    }
}
