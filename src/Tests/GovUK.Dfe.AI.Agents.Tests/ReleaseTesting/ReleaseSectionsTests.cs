using GovUK.Dfe.AI.Agents.Models;
using GovUK.Dfe.AI.Agents.Options;
using GovUK.Dfe.AI.Agents.Quality;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Tests.ReleaseTesting;

/// <summary>The guardrail and evaluation sections every app's release is reported in.</summary>
public sealed class ReleaseSectionsTests
{
    private static readonly IReadOnlyDictionary<string, double> NoScores = new Dictionary<string, double>();

    [Theory]
    [InlineData("guardrail/jailbreak-prompt", false, ReleaseSections.Guardrail)]
    [InlineData("evaluation/ks2-trend", false, ReleaseSections.Evaluation)]
    [InlineData("ks2-trend", false, ReleaseSections.Evaluation)]                       // no folder: an evaluation case
    [InlineData("evaluation/guardrail-wording", false, ReleaseSections.Evaluation)]    // by folder, not by wording
    [InlineData("evaluation/ks2-trend", true, ReleaseSections.Guardrail)]              // blocked by mistake
    public void A_results_section_is_its_cases_folder_or_the_guardrails_when_wrongly_blocked(string caseName, bool wronglyBlocked, string section) =>
        Assert.Equal(section, ReleaseSections.Of(new AgentTestResult(caseName, null, ["failed"], NoScores) { BlockedByGuardrailInError = wronglyBlocked }));

    [Theory]
    [InlineData("guardrail/jailbreak", true, false, null, 0)]
    [InlineData("evaluation/outcome", false, false, "academy", 0)]
    [InlineData("guardrail/jailbreak", false, true, null, 1)]          // only allowing the block proves nothing
    [InlineData("guardrail/jailbreak", true, false, "academy", 1)]     // guardrail cases aren't scored
    [InlineData("evaluation/outcome", false, true, null, 1)]           // a block there is the guardrail's mistake
    [InlineData("outcome", false, false, null, 1)]                     // not in a section's folder
    public void Every_apps_cases_follow_the_same_conventions(string name, bool mustBlock, bool mayBlock, string? group, int problems) =>
        Assert.Equal(problems, ReleaseSections.Problems(new AgentTestCase(name, "What was the outcome?")
        {
            GuardrailMustBlock = mustBlock,
            GuardrailMayBlock = mayBlock,
            Group = group,
        }).Count);

    [Fact]
    public void A_reports_case_failures_can_be_taken_by_section_and_its_score_failures_apart()
    {
        var report = new AgentEvaluationReport("ofsted-agent",
        [
            new AgentTestResult("guardrail/jailbreak", "x", ["Not blocked"], NoScores),
            new AgentTestResult("evaluation/outcome", "x", ["Doesn't mention \"Good\""], new Dictionary<string, double> { ["Relevance"] = 2.0 }),
        ]);
        var gate = new ReleaseGate { Metrics = ["Relevance"] };

        Assert.Equal(["Case guardrail/jailbreak: Not blocked"], report.CaseFailures(ReleaseSections.Guardrail));
        Assert.Equal(["Case evaluation/outcome: Doesn't mention \"Good\""], report.CaseFailures(ReleaseSections.Evaluation));
        Assert.Equal(["Relevance: averaged 2, below the minimum 3.5"], report.ScoreFailuresAgainst(gate));
        Assert.Equal([.. report.CaseFailures(), .. report.ScoreFailuresAgainst(gate)], report.FailuresAgainst(gate));
    }
}
