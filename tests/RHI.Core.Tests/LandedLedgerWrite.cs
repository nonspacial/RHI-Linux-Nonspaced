using System.Text;
using RHI.Core.Deployment;
using RHI.Core.Models;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #204: the two shapes of a ledger write that lands (the new deployments.json is renamed into place) and then reports an
/// error. A fix that trusts the store's cache passes <see cref="AfterCache"/> and fails <see cref="InsideWrite"/>.
/// </summary>
public enum LandedShape
{
    /// <summary><c>SaveWrittenFaultForTesting</c>: after the store took the new ledger into its cache.</summary>
    AfterCache,

    /// <summary>
    /// <c>SaveInsideWriteFaultForTesting</c>: after the rename, before the store's cache — a real directory fsync failing
    /// inside <see cref="AtomicFile.WriteStoreFile"/>.
    /// </summary>
    InsideWrite
}

/// <summary>Arms one ledger save to land and then throw (#204).</summary>
internal static class LandedLedgerWrite
{
    public const string FaultMessage = "deployments.json was written, then the directory sync failed (test)";

    public static TheoryData<LandedShape> Shapes => new() { LandedShape.AfterCache, LandedShape.InsideWrite };

    /// <summary>Save number <paramref name="at"/> lands and then throws; every other save goes through.</summary>
    public static void Arm(LinuxDeploymentRecordStore store, LandedShape shape, int at, Func<Exception>? fault = null)
    {
        var saves = 0;
        Exception? Fault() => ++saves == at ? fault?.Invoke() ?? new IOException(FaultMessage) : null;
        Set(store, shape, Fault);
    }

    public static void Set(LinuxDeploymentRecordStore store, LandedShape shape, Func<Exception?> fault)
    {
        if (shape == LandedShape.AfterCache) store.SaveWrittenFaultForTesting = fault;
        else store.SaveInsideWriteFaultForTesting = fault;
    }

    public static void Disarm(LinuxDeploymentRecordStore store)
    {
        store.SaveWrittenFaultForTesting = null;
        store.SaveInsideWriteFaultForTesting = null;
        store.SaveFaultForTesting = null;
    }

    /// <summary>True when <paramref name="message"/> is the result of an operation finished as committed (<see cref="DeploymentScope.Landed"/>).</summary>
    public static bool IsLanded(string message)
        => message.StartsWith(DeploymentScope.LandedChangePrefix, StringComparison.Ordinal)
           || message.StartsWith(DeploymentScope.LandedRemovalPrefix, StringComparison.Ordinal);
}

/// <summary>One journaled operation of a family, on a fresh fixture, for <see cref="CommitPointAgreement"/>.</summary>
/// <param name="Root">Paths in files and records are compared relative to it.</param>
/// <param name="Watched">Folders whose files make up the compared state (game and prefix; never caches or the journal).</param>
/// <param name="Describes">Records that must describe their files (default every one; another component's record whose
/// file the operation replaces by design, e.g. the runtime NR Cost Scaler chains, is left out).</param>
/// <param name="Extra">More state to compare, already made root-independent (a file holding absolute paths).</param>
internal sealed record CommitPointCase(
    string Root, string[] Watched, LinuxDeploymentRecordStore Records, string RecordsPath,
    DeploymentJournal Journal, string JournalDirectory, Func<Task<ComponentOperationResult>> Act,
    Func<LinuxDeploymentRecord, bool>? Describes = null, Func<string>? Extra = null);

/// <summary>
/// #204: crash recovery and the in-process outcome agree. For a ledger write n of an operation, (a) the process dies right
/// after write n landed and the next start recovers, and (b) on a fresh fixture write n lands and throws in process: both
/// must leave the same files and the same records, every record must describe its files, and (b) leaves no journal entry.
/// </summary>
internal static class CommitPointAgreement
{
    /// <summary>How many ledger writes the operation makes (run once on a fresh fixture).</summary>
    public static async Task<int> SavesOfAsync(Func<string, Task<CommitPointCase>> make)
    {
        var c = await make("count");
        var saves = 0;
        c.Records.SaveFaultForTesting = () => { saves++; return null; };
        var result = await c.Act();
        c.Records.SaveFaultForTesting = null;
        Assert.True(result.Success, result.Message);
        return saves;
    }

    /// <summary>Checks every ledger write of the operation in <paramref name="shape"/>; returns the in-process results by write.</summary>
    public static async Task<IReadOnlyList<ComponentOperationResult>> AssertEveryWriteAsync(Func<string, Task<CommitPointCase>> make, LandedShape shape)
    {
        var count = await SavesOfAsync(make);
        Assert.True(count > 0);
        var results = new List<ComponentOperationResult>();
        for (var at = 1; at <= count; at++) results.Add(await AssertAsync(make, shape, at));
        return results;
    }

    public static async Task<ComponentOperationResult> AssertAsync(Func<string, Task<CommitPointCase>> make, LandedShape shape, int at)
    {
        var crash = await make($"crash-{at}");
        string crashed;
        using (var death = new SimulatedDeath(crash.Journal))
        {
            var saves = 0;
            crash.Records.SaveWrittenFaultForTesting = () => ++saves == at ? death.Die() : null;
            var (died, _) = await death.RunUntilDeathAsync(crash.Act);
            Assert.True(died, $"write {at}: the operation never reached it");
            await SimulatedDeath.RecoverAsync(crash.JournalDirectory, crash.RecordsPath);
            crashed = Capture(crash); // inside: nothing of the dead thread runs before this, as in a real crash
        }

        var live = await make($"live-{shape}-{at}");
        LandedLedgerWrite.Arm(live.Records, shape, at);
        var result = await live.Act();
        LandedLedgerWrite.Disarm(live.Records);
        var inProcess = Capture(live);

        Assert.True(crashed == inProcess,
            $"write {at} ({shape}): crash recovery and the in-process outcome differ.\n--- crash + recovery\n{crashed}\n--- in process ({result.Message})\n{inProcess}");
        Assert.Empty(JournalEntries(live));
        // A failed operation never claims success; a finished one says the save reported an error.
        Assert.Equal(result.Success, LandedLedgerWrite.IsLanded(result.Message));
        return result;
    }

    public static string[] JournalEntries(CommitPointCase c)
        => Directory.Exists(c.JournalDirectory) ? Directory.GetFiles(c.JournalDirectory, "*.json") : [];

    /// <summary>The files under the watched folders and the records, relative to the case's root; every record checked against its files.</summary>
    public static string Capture(CommitPointCase c)
    {
        var root = Path.GetFullPath(c.Root);
        string Rel(string path) => Path.GetRelativePath(root, Path.GetFullPath(path));
        var lines = new List<string>();
        foreach (var dir in c.Watched.Where(Directory.Exists))
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (name.Contains(".rhi-backup-", StringComparison.Ordinal) || name.EndsWith(".rhi-lock", StringComparison.Ordinal)) continue;
                if (Path.GetFullPath(file).StartsWith(Path.GetFullPath(c.JournalDirectory) + "/", StringComparison.Ordinal)) continue;
                var rel = Rel(file);
                var recovered = rel.IndexOf(RecoveredCopies.FolderName + "/", StringComparison.Ordinal);
                if (recovered >= 0)
                {
                    var rest = rel[(recovered + RecoveredCopies.FolderName.Length + 1)..];
                    rel = rel[..recovered] + RecoveredCopies.FolderName + "/*/" + rest[(rest.IndexOf('/') + 1)..];
                }
                lines.Add($"file {rel} {(name.EndsWith(".reg", StringComparison.Ordinal) ? HiveContent(file) : FileHash.Sha256Hex(file))}");
            }

        var records = new LinuxDeploymentRecordStore(c.RecordsPath).All();
        foreach (var record in records.OrderBy(r => r.Component.ToString(), StringComparer.Ordinal))
        {
            var mustDescribe = c.Describes?.Invoke(record) ?? true;
            var text = new StringBuilder($"record {record.Component}");
            foreach (var file in record.Files.OrderBy(f => f.Path, StringComparer.Ordinal))
            {
                text.Append($" [{Rel(file.Path)} {file.Sha256} allow={file.AllowUserChanges}]");
                if (!mustDescribe) continue;
                Assert.True(File.Exists(file.Path), $"{record.Component} names a missing file: {file.Path}");
                if (!file.AllowUserChanges && !string.IsNullOrEmpty(file.Sha256))
                    Assert.True(FileHash.HexEquals(FileHash.Sha256Hex(file.Path), file.Sha256), $"{record.Component} does not describe {file.Path}");
            }
            text.Append($" override={record.WineOverride?.DllName ?? "-"}");
            text.Append($" extra={string.Join(",", record.AdditionalWineOverrides.Select(o => o.DllName).Order(StringComparer.Ordinal))}");
            text.Append($" removing={record.Metadata.ContainsKey(LinuxDeploymentRecovery.RemovalOperationIdMetadataKey)}");
            lines.Add(text.ToString());
        }
        if (c.Extra is not null) lines.Add("extra " + c.Extra());
        lines.Sort(StringComparer.Ordinal);
        return string.Join("\n", lines);
    }

    // A Wine hive's section headers carry the time of their last write (and #time lines): compared without them.
    private static string HiveContent(string path)
        => string.Join("\n", File.ReadAllLines(path)
            .Where(line => !line.StartsWith("#time=", StringComparison.Ordinal))
            .Select(line => System.Text.RegularExpressions.Regex.Replace(line, @"^(\[.*\]) \d+$", "$1")));
}
