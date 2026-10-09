using GovUK.Dfe.AI.Agents.Resilience;
using GovUK.Dfe.AI.Agents.Tests.Constants;
using System.Diagnostics;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Tests.Resilience;

/// <summary>Fallback results for failures the caller suppresses, and parallel work where the first failure cancels the rest.</summary>
public sealed class ResilienceTests
{
    [Fact]
    public async Task ExecuteAsync_ReturnsFallback_WhenStepThrowsAndSuppressionAllows()
    {
        var result = await ResilientAgentStep.ExecuteAsync<string>(
            step: () => throw new InvalidOperationException(TestErrorMessages.StepFailure),
            fallback: ex => $"fallback: {ex.Message}",
            shouldSuppress: _ => true);

        Assert.Equal($"fallback: {TestErrorMessages.StepFailure}", result);
    }

    [Fact]
    public async Task ExecuteAsync_Propagates_WhenShouldSuppressReturnsFalse()
        => await Assert.ThrowsAsync<InvalidOperationException>(() => ResilientAgentStep.ExecuteAsync<string>(
            step: () => throw new InvalidOperationException(TestErrorMessages.StepFailure),
            fallback: _ => "fallback",
            shouldSuppress: _ => false));

    [Fact]
    public async Task ExecuteAsync_Propagates_WhenCancelled()
        => await Assert.ThrowsAsync<OperationCanceledException>(() => ResilientAgentStep.ExecuteAsync<string>(
            step: () => throw new OperationCanceledException(),
            fallback: _ => "fallback"));

    [Fact]
    public async Task WhenAllAsync_AFailure_CancelsTheOthers_AndIsTheExceptionThrown()
    {
        var failure = new InvalidOperationException("Mandatory agent failed.");
        var siblingCancelled = false;
        var stopwatch = Stopwatch.StartNew();

        var work = new Func<CancellationToken, Task<int>>[]
        {
            // Listed first, so plain Task.WhenAll would surface its cancellation instead of the real failure.
            async token =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), token);
                    return 1;
                }
                catch (OperationCanceledException)
                {
                    siblingCancelled = true;
                    throw;
                }
            },
            async ct =>
            {
                await Task.Delay(20, ct);
                throw failure;
            },
        };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => FailFastParallel.WhenAllAsync(work, CancellationToken.None));

        Assert.Same(failure, thrown);
        Assert.True(siblingCancelled);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), "The slow sibling should have been cancelled, not waited for.");
    }

    [Fact]
    public async Task WhenAllAsync_CallerCancellation_Propagates()
    {
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FailFastParallel.WhenAllAsync(
            [token => Task.Delay(TimeSpan.FromMinutes(5), token).ContinueWith(_ => 1, token)], caller.Token));
    }
}
