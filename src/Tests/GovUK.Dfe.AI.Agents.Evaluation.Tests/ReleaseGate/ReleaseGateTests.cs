using GovUK.Dfe.AI.Agents.Exceptions;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.Extensions;
using NSubstitute.ExceptionExtensions;
using Xunit;
using GovUK.Dfe.AI.Agents.Evaluation.ValueObjects;
using GovUK.Dfe.AI.Agents.Evaluation.Exceptions;

namespace GovUK.Dfe.AI.Agents.Evaluation.Tests.ReleaseGate;

/// <summary>The release gate: test cases on temporary copies, reports, scores, baselines, groups and clean-up.</summary>
public sealed class ReleaseGateTests : IDisposable
{
    private const string Outside = "outside what I can answer";

    private readonly ReleaseGateTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Passes_every_agent_whose_cases_pass_and_tests_temporary_copies_only()
    {
        _host.AddCaseForEveryAgent(mustMention: ["Good"]);

        var results = await _host.Build().EvaluateAsync(ReleaseGateTestHost.Agents, Token);

        Assert.All(results, r => Assert.True(r.Passed));
        await _host.AgentService.DidNotReceive().RunAsync(
            Arg.Is<AgentDefinition>(d => d.IsManagedAgent), Arg.Any<string>(), Arg.Any<AgentEvidence?>(), Arg.Any<CancellationToken>());
        Assert.All(ReleaseGateTestHost.Agents, a => Assert.True(File.Exists(Path.Combine(_host.ReportsDirectory, $"{a.Name}.json"))));
    }

    [Fact]
    public async Task Fails_an_agent_whose_answer_misses_a_fact()
    {
        _host.AddCaseForEveryAgent(caseName: "outcome", mustMention: ["Outstanding"]);

        var results = await _host.Build().EvaluateAsync(ReleaseGateTestHost.Agents, Token);

        Assert.All(results, r => Assert.Contains("Case outcome: Doesn't mention \"Outstanding\"", r.Problems));
    }

    [Fact]
    public async Task Fails_an_agent_with_no_test_cases()
    {
        var results = await _host.Build().EvaluateAsync(ReleaseGateTestHost.Agents, Token);

        Assert.All(results, r => Assert.StartsWith("No test cases found in ", Assert.Single(r.Problems), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Runs_cases_in_parallel_up_to_the_limit_and_each_the_configured_number_of_times()
    {
        _host.AddCaseForEveryAgent(caseName: "a");
        _host.AddCaseForEveryAgent(caseName: "b");
        var running = 0;
        var mostAtOnce = 0;
        _host.AgentService.Configure().RunAsync(default!, default!, default, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(async call =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref mostAtOnce, now);
            await Task.Delay(30, call.Arg<CancellationToken>());
            Interlocked.Decrement(ref running);
            return new AgentResult { AgentName = call.Arg<AgentDefinition>().Name, Output = "Rated Good [Evidence 1]." };
        });

        await _host.Build(settings: new() { ["MaxParallelTestRuns"] = "2", ["Repeats"] = "2" })
            .EvaluateAsync(ReleaseGateTestHost.Agents, Token);

        Assert.Equal(2, mostAtOnce);
        await _host.AgentService.ReceivedWithAnyArgs(ReleaseGateTestHost.Agents.Length * 2 * 2).RunAsync(default!, default!, default, Arg.Any<CancellationToken>());
    }

    // Which blocks pass is the test runner's; a block by mistake is in ReleaseCheckTests, with its section.
    [Fact]
    public async Task A_block_the_case_allows_passes_and_is_logged_as_the_guardrail_doing_its_job()
    {
        _host.AddCaseForEveryAgent(caseName: "injected-instruction", guardrailMayBlock: true);
        _host.AgentService.Configure().RunAsync(default!, default!, default, Arg.Any<CancellationToken>()).ThrowsAsyncForAnyArgs(call =>
            new AgentGuardrailException(call.Arg<AgentDefinition>().Name, AgentGuardrailException.PromptStage));

        var results = await _host.Build().EvaluateAsync(ReleaseGateTestHost.Agents, Token);

        Assert.All(results, r => Assert.True(r.Passed));
        Assert.Contains((LogLevel.Information, "ofsted-agent / injected-instruction: blocked by the guardrail, as this case allows"), _host.Logs);
    }

    [Fact]
    public async Task Fails_a_metric_below_the_minimum_or_not_scored()
    {
        _host.AddCaseForEveryAgent();
        _host.Judge.EvaluateAsync(default!, Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(new Dictionary<string, double> { [JudgeMetrics.Groundedness] = 2 });

        var results = await _host.Build(judge: true).EvaluateAsync(ReleaseGateTestHost.Agents, Token);

        Assert.All(results, r =>
        {
            Assert.Contains($"{JudgeMetrics.Groundedness}: averaged 2, below the minimum 3.5", r.Problems);
            Assert.Contains($"{JudgeMetrics.Relevance}: not scored", r.Problems);
        });
    }

    [Fact]
    public async Task Leaves_cases_expecting_a_refusal_out_of_the_scores()
    {
        const string Weather = "What will the weather be?";
        _host.AddCaseForEveryAgent(mustMention: ["Good"]);
        _host.AddCaseForEveryAgent(caseName: "out-of-scope", prompt: Weather, mustMention: [Outside]);
        _host.AnswerWith($"This is {Outside}. Rated Good [Evidence 1].");
        _host.Judge.EvaluateAsync(default!, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(call =>
            JudgeMetrics.All.ToDictionary(m => m, _ => call.Arg<CompletedAgentRun>().Prompt == Weather ? 1.0 : 5.0));

        var results = await _host.Build(judge: true, configure: s => s.RefusalPhrases.Add(Outside))
            .EvaluateAsync(ReleaseGateTestHost.Agents, Token);

        Assert.All(results, r => Assert.True(r.Passed, string.Join("; ", r.Problems)));
        Assert.Contains(_host.Logs, l => l.Message.EndsWith("(1 cases expecting a refusal not scored)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fails_when_one_group_scores_much_worse_than_another()
    {
        const string Special = "What about the special school?";
        _host.AddCaseForEveryAgent(caseName: "academy", group: "academy");
        _host.AddCaseForEveryAgent(caseName: "special", prompt: Special, group: "special-school");
        _host.Judge.EvaluateAsync(default!, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(call =>
            JudgeMetrics.All.ToDictionary(m => m, _ => call.Arg<CompletedAgentRun>().Prompt == Special ? 3.6 : 4.8));

        var results = await _host.Build(judge: true, settings: new() { ["MaxGroupGap"] = "0.5" })
            .EvaluateAsync(ReleaseGateTestHost.Agents, Token);

        Assert.All(results, r =>
            Assert.Contains($"{JudgeMetrics.Groundedness}: special-school 3.6, academy 4.8: a gap over 0.5", r.Problems));
    }

    [Theory]
    [InlineData(4.2, false)]
    [InlineData(4.7, true)]
    public async Task Holds_scores_to_the_baseline_within_the_tolerance(double score, bool passes)
    {
        _host.AddCaseForEveryAgent();
        _host.SaveBaselineForEveryAgent(4.8);
        _host.JudgeScores(score);

        var results = await _host.Build(judge: true).EvaluateAsync(ReleaseGateTestHost.Agents, Token);

        Assert.All(results, r => Assert.Equal(passes, r.Passed));
        if (!passes)
        {
            Assert.Contains(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{JudgeMetrics.Groundedness}: fell from 4.8 to {score}, more than the tolerance 0.2"),
                results[0].Problems);
        }
    }

    [Fact]
    public async Task An_unreadable_baseline_throws_and_still_deletes_leftover_test_agents()
    {
        _host.AddCaseForEveryAgent();
        _host.JudgeScores(4.5);
        _host.SaveUnreadableBaseline("trust-agent");

        var ex = await Assert.ThrowsAsync<ReleaseGateException>(() =>
            _host.Build(judge: true).EvaluateAsync(ReleaseGateTestHost.Agents, Token));

        Assert.Contains("trust-agent.json isn't a readable evaluation report", ex.Message, StringComparison.Ordinal);
        await _host.Runtime.ReceivedWithAnyArgs(1).DeleteOrphanedEphemeralAgentsAsync(default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Invalid_settings_fail_startup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            _host.Build(settings: new() { ["MinimumScore"] = "7", ["Repeats"] = "0" }));

        Assert.Contains("Evaluation:ReleaseGate:MinimumScore (from 1 to 5)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Evaluation:ReleaseGate:Repeats (1 or more)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logs_each_case_and_agent()
    {
        _host.AddCaseForEveryAgent(caseName: "outcome", mustMention: ["Outstanding"]);

        await _host.Build().EvaluateAsync(ReleaseGateTestHost.Agents, Token);

        Assert.Contains(_host.Logs, l => l.Level == LogLevel.Warning
            && l.Message.StartsWith("ofsted-agent / outcome: failed", StringComparison.Ordinal));
        Assert.Contains(_host.Logs, l => l.Message.StartsWith("trust-agent failed 1 release checks", StringComparison.Ordinal));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value
            && Interlocked.CompareExchange(ref target, value, current) != current)
        {
            // Another thread changed it; try again.
        }
    }
}
