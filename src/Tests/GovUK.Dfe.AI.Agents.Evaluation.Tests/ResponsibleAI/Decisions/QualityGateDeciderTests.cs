using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Decisions;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Governance;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Policy;
using GovUK.Dfe.AI.Agents.Evaluation.ValueObjects;
using Xunit;
using static GovUK.Dfe.AI.Agents.Evaluation.Tests.ResponsibleAI.QualityGateTestData;

namespace GovUK.Dfe.AI.Agents.Evaluation.Tests.ResponsibleAI.Decisions;

/// <summary>
/// The release decision model: Pass, Warning or Fail from each result's own severity, never from an aggregate score;
/// risk acceptances, critical exceptions, and principles left untested.
/// </summary>
public sealed class QualityGateDeciderTests
{
    private const string AgentName = "briefing-agent";

    private static AgentReleaseDecision Decide(IEnumerable<QualityCheckResult> results, QualityGatePolicy? policy = null,
        GovernanceRecords? governance = null, bool checkCoverage = true) =>
        QualityGateDecider.Decide(AgentName, [.. results], policy ?? new QualityGatePolicy(), governance ?? GovernanceRecords.None, Today, checkCoverage);

    [Fact]
    public void Passes_and_permits_release_when_every_result_passes_and_every_principle_is_covered()
    {
        var decision = Decide(EveryPrinciplePasses());

        Assert.Equal(ReleaseDecision.Pass, decision.Decision);
        Assert.True(decision.ReleasePermitted);
        Assert.All(decision.Principles, p => Assert.Equal(ReleaseDecision.Pass, p.Decision));
    }

    [Theory]
    [InlineData(CheckSeverity.Critical)]
    [InlineData(CheckSeverity.High)]
    public void Fails_and_blocks_release_on_a_mandatory_failure(CheckSeverity severity)
    {
        var decision = Decide([.. EveryPrinciplePasses(), Finding(CheckOutcome.Failed, severity)]);

        Assert.Equal(ReleaseDecision.Fail, decision.Decision);
        Assert.False(decision.ReleasePermitted);
        Assert.Contains(decision.Reasons, r => r.StartsWith($"Blocking: [{severity}] ReliabilityAndSafety reliability.example", StringComparison.Ordinal));
    }

    [Fact]
    public void Treats_an_inconclusive_mandatory_result_as_a_failure_because_missing_evidence_is_never_a_pass()
    {
        var decision = Decide([.. EveryPrinciplePasses(), Finding(CheckOutcome.Inconclusive, CheckSeverity.High)]);

        Assert.Equal(ReleaseDecision.Fail, decision.Decision);
    }

    [Fact]
    public void Never_lets_high_judge_scores_compensate_for_a_critical_privacy_failure()
    {
        var perfectScores = Enumerable.Range(0, 50).Select(i => Finding(CheckOutcome.Passed, checkId: "reliability.judge-score", subject: $"metric-{i}"));
        var leak = Finding(CheckOutcome.Failed, CheckSeverity.Critical, ResponsibleAiPrinciple.PrivacyAndSecurity, QualityCheckIds.SensitiveDataLeakage);

        var decision = Decide([.. EveryPrinciplePasses(), .. perfectScores, leak]);

        Assert.Equal(ReleaseDecision.Fail, decision.Decision);
        Assert.Equal(ReleaseDecision.Fail, decision.Principles.Single(p => p.Principle == ResponsibleAiPrinciple.PrivacyAndSecurity).Decision);
        Assert.Equal(ReleaseDecision.Pass, decision.Principles.Single(p => p.Principle == ResponsibleAiPrinciple.ReliabilityAndSafety).Decision);
    }

    [Fact]
    public void Warns_on_a_medium_failure_and_blocks_until_its_risk_is_accepted()
    {
        var decision = Decide([.. EveryPrinciplePasses(), Finding(CheckOutcome.Failed, CheckSeverity.Medium)]);

        Assert.Equal(ReleaseDecision.Warning, decision.Decision);
        Assert.False(decision.ReleasePermitted);
        Assert.Equal("1 warning(s) need a risk acceptance before release.", decision.Reasons[0]);
    }

    [Fact]
    public void Treats_a_low_failure_as_advisory_so_only_medium_warnings_need_a_risk_acceptance()
    {
        var decision = Decide([.. EveryPrinciplePasses(), Finding(CheckOutcome.Failed, CheckSeverity.Low),
            Finding(CheckOutcome.Failed, CheckSeverity.Medium, subject: "other")]);

        Assert.Equal(ReleaseDecision.Warning, decision.Decision);
        Assert.False(decision.ReleasePermitted);
        Assert.Equal("1 warning(s) need a risk acceptance before release.", decision.Reasons[0]);

        var advisoryOnly = Decide([.. EveryPrinciplePasses(), Finding(CheckOutcome.Failed, CheckSeverity.Low)]);

        Assert.Equal((ReleaseDecision.Warning, true), (advisoryOnly.Decision, advisoryOnly.ReleasePermitted));
    }

    [Fact]
    public void Permits_a_warning_without_acceptance_when_the_policy_does_not_require_one()
    {
        var decision = Decide([.. EveryPrinciplePasses(), Finding(CheckOutcome.Failed, CheckSeverity.Medium)],
            new QualityGatePolicy { RequireRiskAcceptanceForWarnings = false });

        Assert.Equal(ReleaseDecision.Warning, decision.Decision);
        Assert.True(decision.ReleasePermitted);
    }

    [Fact]
    public void Blocks_a_warning_even_when_accepted_if_the_policy_allows_no_warnings()
    {
        var governance = new GovernanceRecords { RiskAcceptances = [Acceptance("reliability.example")] };

        var decision = Decide([.. EveryPrinciplePasses(), Finding(CheckOutcome.Failed, CheckSeverity.Medium)],
            new QualityGatePolicy { AllowReleaseWithWarnings = false }, governance);

        Assert.Equal(ReleaseDecision.Warning, decision.Decision);
        Assert.False(decision.ReleasePermitted);
        Assert.Equal("The policy doesn't allow release with warnings.", decision.Reasons[0]);
    }

    [Fact]
    public void Turns_an_accepted_high_failure_into_a_visible_warning_that_permits_release()
    {
        var governance = new GovernanceRecords { RiskAcceptances = [Acceptance("reliability.example")] };

        var decision = Decide([.. EveryPrinciplePasses(), Finding(CheckOutcome.Failed, CheckSeverity.High)], governance: governance);

        Assert.Equal(ReleaseDecision.Warning, decision.Decision);
        Assert.True(decision.ReleasePermitted);
        var accepted = Assert.Single(decision.With(FindingDisposition.RiskAccepted));
        Assert.Equal("RISK-1", accepted.Acceptance?.Reference);
        Assert.Contains(decision.Reasons, r => r.Contains("Accepted by Sam Owner (Senior Responsible Owner), ref RISK-1", StringComparison.Ordinal));
    }

    [Fact]
    public void Never_lets_a_risk_acceptance_waive_a_critical_failure_unless_the_policy_allows_critical_exceptions()
    {
        var governance = new GovernanceRecords { RiskAcceptances = [Acceptance("reliability.example")] };
        QualityCheckResult[] results = [.. EveryPrinciplePasses(), Finding(CheckOutcome.Failed, CheckSeverity.Critical)];

        var strict = Decide(results, governance: governance);
        var allowing = Decide(results, new QualityGatePolicy { AllowCriticalExceptions = true }, governance);

        Assert.Equal(ReleaseDecision.Fail, strict.Decision);
        Assert.Contains("can't waive a critical failure", Assert.Single(strict.With(FindingDisposition.Blocking)).Note, StringComparison.Ordinal);
        Assert.Equal(ReleaseDecision.Warning, allowing.Decision);
        Assert.True(allowing.ReleasePermitted);
    }

    [Theory]
    [InlineData(-60, -1, "it expired on 2026-11-02")]
    [InlineData(-7, 400, "it lasts more than 180 days")]
    [InlineData(3, 30, "it isn't accepted until 2026-11-06")]
    public void Ignores_and_lists_a_risk_acceptance_that_is_expired_too_long_or_not_yet_valid(int acceptedDays, int expiresDays, string problem)
    {
        var governance = new GovernanceRecords
        {
            RiskAcceptances = [Acceptance("reliability.example", acceptedOn: Today.AddDays(acceptedDays), expiresOn: Today.AddDays(expiresDays))],
        };

        var decision = Decide([.. EveryPrinciplePasses(), Finding(CheckOutcome.Failed, CheckSeverity.High)], governance: governance);

        Assert.Equal(ReleaseDecision.Fail, decision.Decision);
        Assert.Contains(problem, Assert.Single(decision.IgnoredRiskAcceptances), StringComparison.Ordinal);
    }

    [Fact]
    public void Applies_a_risk_acceptance_only_to_its_own_agent_check_and_subject()
    {
        var governance = new GovernanceRecords
        {
            RiskAcceptances =
            [
                Acceptance("reliability.example", agent: "another-agent"),
                Acceptance("reliability.other"),
                Acceptance("reliability.example", subject: "case-b"),
            ],
        };

        var decision = Decide([.. EveryPrinciplePasses(), Finding(CheckOutcome.Failed, subject: "case-a"), Finding(CheckOutcome.Failed, subject: "case-b")],
            governance: governance);

        Assert.Equal("case-a", Assert.Single(decision.With(FindingDisposition.Blocking)).Result.Subject);
        Assert.Equal("case-b", Assert.Single(decision.With(FindingDisposition.RiskAccepted)).Result.Subject);
    }

    [Fact]
    public void Applies_a_risk_acceptance_for_every_agent_when_its_agent_is_a_star()
    {
        var governance = new GovernanceRecords { RiskAcceptances = [Acceptance("reliability.example", agent: "*")] };

        var decision = Decide([.. EveryPrinciplePasses(), Finding(CheckOutcome.Failed)], governance: governance);

        Assert.Single(decision.With(FindingDisposition.RiskAccepted));
    }

    [Fact]
    public void Fails_a_required_principle_that_no_check_evaluated()
    {
        var results = EveryPrinciplePasses().Where(r => r.Principle != ResponsibleAiPrinciple.Fairness)
            .Append(Finding(CheckOutcome.NotApplicable, principle: ResponsibleAiPrinciple.Fairness));

        var decision = Decide(results);

        var gap = Assert.Single(decision.With(FindingDisposition.Blocking));
        Assert.Equal(QualityCheckIds.PrincipleCoverage, gap.Result.CheckId);
        Assert.Equal(ResponsibleAiPrinciple.Fairness, gap.Result.Principle);
        Assert.Equal(ReleaseDecision.Fail, decision.Principles.Single(p => p.Principle == ResponsibleAiPrinciple.Fairness).Decision);
    }

    [Fact]
    public void Requires_only_the_principles_the_policy_lists()
    {
        var decision = Decide([Finding(CheckOutcome.Passed, principle: ResponsibleAiPrinciple.PrivacyAndSecurity)],
            new QualityGatePolicy { Principles = [ResponsibleAiPrinciple.PrivacyAndSecurity] });

        Assert.Equal(ReleaseDecision.Pass, decision.Decision);
        Assert.Null(decision.Principles.Single(p => p.Principle == ResponsibleAiPrinciple.Fairness).Decision);
    }

    [Fact]
    public void Skips_coverage_when_nothing_could_run_so_the_cause_is_the_only_reason()
    {
        var decision = Decide([Finding(CheckOutcome.Failed, CheckSeverity.Critical, ResponsibleAiPrinciple.PrivacyAndSecurity, QualityCheckIds.GuardrailInPlace)],
            checkCoverage: false);

        Assert.Equal(QualityCheckIds.GuardrailInPlace, Assert.Single(decision.With(FindingDisposition.Blocking)).Result.CheckId);
    }

    [Fact]
    public void Orders_findings_blocking_first_and_lists_each_recommendation_once()
    {
        var decision = Decide([.. EveryPrinciplePasses(), Finding(CheckOutcome.Failed, CheckSeverity.Medium), Finding(CheckOutcome.Failed, CheckSeverity.High),
            Finding(CheckOutcome.Failed, CheckSeverity.High)]);

        Assert.Equal([FindingDisposition.Blocking, FindingDisposition.Blocking, FindingDisposition.Warning],
            decision.Findings.Take(3).Select(f => f.Disposition));
        Assert.Equal(["Fix reliability.example"], decision.Recommendations);
    }

    [Fact]
    public void Combines_agents_into_the_worst_decision_and_permits_release_only_when_every_agent_is_permitted()
    {
        var pass = Decide(EveryPrinciplePasses());
        var warning = Decide([.. EveryPrinciplePasses(), Finding(CheckOutcome.Failed, CheckSeverity.Medium)],
            new QualityGatePolicy { RequireRiskAcceptanceForWarnings = false });
        var fail = Decide([.. EveryPrinciplePasses(), Finding(CheckOutcome.Failed, CheckSeverity.Critical)]);

        Assert.Equal((ReleaseDecision.Warning, true), QualityGateDecider.Combine([pass, warning]));
        Assert.Equal((ReleaseDecision.Fail, false), QualityGateDecider.Combine([pass, warning, fail]));
        Assert.Equal((ReleaseDecision.Fail, false), QualityGateDecider.Combine([]));
    }
}
