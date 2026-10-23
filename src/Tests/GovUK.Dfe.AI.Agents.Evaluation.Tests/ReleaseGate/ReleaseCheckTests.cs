using GovUK.Dfe.AI.Agents.Exceptions;
using GovUK.Dfe.AI.Agents.Extensibility.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NSubstitute.Extensions;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Evaluation.Tests.ReleaseGate;

/// <summary>
/// The standard release check every app runs: the guardrail must be in place, then each agent's guardrail and
/// evaluation test cases, each problem reported in its section and logged in the form the standard queries read.
/// </summary>
public sealed class ReleaseCheckTests : IDisposable
{
    private const string Guardrail = "Foundry guardrail test-guardrail";

    private readonly ReleaseGateTestHost _host = new();
    private readonly IGuardrailCheck _guardrail = Substitute.For<IGuardrailCheck>();

    public ReleaseCheckTests()
    {
        _guardrail.Name.Returns(Guardrail);
        GuardrailHas();
        _host.GuardrailChecks.Add(_guardrail);
    }

    public void Dispose() => _host.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Releases_every_agent_when_the_guardrail_is_in_place_and_every_case_passes()
    {
        _host.AddCaseForEveryAgent(caseName: "evaluation/outcome", mustMention: ["Good"]);

        var results = await _host.Build().CheckReleaseAsync(ReleaseGateTestHost.Agents, Token);

        Assert.All(results, result => Assert.True(result.Passed && result.Evaluated));
        Assert.Contains((LogLevel.Information, "ofsted-agent: guardrail passed, evaluation passed"), _host.Logs);
        Assert.Contains((LogLevel.Information, "All 2 agents passed their release checks"), _host.Logs);
        Assert.DoesNotContain(_host.Logs, log => log.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Reports_each_failure_in_its_section_by_the_cases_folder()
    {
        _host.AddCase("ofsted-agent", caseName: "guardrail/jailbreak", guardrailMustBlock: true);
        _host.AddCase("ofsted-agent", caseName: "evaluation/outcome", mustMention: ["Outstanding"]);

        var result = (await _host.Build().CheckReleaseAsync([ReleaseGateTestHost.Agents[0]], Token))[0];

        Assert.StartsWith("Case guardrail/jailbreak: Not blocked by the guardrail", Assert.Single(result.GuardrailProblems), StringComparison.Ordinal);
        Assert.Equal(["Case evaluation/outcome: Doesn't mention \"Outstanding\""], result.EvaluationProblems);
        Assert.Contains((LogLevel.Warning, "ofsted-agent: guardrail failed (1 problems), evaluation failed (1 problems)"), _host.Logs);
    }

    [Fact]
    public async Task Reports_an_evaluation_case_the_guardrail_blocked_by_mistake_as_the_guardrails()
    {
        _host.AddCase("ofsted-agent", caseName: "evaluation/outcome", mustMention: ["Good"]);
        _host.AgentService.Configure().RunAsync(default!, default!, default, Token)
            .ThrowsAsyncForAnyArgs(new AgentGuardrailException("ofsted-agent", "prompt"));

        var result = (await _host.Build().CheckReleaseAsync([ReleaseGateTestHost.Agents[0]], Token))[0];

        Assert.StartsWith("Case evaluation/outcome: Blocked by the guardrail, which this case doesn't allow",
            Assert.Single(result.GuardrailProblems), StringComparison.Ordinal);
        Assert.Empty(result.EvaluationProblems);
    }

    [Theory]
    [InlineData(false, "blocklist 'personal-identifiers' doesn't exist")]
    [InlineData(true, "The " + Guardrail + " couldn't be checked: Reader role missing")]   // so a release never skips it
    public async Task Tests_nothing_and_fails_every_agent_when_the_guardrail_is_not_in_place_or_cannot_be_checked(
        bool unreadable, string problem)
    {
        _host.AddCaseForEveryAgent(caseName: "evaluation/outcome", mustMention: ["Good"]);
        if (unreadable)
        {
            _guardrail.CheckAsync(Token).ThrowsAsyncForAnyArgs(new InvalidOperationException("Reader role missing"));
        }
        else
        {
            GuardrailHas("blocklist 'personal-identifiers' doesn't exist");
        }

        var results = await _host.Build().CheckReleaseAsync(ReleaseGateTestHost.Agents, Token);

        Assert.All(results, result =>
        {
            Assert.False(result.Evaluated);
            Assert.Equal([problem], result.GuardrailProblems);
        });
        await _host.AgentService.DidNotReceiveWithAnyArgs().RunAsync(default!, default!, default, Token);
        Assert.Contains((LogLevel.Warning, "trust-agent: guardrail failed (1 problems), evaluation not run"), _host.Logs);
    }

    [Fact]
    public async Task Logs_each_reason_as_an_error_whose_fields_the_standard_queries_filter_on()
    {
        GuardrailHas("the guardrail doesn't exist");
        var gate = _host.Build();

        string runId;
        using (var run = gate.BeginRun())
        {
            runId = run.Id;
            await gate.CheckReleaseAsync(ReleaseGateTestHost.Agents, Token);
        }

        // Fields, not message text, so rewording a message never breaks a query.
        var reasons = _host.LogFields.Where(log => log.Level == LogLevel.Error && log.Fields.ContainsKey("ReleaseSection"))
            .Select(log => (log.Fields["ReleaseRunId"], log.Fields["AgentName"], log.Fields["ReleaseSection"], log.Fields["ReleaseProblem"]));
        Assert.Equal(
        [
            (runId, "ofsted-agent", "guardrail", "the guardrail doesn't exist"),
            (runId, "trust-agent", "guardrail", "the guardrail doesn't exist"),
        ], reasons);
        Assert.Contains((LogLevel.Error, $"Agent release blocked by gate: 2 of 2 agents failed (release run {runId})"), _host.Logs);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Without_a_guardrail_check_fails_every_agent_unless_none_is_required(bool required)
    {
        _host.GuardrailChecks.Clear();
        _host.AddCaseForEveryAgent(caseName: "evaluation/outcome", mustMention: ["Good"]);

        var results = await _host.Build(settings: new() { ["RequireGuardrail"] = required.ToString() })
            .CheckReleaseAsync(ReleaseGateTestHost.Agents, Token);

        Assert.All(results, result => Assert.Equal(!required, result.Passed));
        if (required)
        {
            Assert.All(results, result => Assert.StartsWith("No guardrail is checked before release",
                Assert.Single(result.GuardrailProblems), StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Fails_cases_that_break_the_conventions_each_in_its_section()
    {
        _host.AddCase("ofsted-agent", caseName: "guardrail/planted-instruction", guardrailMayBlock: true);
        _host.AddCase("ofsted-agent", caseName: "evaluation/outcome", guardrailMayBlock: true);
        _host.AddCase("ofsted-agent", caseName: "outcome");

        var result = (await _host.Build().CheckReleaseAsync([ReleaseGateTestHost.Agents[0]], Token))[0];

        Assert.Equal(["Test case guardrail/planted-instruction must set \"guardrailMustBlock\": true, so it proves the guardrail blocks it."],
            result.GuardrailProblems);
        Assert.Equal(
        [
            "Test case evaluation/outcome is an evaluation case, so it can't allow a guardrail block: move it to guardrail/.",
            "Test case outcome must be in its agent's guardrail/ or evaluation/ folder.",
        ], result.EvaluationProblems);
    }

    private void GuardrailHas(params string[] problems) =>
        _guardrail.CheckAsync(Token).ReturnsForAnyArgs(problems);
}
