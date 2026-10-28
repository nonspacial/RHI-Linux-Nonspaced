using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// #248: the three DLSS5 Feeder failures from the owner's Borderlands 3 <c>dlss5-feed.log</c> (fixture: his lines, paths
/// anonymised) — neural rendering NotImplemented (feature 18, <c>0xBAD00012</c>), flat depth while the scene moves (Generic
/// Depth on the wrong buffer) and the evaluate crash after a swapchain/effect change — each named with its own plain-step
/// fix; the generic crash row stays for crashes without the Feeder's "stopped" context after a change.
/// </summary>
public sealed class NeuralRenderingFeederLogTests
{
    private static readonly string Bl3Log = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "bl3-dlss5-feed.log"));

    private static string[] Bl3Lines => Bl3Log.Replace("\r", "").Split('\n');

    // The prove-first test: written against names only, so it compiles on the wave base too (there the owner's log
    // gives the generic crash row alone).
    [Fact]
    public void TheOwnersBl3Log_NamesTheThreeFeederFailures_AndNotTheGenericCrashRow()
    {
        var findings = NeuralRenderingDiagnosis.Classify("dlss5-feed.log", Bl3Log);

        Assert.Equal(["NeuralRenderingNotImplemented", "FlatDepth", "EvaluateCrashAfterDisplayChange"], findings.Select(f => f.Signature.ToString()));
        Assert.Equal([25, 114, 170], findings.Select(f => f.Line ?? 0));
        Assert.Equal(["NothingToChangeInRhi", "GenericDepthInReShade", "RestartWithDisplayModeSet"], findings.Select(f => f.Fix.ToString()));
        Assert.StartsWith("18:54:19.748  [feed] NGX feature requirements: feature 18", findings[0].Excerpt);
        Assert.Contains("depth is FLAT while the scene moves", findings[1].Excerpt);
        Assert.StartsWith("18:56:19.746  stopped: the DLSS evaluate crashed", findings[2].Excerpt);
    }

    private static NrLogFinding? Find(string text, NrLogSignature signature)
        => NeuralRenderingDiagnosis.Classify("dlss5-feed.log", text).SingleOrDefault(f => f.Signature == signature);

    // ---- each signature found and not found ---------------------------------------------------------------------

    [Theory]
    [InlineData("[feed] NGX feature requirements: feature 18 (neural rendering, what nvngx_dlssnr.dll backs) -> the query itself failed 0xBAD00012 (NotImplemented)", true)]
    [InlineData("[feed] ngx FEATURE 18 query returned 0xbad00012", true)]
    // NotImplemented for another feature is no neural-rendering finding, even when its line mentions neural rendering
    // (review nit 7: only the Feeder's own "feature 18" names it); nor is feature 18 without the code.
    [InlineData("[feed] neural rendering query returned 0xbad00012", false)]
    [InlineData("[feed] NGX feature requirements: SuperSampling (DLSS, needed before neural rendering) -> the query itself failed 0xBAD00012 (NotImplemented)", false)]
    [InlineData("[feed] NGX feature requirements: SuperSampling (DLSS) -> the query itself failed 0xBAD00012 (NotImplemented)", false)]
    [InlineData("[feed] NGX feature requirements: feature 180 -> failed 0xBAD00012", false)]
    [InlineData("[feed] NGX feature requirements: feature 18 (neural rendering) -> supported", false)]
    [InlineData("[feed] feature 18 at 0xBAD000120 (an address)", false)]
    public void NotImplemented_IsFeature18Only(string line, bool found)
    {
        var finding = Find("start\n" + line + "\n", NrLogSignature.NeuralRenderingNotImplemented);

        Assert.Equal(found, finding is not null);
        if (finding is null) return;
        Assert.Equal(2, finding.Line);
        Assert.Equal(NrFixHint.NothingToChangeInRhi, finding.Fix);
        Assert.Equal(NrFixHint.NothingToChangeInRhi, NeuralRenderingDiagnosis.FixFor(NrLogSignature.NeuralRenderingNotImplemented));
    }

    [Fact]
    public void FlatDepth_IsOnlyTheWhileTheSceneMovesLine_AndKeepsTheLastOne()
    {
        // The owner's log: :68 … :114 blame Generic Depth (the scene moves); :123/:134/:144 say only "sampled depth is
        // flat" (a still scene, a menu) — no finding, so the finding stays on :114.
        var finding = Find(Bl3Log, NrLogSignature.FlatDepth);

        Assert.NotNull(finding);
        Assert.Equal(114, finding.Line);
        Assert.Equal(NrFixHint.GenericDepthInReShade, finding.Fix);
        Assert.Contains("sampled depth is flat", Bl3Lines[143]);
        Assert.Null(Find(Bl3Lines[56] + "\n" + Bl3Lines[143] + "\n", NrLogSignature.FlatDepth));
        Assert.Empty(NeuralRenderingDiagnosis.Classify("dlss5-feed.log", Bl3Lines[56] + "\n" + Bl3Lines[55] + "\n"));
        Assert.Null(Find("[feed] Depth probe: depth is FLAT\n[feed] Generic Depth: picked buffer 3\n", NrLogSignature.FlatDepth));
        // Review F6 (R3): the verdict counts only on the depth probe's own line.
        Assert.Null(Find("[feed] MV probe (frame 600): <-- depth is FLAT while the scene moves\n", NrLogSignature.FlatDepth));
    }

    // ---- the evaluate crash after a change, and the generic crash row --------------------------------------------

    private static readonly string Ready = "[feed] feature ready: 3840x2160 DLAA, flags=74\n";
    private static readonly string Crash = "[feed] evaluate raised 0xC0000005 (reading address FFFFFFFFFFFFFFFF) (caught; nothing submitted)\n"
                                           + "stopped: the DLSS evaluate crashed (the DLSS 5 add-on may be incompatible with this game/resolution).\n";

    [Fact]
    public void TheOwnersCrash_IsTheSpecificRow_WhichReplacesTheGenericOne()
    {
        var findings = NeuralRenderingDiagnosis.Classify("dlss5-feed.log", Bl3Log);

        Assert.DoesNotContain(findings, f => f.Signature == NrLogSignature.CrashOrDeviceLost);
        var crash = Assert.Single(findings, f => f.Signature == NrLogSignature.EvaluateCrashAfterDisplayChange);
        Assert.Equal(NrFixHint.RestartWithDisplayModeSet, crash.Fix);
        Assert.Equal(NrFixHint.RestartWithDisplayModeSet, NeuralRenderingDiagnosis.FixFor(NrLogSignature.EvaluateCrashAfterDisplayChange));
    }

    [Theory]
    [InlineData("[feed] swapchain colour space: linear BT.709 (scRGB, HDR)\n")]
    [InlineData("[feed] effect runtime 0000000030D7B050 initialised (device 0000000033F50080; 1 runtime in this process)\n")]
    [InlineData("[feed] building: 2560x1440 backbuffer R10G10B10A2_UNORM (same-device D3D12, depth reversed=1)\n")]
    public void ACrashRightAfterAChange_IsTheSpecificRow(string change)
    {
        var text = "dlss5-feed 1.19.0 attached.\n" + Ready + "[feed] frame 1 delivered\n" + change + Ready + Crash;

        var findings = NeuralRenderingDiagnosis.Classify("dlss5-feed.log", text);

        var finding = Assert.Single(findings);
        Assert.Equal(NrLogSignature.EvaluateCrashAfterDisplayChange, finding.Signature);
        Assert.Equal(7, finding.Line);
    }

    private const string Holding = "[feed] holding the feature (re)build for 60 frames (the DLSS 5 add-on re-arms its hooks asynchronously)\n";

    // The change lands and the Feeder announces the rebuild for it (its "holding the feature (re)build" line, or the rebuild
    // itself); the old feature still delivers a frame; then the rebuilt feature crashes on its first evaluate.
    [Theory]
    [InlineData("[feed] swapchain colour space: linear BT.709 (scRGB, HDR)\n" + Holding)]
    [InlineData("[feed] building: 3840x2160 backbuffer R16G16B16A16_FLOAT (same-device D3D12, depth reversed=1)\n")]
    // A further change while the rebuild is held (an effect reload) does not cancel the announced rebuild.
    [InlineData("[feed] swapchain colour space: linear BT.709 (scRGB, HDR)\n" + Holding
                + "[feed] effect runtime 0000000030D7B050 initialised (device 0000000033F50080; 1 runtime in this process)\n")]
    public void AFrameTheOldFeatureDeliveredBeforeTheRebuild_DoesNotClearTheChange(string changeAndRebuildPending)
    {
        var text = "dlss5-feed 1.19.0 attached.\n" + Ready + changeAndRebuildPending + "[feed] frame 1800 delivered\n" + Ready + Crash;

        Assert.Equal([NrLogSignature.EvaluateCrashAfterDisplayChange], NeuralRenderingDiagnosis.Classify("dlss5-feed.log", text).Select(f => f.Signature));
    }

    // Review F2 (probe P07): an alt-tab or effect reload re-makes ReShade's effect runtime and reports the colour space again,
    // but on a D3D12 game the feature survives it untouched — no rebuild is announced, and frames keep coming. A crash
    // thousands of frames later is not "after the display mode changed": the generic row, never the restart advice.
    [Fact]
    public void AChangeTheFeatureCameThroughWithoutARebuild_KeepsTheGenericRow()
    {
        var text = "dlss5-feed 1.19.0 attached.\n" + Ready + "[feed] frame 1 delivered\n"
                   + "[feed] effect runtime 0000000030D7B050 destroyed -- it was the bound one (0 runtimes left)\n"
                   + "[feed] swapchain colour space: sRGB G2.2 BT.709 (SDR)\n"
                   + "[feed] effect runtime 0000000030D7B050 initialised (device 0000000033F50080; 1 runtime in this process)\n"
                   + "[feed] frame 1800 delivered\n[feed] frame 3600 delivered\n" + Crash;

        var finding = Assert.Single(NeuralRenderingDiagnosis.Classify("dlss5-feed.log", text));

        Assert.Equal(NrLogSignature.CrashOrDeviceLost, finding.Signature);
        Assert.Equal(9, finding.Line);
        Assert.Equal(NrFixHint.RemoveNrMethod, finding.Fix);
    }

    // Once the announced rebuild is ready, the rebuilt feature's first delivered frame settles the change: a later crash is
    // the generic row.
    [Theory]
    [InlineData("[feed] swapchain colour space: linear BT.709 (scRGB, HDR)\n" + Holding)]
    [InlineData("[feed] building: 3840x2160 backbuffer R16G16B16A16_FLOAT (same-device D3D12, depth reversed=1)\n")]
    public void TheRebuiltFeaturesFirstFrame_SettlesTheChange(string changeAndRebuildPending)
    {
        var text = "dlss5-feed 1.19.0 attached.\n" + Ready + changeAndRebuildPending + Ready + "[feed] frame 1800 delivered\n" + Crash;

        Assert.Equal([NrLogSignature.CrashOrDeviceLost], NeuralRenderingDiagnosis.Classify("dlss5-feed.log", text).Select(f => f.Signature));
    }

    // Review F6 (R2, probe P08): a crash between the change and any rebuild (the old feature, the new picture) is the
    // specific row alone — the generic hit of that crash is absorbed, not shown beside it.
    [Fact]
    public void ACrashBetweenTheChangeAndTheRebuild_IsTheSpecificRowAlone()
    {
        var text = "dlss5-feed 1.19.0 attached.\n" + Ready + "[feed] frame 1 delivered\n[feed] swapchain colour space: linear BT.709 (scRGB, HDR)\n" + Crash;

        var finding = Assert.Single(NeuralRenderingDiagnosis.Classify("dlss5-feed.log", text));

        Assert.Equal(NrLogSignature.EvaluateCrashAfterDisplayChange, finding.Signature);
        Assert.Equal(6, finding.Line);
    }

    // Review F6 (R1, probe P09): a generic crash held after a change and then settled (no stopped line followed) replaces
    // an older run's generic row — the page shows the newest crash, as for every signature.
    [Fact]
    public void ANewerHeldGenericCrash_ReplacesAnOlderRunsGenericRow()
    {
        var av = "[feed] evaluate raised 0xC0000005 (reading address FFFFFFFFFFFFFFFF) (caught; nothing submitted)\n";
        var text = "dlss5-feed 1.19.0 attached.\n" + Ready + av
                   + "dlss5-feed 1.19.0 attached.\n" + Ready + "[feed] frame 1 delivered\n[feed] swapchain colour space: linear BT.709 (scRGB, HDR)\n" + Ready + av;

        var finding = Assert.Single(NeuralRenderingDiagnosis.Classify("dlss5-feed.log", text));

        Assert.Equal(NrLogSignature.CrashOrDeviceLost, finding.Signature);
        Assert.Equal(9, finding.Line);
    }

    [Fact]
    public void ACrashWithoutAChange_KeepsTheGenericRow()
    {
        // The first build crashes: no change since the feature was ready.
        var first = "dlss5-feed 1.19.0 attached.\n[feed] swapchain colour space: sRGB G2.2 BT.709 (SDR)\n" + Ready + Crash;
        var finding = Assert.Single(NeuralRenderingDiagnosis.Classify("dlss5-feed.log", first));
        Assert.Equal(NrLogSignature.CrashOrDeviceLost, finding.Signature);
        Assert.Equal(4, finding.Line);
        Assert.Equal(NrFixHint.RemoveNrMethod, finding.Fix);

        // A change the rebuilt feature came through (a frame delivered after it): a later crash is no longer "after the change".
        var later = "dlss5-feed 1.19.0 attached.\n" + Ready + "[feed] swapchain colour space: linear BT.709 (scRGB, HDR)\n" + Ready
                    + "[feed] frame 1800 delivered\n" + Crash;
        Assert.Equal([NrLogSignature.CrashOrDeviceLost], NeuralRenderingDiagnosis.Classify("dlss5-feed.log", later).Select(f => f.Signature));

        // A crash without the Feeder's "stopped" line (another log, another tool): the generic row, as before.
        var bare = Ready + "[feed] swapchain colour space: linear BT.709 (scRGB, HDR)\n" + Ready + "Unhandled exception 0xC0000005 at 0x00007FF6\n";
        Assert.Equal([NrLogSignature.CrashOrDeviceLost], NeuralRenderingDiagnosis.Classify("ReShade.log", bare).Select(f => f.Signature));
    }

    // Review F3 (probe P05): the Feeder says it stopped, with no change before and a code that is not 0xC0000005: the page
    // still names the crash — the generic row on the stopped line, never "No known errors" and never the restart advice.
    [Fact]
    public void AStoppedLineWithoutAChange_IsTheGenericRow_OncePerRun()
    {
        var stopped = "stopped: the DLSS evaluate crashed (the DLSS 5 add-on may be incompatible with this game/resolution).\n";
        var text = "dlss5-feed 1.19.0 attached.\n" + Ready + "[feed] evaluate raised 0xC000001D (caught)\n" + stopped;

        var finding = Assert.Single(NeuralRenderingDiagnosis.Classify("dlss5-feed.log", text));

        Assert.Equal(NrLogSignature.CrashOrDeviceLost, finding.Signature);
        Assert.Equal(NrFixHint.RemoveNrMethod, finding.Fix);
        Assert.Equal(4, finding.Line);
        Assert.StartsWith("stopped: the DLSS evaluate crashed", finding.Excerpt);

        // The run already has a generic row for that crash (its 0xC0000005 line): that row stays, on its own line.
        var withCode = "dlss5-feed 1.19.0 attached.\n" + Ready + Crash;
        Assert.Equal([3], NeuralRenderingDiagnosis.Classify("dlss5-feed.log", withCode).Select(f => f.Line ?? 0));
        // A crash held after a change and settled by a later frame is the run's generic row too: the stopped line after it
        // adds nothing and moves nothing.
        var settled = "dlss5-feed 1.19.0 attached.\n" + Ready + "[feed] frame 1 delivered\n[feed] swapchain colour space: linear BT.709 (scRGB, HDR)\n"
                      + "[feed] evaluate raised 0xC0000005 (caught; nothing submitted)\n[feed] frame 1800 delivered\n" + stopped;
        Assert.Equal([(NrLogSignature.CrashOrDeviceLost, 5)], NeuralRenderingDiagnosis.Classify("dlss5-feed.log", settled).Select(f => (f.Signature, f.Line ?? 0)));
        // A later run's stopped line is that run's crash: the newest row.
        Assert.Equal([8], NeuralRenderingDiagnosis.Classify("dlss5-feed.log", withCode + text).Select(f => f.Line ?? 0));
    }

    // Review F4 (probe P04): the Feeder's stopped line written before the evaluate lines of the same crash: one row, the
    // specific one. Only the evaluate lines right after it belong to that crash — a device lost at teardown later is its
    // own generic row (probe P20).
    [Fact]
    public void TheStoppedLineBeforeItsEvaluateLines_IsTheSpecificRowAlone()
    {
        var stopped = "stopped: the DLSS evaluate crashed (the DLSS 5 add-on may be incompatible with this game/resolution).\n";
        var text = "dlss5-feed 1.19.0 attached.\n" + Ready + "[feed] frame 1 delivered\n[feed] swapchain colour space: linear BT.709 (scRGB, HDR)\n" + Ready
                   + stopped + "[feed] evaluate raised 0xC0000005 (reading address FFFFFFFFFFFFFFFF) (caught; nothing submitted)\n"
                   + "[feed] evaluate raised exception 0xC0000005 (caught; nothing was submitted)\n";

        var finding = Assert.Single(NeuralRenderingDiagnosis.Classify("dlss5-feed.log", text));

        Assert.Equal(NrLogSignature.EvaluateCrashAfterDisplayChange, finding.Signature);
        Assert.Equal(6, finding.Line);

        var teardown = Bl3Log.Replace("\r", "").Replace("[feed] timed out draining the queue before teardown",
            "[feed] the D3D12 device was removed (DXGI_ERROR_DEVICE_REMOVED)");
        Assert.Equal([(NrLogSignature.NeuralRenderingNotImplemented, 25), (NrLogSignature.FlatDepth, 114),
                (NrLogSignature.EvaluateCrashAfterDisplayChange, 170), (NrLogSignature.CrashOrDeviceLost, 172)],
            NeuralRenderingDiagnosis.Classify("dlss5-feed.log", teardown).Select(f => (f.Signature, f.Line ?? 0)));
    }

    [Fact]
    public void ANewRun_ForgetsTheLastRunsChange()
    {
        var text = "dlss5-feed 1.19.0 attached.\n" + Ready + "[feed] swapchain colour space: linear BT.709 (scRGB, HDR)\n"
                   + "shut down cleanly.\n"
                   + "dlss5-feed 1.19.0 attached.\n" + Ready + Crash;

        Assert.Equal([NrLogSignature.CrashOrDeviceLost], NeuralRenderingDiagnosis.Classify("dlss5-feed.log", text).Select(f => f.Signature));
    }

    [Fact]
    public void AnEarlierGenericCrash_StaysBesideTheSpecificRow()
    {
        // Run 1 crashed on its first build (generic); run 2 crashed right after an HDR switch (specific): both rows, the
        // generic one on run 1's line.
        var text = "dlss5-feed 1.19.0 attached.\n" + Ready + Crash
                   + "dlss5-feed 1.19.0 attached.\n" + Ready + "[feed] frame 1 delivered\n[feed] swapchain colour space: linear BT.709 (scRGB, HDR)\n" + Ready + Crash;

        var findings = NeuralRenderingDiagnosis.Classify("dlss5-feed.log", text);

        Assert.Equal([NrLogSignature.EvaluateCrashAfterDisplayChange, NrLogSignature.CrashOrDeviceLost], findings.Select(f => f.Signature));
        Assert.Equal(11, findings[0].Line);
        Assert.Equal(3, findings[1].Line);
    }

    [Fact]
    public void OnlyTheEndOfALargeLog_AChangeBeforeAnyReadyLineStillCounts()
    {
        // ReadEnd: the run's first "feature ready" lies before the part that was read.
        var tail = "[feed] STALL frame 5784\n[feed] swapchain colour space: linear BT.709 (scRGB, HDR)\n" + Ready + Crash;

        var finding = Assert.Single(NeuralRenderingDiagnosis.Classify("dlss5-feed.log", tail, firstLine: null));

        Assert.Equal(NrLogSignature.EvaluateCrashAfterDisplayChange, finding.Signature);
        Assert.Null(finding.Line);
        // Read whole, a change before the feature was ever ready is the game starting up, not a change.
        Assert.Equal([NrLogSignature.CrashOrDeviceLost], NeuralRenderingDiagnosis.Classify("dlss5-feed.log", tail).Select(f => f.Signature));
    }

    [Fact]
    public void TheOwnersLog_ReadFromTheGameFolder_GivesTheSameThreeRows_AndWritesNothing()
    {
        var root = Path.Combine(Path.GetTempPath(), "rhi-nr-logs-" + Guid.NewGuid().ToString("N"));
        var bin = Directory.CreateDirectory(Path.Combine(root, "Borderlands 3", "OakGame", "Binaries", "Win64")).FullName;
        try
        {
            var path = Path.Combine(bin, "dlss5-feed.log");
            File.WriteAllText(path, Bl3Log);
            var before = File.GetLastWriteTimeUtc(path);

            var log = NeuralRenderingDiagnosis.ReadLog("dlss5-feed.log", path, [root]);

            Assert.Equal(NrLogState.Read, log.State);
            Assert.Equal([25, 114, 170], log.Findings.Select(f => f.Line ?? 0));
            Assert.Equal([path], Directory.GetFiles(root, "*", SearchOption.AllDirectories));
            Assert.Equal(before, File.GetLastWriteTimeUtc(path));
            Assert.Equal(Bl3Log, File.ReadAllText(path));
        }
        finally { Directory.Delete(root, true); }
    }
}
