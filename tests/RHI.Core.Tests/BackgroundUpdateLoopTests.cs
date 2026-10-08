using RHI.Desktop.Localization;
using RHI.Desktop.ViewModels;

namespace RHI.Core.Tests;

/// <summary>UI-13 (#81): the automatic-update loop survives a failed pass: logged, reported, retried with back-off.</summary>
[Collection(UiLanguageCollection.Name)]
public sealed class BackgroundUpdateLoopTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(4);

    [Fact]
    public async Task PassThatThrowsOnce_IsRetried_AndLaterPassesKeepRunning()
    {
        using var cts = new CancellationTokenSource();
        var passes = 0;
        var waits = new List<TimeSpan>();
        var failures = new List<(Exception Error, TimeSpan Retry)>();

        await BackgroundUpdateLoop.RunAsync(
            _ =>
            {
                passes++;
                if (passes == 1) throw new HttpRequestException("GitHub unreachable");
                if (passes == 3) cts.Cancel();
                return Task.CompletedTask;
            },
            Interval, runImmediate: true,
            (error, retry) => failures.Add((error, retry)),
            cts.Token,
            (wait, token) => { token.ThrowIfCancellationRequested(); waits.Add(wait); return Task.CompletedTask; }).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(3, passes);
        var failure = Assert.Single(failures);
        Assert.Equal("GitHub unreachable", failure.Error.Message);
        Assert.Equal(TimeSpan.FromMinutes(15), failure.Retry);
        // Retry soon after the failure, then the normal interval once a pass succeeded.
        Assert.Equal([TimeSpan.FromMinutes(15), Interval], waits);
    }

    [Fact]
    public async Task ConsecutiveFailures_BackOff_UpToTheInterval_AndATimeoutIsAFailureNotTheEnd()
    {
        using var cts = new CancellationTokenSource();
        var passes = 0;
        var waits = new List<TimeSpan>();

        await BackgroundUpdateLoop.RunAsync(
            _ =>
            {
                if (++passes == 7) cts.Cancel();
                // An HttpClient timeout surfaces as TaskCanceledException without our token being cancelled.
                throw new TaskCanceledException("request timed out");
            },
            Interval, runImmediate: false,
            (_, _) => { },
            cts.Token,
            (wait, token) => { token.ThrowIfCancellationRequested(); waits.Add(wait); return Task.CompletedTask; }).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(7, passes);
        Assert.Equal(
            new[] { 240, 15, 30, 60, 120, 240, 240 }.Select(m => TimeSpan.FromMinutes(m)),
            waits);
    }

    [Fact]
    public async Task Cancellation_EndsTheLoop_WithoutReportingAFailure()
    {
        using var cts = new CancellationTokenSource();
        var failures = 0;
        var passes = 0;

        await BackgroundUpdateLoop.RunAsync(
            async token => { passes++; cts.Cancel(); await Task.Delay(Timeout.Infinite, token); },
            Interval, runImmediate: true, (_, _) => failures++, cts.Token,
            (_, token) => Task.Delay(Timeout.Infinite, token)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal((1, 0), (passes, failures));
    }

    [Fact]
    public void FailedStatus_NamesTheErrorAndTheRetry_InTheUiLanguage()
    {
        UiLocalizationService.Apply("pt-BR");
        try
        {
            var text = BackgroundUpdateLoop.FailedStatus(new IOException("E/S negada"), TimeSpan.FromMinutes(30));
            Assert.Equal("A atualização automática falhou: E/S negada. Nova tentativa em 30 min.", text);
        }
        finally { UiLocalizationService.Apply("en-US"); }
    }
}
