using GovUK.Dfe.AI.Agents.Evaluation.Options;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks.Fairness;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Policy;
using Xunit;
using static GovUK.Dfe.AI.Agents.Evaluation.Tests.ResponsibleAI.QualityGateTestData;

namespace GovUK.Dfe.AI.Agents.Evaluation.Tests.ResponsibleAI.Fairness;

/// <summary>
/// Fairness: the agent serves every user group equally well. Risk: an agent that gets one group's facts wrong, or is
/// judged less grounded for them, more often than others'.
/// </summary>
public sealed class FairnessCheckTests
{
    /// <summary>Two cases per group, so the comparison logic can be tested with small groups.</summary>
    private static readonly QualityGatePolicy TwoPerGroup = new() { MinimumCasesPerGroup = 2 };

    [Fact]
    public async Task Fails_when_one_groups_pass_rate_trails_anothers_by_more_than_the_allowed_gap()
    {
        var context = CheckContext([Passing("a1", "academy"), Passing("a2", "academy"), Passing("s1", "special"), Failing("s2", "special")], TwoPerGroup);

        var result = await OnlyAsync(new GroupOutcomeParityCheck(), context);

        Assert.Equal(CheckOutcome.Failed, result.Outcome);
        Assert.Equal(ResponsibleAiPrinciple.Fairness, result.Principle);
        Assert.Equal(CheckSeverity.High, result.Severity);
        Assert.Equal("special passes 50% of cases, academy 100%: a gap over 20%", result.Summary);
        Assert.Equal(["academy: 2 of 2 passed (100%)", "special: 1 of 2 passed (50%)"], result.Evidence);
        Assert.Equal(0.5, result.Measured);
    }

    [Fact]
    public async Task Passes_when_pass_rates_are_within_the_allowed_gap()
    {
        var context = CheckContext([Passing("a1", "academy"), Failing("a2", "academy"), Passing("s1", "special"), Failing("s2", "special")], TwoPerGroup);

        var result = await OnlyAsync(new GroupOutcomeParityCheck(), context);

        Assert.Equal(CheckOutcome.Passed, result.Outcome);
    }

    [Fact]
    public async Task Leaves_groups_with_too_few_cases_out_of_the_comparison()
    {
        var context = CheckContext([Passing("a1", "academy"), Passing("a2", "academy"), Failing("s1", "special")], TwoPerGroup);

        var result = await OnlyAsync(new GroupOutcomeParityCheck(), context);

        Assert.Equal(CheckOutcome.NotApplicable, result.Outcome);
    }

    [Fact]
    public async Task Fails_when_judge_scores_differ_between_groups_by_more_than_the_gap_reusing_the_release_gates_group_gaps()
    {
        var context = CheckContext([Passing("a1", "academy", score: 4.8), Passing("s1", "special", score: 3.2)],
            releaseGate: new ReleaseGateSettings { MaxGroupGap = 1.0 });

        var result = await OnlyAsync(new GroupScoreParityCheck(), context);

        Assert.Equal(CheckOutcome.Failed, result.Outcome);
        Assert.StartsWith("Groundedness: special 3.2, academy 4.8", result.Summary, StringComparison.Ordinal);
        Assert.Equal(1.0, result.Threshold);
    }

    [Fact]
    public async Task Holds_score_gaps_to_the_policys_stricter_gap_when_set()
    {
        var context = CheckContext([Passing("a1", "academy", score: 4.5), Passing("s1", "special", score: 4.0)],
            new QualityGatePolicy { MaxGroupScoreGap = 0.3 }, new ReleaseGateSettings { MaxGroupGap = 1.0 });

        Assert.Equal(CheckOutcome.Failed, (await OnlyAsync(new GroupScoreParityCheck(), context)).Outcome);
    }

    [Fact]
    public async Task Does_not_compare_scores_without_a_judge()
    {
        var context = CheckContext([Passing("a1", "academy"), Passing("s1", "special")], judge: null);

        Assert.Equal(CheckOutcome.NotApplicable, (await OnlyAsync(new GroupScoreParityCheck(), context)).Outcome);
    }

    [Fact]
    public async Task Blocks_when_no_case_names_a_group_because_fairness_is_then_untested()
    {
        var result = await OnlyAsync(new GroupSampleSizeCheck(), CheckContext([Passing("a"), Passing("b")]));

        Assert.Equal(CheckOutcome.Inconclusive, result.Outcome);
        Assert.Equal(CheckSeverity.High, result.Severity);
    }

    [Fact]
    public async Task Warns_when_a_group_has_too_few_cases_to_compare()
    {
        var result = await OnlyAsync(new GroupSampleSizeCheck(), CheckContext([Passing("a1", "academy"), Passing("a2", "academy"), Passing("s1", "special")], TwoPerGroup));

        Assert.Equal(CheckOutcome.Inconclusive, result.Outcome);
        Assert.Equal(CheckSeverity.Medium, result.Severity);
        Assert.Equal("Too few cases to compare special (fewer than 2 each)", result.Summary);
    }
}
