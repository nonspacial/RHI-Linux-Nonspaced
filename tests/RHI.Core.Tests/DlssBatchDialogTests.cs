using RHI.Core.Models;
using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;
using RHI.Platform.Linux.Deployment;

namespace RHI.Core.Tests;

/// <summary>
/// The DLSS / Streamline batch dialog as a view model (epic #89: was built in the window's code-behind): only eligible
/// games, sorted; versions filled from Settings' defaults (a saved value missing from the manifest stays listed); the
/// two refusals before anything runs; Close turns into Cancel while a batch runs and stops it instead of closing.
/// </summary>
[Collection(UiLanguageCollection.Name)]
public sealed class DlssBatchDialogTests
{
    /// <summary>A refusal must return at once: a batch that was started by mistake waits forever, so it fails here instead of hanging.</summary>
    private static readonly TimeSpan Hang = TimeSpan.FromSeconds(10);

    private static LinuxDlssBatchCandidate Candidate(string name, bool eligible = true)
        => new(new DetectedGame { Name = name, InstallPath = $"/games/{name}" }, eligible, $"{name} reason", new DlssDetectionResult());

    private sealed class Batch
    {
        public readonly List<(IReadOnlyList<DetectedGame> Games, LinuxDlssVersionDefaults? Chosen)> Calls = [];
        public TaskCompletionSource<LinuxDlssBatchResult> Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Cancels;

        public DlssBatchDialogViewModel Dialog(IReadOnlyList<LinuxDlssBatchCandidate> eligible, LinuxDlssVersionDefaults? defaults = null)
            => new(eligible,
                [["310.1.0", "310.2.0"], ["310.1.0"], ["310.1.0"], [], ["2.7.0"]],
                defaults ?? new LinuxDlssVersionDefaults(),
                (games, chosen) => { Calls.Add((games, chosen)); return Pending.Task; },
                games => { Calls.Add((games, null)); return Pending.Task; },
                () => Cancels++);
    }

    [Fact]
    public void OnlyEligibleGames_ByName_NoneTicked()
    {
        UiLocalizationService.Apply("en-US");
        var eligible = DlssBatchDialogViewModel.Eligible([Candidate("zeta"), Candidate("Alpha"), Candidate("Mid", eligible: false)]);
        var dialog = new Batch().Dialog(eligible);

        Assert.Equal(["Alpha", "zeta"], dialog.Games.Select(row => row.Name));
        Assert.All(dialog.Games, row => Assert.False(row.IsChecked));
        Assert.Equal(UiLocalizationService.Format("Win.EligibleGames", 2), dialog.EligibleHeading);
        Assert.Equal("Alpha reason", dialog.Games[0].Reason);
        dialog.SelectAllCommand.Execute(null);
        Assert.All(dialog.Games, row => Assert.True(row.IsChecked));
        dialog.ClearAllCommand.Execute(null);
        Assert.All(dialog.Games, row => Assert.False(row.IsChecked));
    }

    [Fact]
    public void Versions_StartFromTheSavedDefaults_AndKeepOneTheManifestNoLongerLists()
    {
        UiLocalizationService.Apply("en-US");
        var dialog = new Batch().Dialog([Candidate("Alpha")], new LinuxDlssVersionDefaults("310.2.0", "", "", "", "2.9.9"));

        Assert.Equal("310.2.0", dialog.SuperResolution.Selected);
        Assert.Equal(OptionLists.DoNotChangeDisplay(), dialog.RayReconstruction.Selected);
        Assert.Equal("", dialog.RayReconstruction.Value);
        Assert.Equal([OptionLists.DoNotChangeDisplay(), "2.7.0", "2.9.9"], dialog.Streamline.Items);
        Assert.Equal("2.9.9", dialog.Streamline.Value);
        dialog.SuperResolution.Selected = null; // the dropdown's items reset: not a choice
        Assert.Equal("310.2.0", dialog.SuperResolution.Value);
    }

    [Fact]
    public async Task NothingTicked_OrNoVersionToApply_IsRefusedBeforeAnythingRuns()
    {
        UiLocalizationService.Apply("en-US");
        var batch = new Batch();
        var dialog = batch.Dialog([Candidate("Alpha")]);

        await dialog.ApplyCommand.ExecuteAsync(null).WaitAsync(Hang);
        Assert.Equal(UiLocalizationService.Text("Win.SelectAtLeastOneGame"), dialog.Status);

        dialog.Games[0].IsChecked = true;
        await dialog.ApplyCommand.ExecuteAsync(null).WaitAsync(Hang);
        Assert.Equal(UiLocalizationService.Text("Win.SelectAtLeastOneVersionUsing"), dialog.Status);
        Assert.Empty(batch.Calls);

        // Restore needs no version.
        batch.Pending.SetResult(new LinuxDlssBatchResult([]));
        await dialog.RestoreCommand.ExecuteAsync(null).WaitAsync(Hang);
        Assert.Single(batch.Calls);
        Assert.Null(batch.Calls[0].Chosen);
    }

    [Fact]
    public async Task WhileABatchRuns_CloseIsCancel_AndStopsItInsteadOfClosing()
    {
        UiLocalizationService.Apply("en-US");
        var batch = new Batch();
        var dialog = batch.Dialog([Candidate("Alpha"), Candidate("Beta")], new LinuxDlssVersionDefaults("310.1.0"));
        var closes = 0;
        dialog.CloseRequested += () => closes++;
        dialog.Games[1].IsChecked = true;

        var run = dialog.ApplyCommand.ExecuteAsync(null);
        Assert.True(dialog.IsRunning);
        Assert.False(dialog.CanRun);
        Assert.Equal(UiLocalizationService.Text("Win.Cancel"), dialog.CloseLabel);
        Assert.Equal(UiLocalizationService.Text("Win.ApplyingSelectedGames"), dialog.Status);
        Assert.Equal(["Beta"], batch.Calls.Single().Games.Select(game => game.Name));
        Assert.Equal("310.1.0", batch.Calls.Single().Chosen!.SuperResolution);

        dialog.CloseCommand.Execute(null);
        Assert.Equal(0, closes);
        Assert.Equal(1, batch.Cancels);
        Assert.False(dialog.CanClose); // pressed once: greyed until the batch has stopped

        batch.Pending.SetException(new OperationCanceledException());
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(UiLocalizationService.Text("Vm.OperationCancelled"), dialog.Status);
        Assert.False(dialog.IsRunning);
        Assert.True(dialog.CanClose);
        Assert.Equal(UiLocalizationService.Text("Win.Close"), dialog.CloseLabel);

        dialog.CloseCommand.Execute(null);
        Assert.Equal(1, closes);
        Assert.Equal(1, batch.Cancels);
    }

    [Fact]
    public async Task ARefusedOrFailedBatch_SaysWhy()
    {
        UiLocalizationService.Apply("en-US");
        var batch = new Batch();
        var dialog = batch.Dialog([Candidate("Alpha")], new LinuxDlssVersionDefaults("310.1.0"));
        dialog.Games[0].IsChecked = true;

        batch.Pending.SetException(new OperationRefusedException("busy elsewhere"));
        await dialog.ApplyCommand.ExecuteAsync(null).WaitAsync(Hang);
        Assert.Equal("busy elsewhere", dialog.Status);

        batch.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        batch.Pending.SetException(new IOException("disk gone"));
        await dialog.ApplyCommand.ExecuteAsync(null).WaitAsync(Hang);
        Assert.Equal(UiLocalizationService.Format("Win.BatchOperationFailed", UiLocalizationService.Present("disk gone")), dialog.Status);

        batch.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new LinuxDlssBatchResult([new LinuxDlssBatchItemResult("Alpha", LinuxDlssBatchComponent.SuperResolution, true, true, false, false, "ok")]);
        batch.Pending.SetResult(result);
        await dialog.ApplyCommand.ExecuteAsync(null).WaitAsync(Hang);
        Assert.Equal(UiLocalizationService.Present(result.Summary), dialog.Status);
    }

    [Fact]
    public void TheMarkupExtension_ReadsTheCurrentLanguage()
    {
        // Both languages pinned (w6/vmsplit review 3): a {l:T} that always read one table fails one of the two.
        Assert.NotEqual(UiLocalizationService.TextFor("pt-BR", "Win.Close"), UiLocalizationService.TextFor("en-US", "Win.Close"));
        UiLocalizationService.Apply("pt-BR");
        try { Assert.Equal(UiLocalizationService.TextFor("pt-BR", "Win.Close"), new TExtension("Win.Close").ProvideValue(null!)); }
        finally { UiLocalizationService.Apply("en-US"); }
        Assert.Equal(UiLocalizationService.TextFor("en-US", "Win.Close"), new TExtension("Win.Close").ProvideValue(null!));
    }
}
