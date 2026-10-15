using RHI.Desktop.Diagnostics;
using RHI.Platform.Linux.Hdr;

namespace RHI.Desktop.ViewModels;

/// <summary>What start-up runs, provided by the shell (its panels and services); a test double records the order.</summary>
internal interface IStartupSteps
{
    /// <summary>Reads the HDR host (NVIDIA driver, gamescope on PATH); called off the UI thread.</summary>
    LinuxHdrHostSnapshot ProbeHdrHost();
    /// <summary>The HDR host is known (first read, or an analysis got there first): HDR rows and resolutions follow it.</summary>
    void HdrHostKnown(LinuxHdrHostSnapshot host);
    /// <summary>The neural-rendering version catalogue (Extras); never throws.</summary>
    Task LoadNeuralCatalogAsync();
    /// <summary>The DLSS manifest (version lists); a failure becomes a status notice, never an exception.</summary>
    Task LoadDlssManifestAsync();
    /// <summary>The first library scan.</summary>
    Task RefreshLibraryAsync();
    /// <summary>The first scan is done (the manifest and catalogue may still be loading): start-up background work
    /// that needs the library, e.g. the CacheAllShaders shader-pack staging (#194). Must not block.</summary>
    void LibraryScanned();
    /// <summary>Starts the background update loop; <paramref name="runImmediate"/> runs a pass at once.</summary>
    void StartBackgroundUpdates(bool runImmediate);
    /// <summary>Settings' "Automatically update RHI-managed components" (read when the loop starts).</summary>
    bool AutoUpdateComponents { get; }
}

/// <summary>
/// The app's start-up sequence (epic #89 unit 7: was the shell's <c>InitializeAsync</c>). The HDR host probe, the
/// neural-rendering catalogue and the DLSS manifest start together; the first library scan waits only for the HDR host
/// (so the first analysis plans HDR with it), never behind the DLSS manifest (Refs #43); the shader-pack staging starts
/// right after the scan (#194, not behind the manifest either); the background update loop
/// starts once the scan, the manifest and the catalogue are all done, running a pass at once when Settings says so.
/// </summary>
internal sealed class StartupSequence(IStartupSteps steps)
{
    public async Task RunAsync()
    {
        // The HDR host (NVIDIA driver, gamescope on PATH) is read once off the UI thread; each analysis refreshes it.
        var hdrHost = Task.Run(steps.ProbeHdrHost);
        var neuralCatalog = steps.LoadNeuralCatalogAsync();
        // The library scan never waits behind the DLSS manifest: both start now (Refs #43).
        var dlssManifest = steps.LoadDlssManifestAsync();
        steps.HdrHostKnown(await hdrHost);
        await steps.RefreshLibraryAsync();
        steps.LibraryScanned();
        await dlssManifest;
        await neuralCatalog;
        steps.StartBackgroundUpdates(runImmediate: steps.AutoUpdateComponents);
    }

    /// <summary>Starts the sequence; a failure is reported once (never unobserved). Tests await the returned task.</summary>
    public static Task Start(IStartupSteps steps)
    {
        var run = new StartupSequence(steps).RunAsync();
        UiExceptionGuard.Forget(run, "initialize");
        return run;
    }
}
