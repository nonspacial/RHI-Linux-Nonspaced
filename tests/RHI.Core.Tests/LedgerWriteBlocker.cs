using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>Makes every save of <paramref name="store"/> fail (as a full or read-only disk would) until disposed.</summary>
internal sealed class LedgerWriteBlocker : IDisposable
{
    private readonly LinuxDeploymentRecordStore _store;

    private LedgerWriteBlocker(LinuxDeploymentRecordStore store, string ledgerPath)
    {
        _store = store;
        store.SaveFaultForTesting = () => new IOException($"Simulated write failure: {Path.GetFileName(ledgerPath)}");
    }

    public static LedgerWriteBlocker Block(LinuxDeploymentRecordStore store, string ledgerPath) => new(store, ledgerPath);

    public void Dispose() => _store.SaveFaultForTesting = null;
}
