using GovUK.Dfe.AI.Agents.Evaluation.Options;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Policy;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Evaluation.Tests.ResponsibleAI.Policy;

/// <summary>The risk-based release policy: read from configuration, checked for values out of range or more lenient than the release gate.</summary>
public sealed class QualityGatePolicyTests
{
    private static QualityGatePolicy Bind(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => $"QualityGate:{v.Key}", v => v.Value)).Build()
            .GetSection("QualityGate").Get<QualityGatePolicy>()!;

    [Fact]
    public void Defaults_evaluate_every_principle_and_require_risk_acceptance_and_risk_acceptance_for_warnings()
    {
        var policy = new QualityGatePolicy();

        Assert.Equal(Enum.GetValues<ResponsibleAiPrinciple>(), policy.EffectivePrinciples);
        Assert.True(policy.RequireRiskAcceptanceForWarnings);
        Assert.False(policy.AllowCriticalExceptions);
        Assert.Empty(policy.Problems());
    }

    [Fact]
    public void Reads_thresholds_check_overrides_and_attestations_from_configuration()
    {
        var policy = Bind(new()
        {
            ["MinimumScores:Groundedness"] = "4.0",
            ["MaxGroupPassRateGap"] = "0.1",
            ["RequiredGroups:0"] = "special",
            ["Principles:0"] = "PrivacyAndSecurity",
            ["Checks:inclusiveness.readability:Severity"] = "High",
            ["Checks:fairness.group-sample-size:Enabled"] = "false",
            ["RequiredAttestations:0:Id"] = "dpia",
            ["RequiredAttestations:0:Description"] = "DPIA approved",
            ["RequiredAttestations:0:Principle"] = "PrivacyAndSecurity",
        });

        Assert.Equal(4.0, policy.MinimumScores["groundedness"]);
        Assert.Equal(0.1, policy.MaxGroupPassRateGap);
        Assert.Equal(["special"], policy.RequiredGroups);
        Assert.Equal([ResponsibleAiPrinciple.PrivacyAndSecurity], policy.EffectivePrinciples);
        Assert.Equal(CheckSeverity.High, policy.For("Inclusiveness.Readability").Severity);
        Assert.False(policy.For("fairness.group-sample-size").Enabled);
        Assert.True(policy.For("unconfigured.check").Enabled);
        Assert.Equal(ResponsibleAiPrinciple.PrivacyAndSecurity, Assert.Single(policy.RequiredAttestations).Principle);
    }

    [Theory]
    [InlineData("MaxRiskAcceptanceDays", "0", "MaxRiskAcceptanceDays (1 or more)")]
    [InlineData("MinimumScores:Relevance", "6", "MinimumScores (each from 1 to 5)")]
    [InlineData("MaxGroupPassRateGap", "1.5", "MaxGroupPassRateGap (from 0 to 1)")]
    [InlineData("MinimumCasesPerGroup", "0", "MinimumCasesPerGroup (1 or more)")]
    [InlineData("MaxPassRateDrop", "-0.1", "MaxPassRateDrop (from 0 to 1)")]
    [InlineData("MinimumReadingEase", "101", "MinimumReadingEase (from 0 to 100)")]
    [InlineData("MaxAverageSentenceWords", "0", "MaxAverageSentenceWords (more than 0)")]
    [InlineData("ReportName", "a/b", "ReportName (a file name)")]
    [InlineData("RequiredAttestations:0:Id", "dpia", "RequiredAttestations (each needs an Id and a Description)")]
    public void Reports_each_setting_out_of_range(string setting, string value, string problem)
    {
        var policy = Bind(new() { [setting] = value });

        Assert.Contains(problem, policy.Problems());
    }

    [Fact]
    public void Puts_only_the_principles_a_team_chooses_in_scope()
    {
        var policy = new QualityGatePolicy { Principles = [ResponsibleAiPrinciple.ReliabilityAndSafety, ResponsibleAiPrinciple.PrivacyAndSecurity] };

        Assert.True(policy.InScope(ResponsibleAiPrinciple.PrivacyAndSecurity));
        Assert.False(policy.InScope(ResponsibleAiPrinciple.Fairness));
    }

    [Fact]
    public void Reads_the_guardrails_blocklists_without_showing_a_term_in_an_entry_name()
    {
        var aiAgents = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AiAgents:Guardrails:Blocklists:pupil-ids:Terms:0"] = "Project Falcon",
            ["AiAgents:Guardrails:Blocklists:pupil-ids:Patterns:0"] = @"\b[A-Z]\d{12}\b",
        }).Build().GetSection("AiAgents");

        var blocklists = GuardrailBlocklists.From(aiAgents);

        Assert.Equal(["pupil-ids term 1", "pupil-ids pattern 1"], blocklists.Patterns.Keys);
        Assert.Equal(@"(?i)Project\ Falcon", blocklists.Patterns["pupil-ids term 1"]);
    }

    [Fact]
    public void Rejects_duplicate_attestation_ids()
    {
        var policy = new QualityGatePolicy
        {
            RequiredAttestations = [new() { Id = "dpia", Description = "a" }, new() { Id = "DPIA", Description = "b" }],
        };

        Assert.Contains("RequiredAttestations (ids must be unique)", policy.Problems());
    }

    [Fact]
    public void May_be_stricter_than_the_release_gate_but_never_more_lenient()
    {
        var releaseGate = new ReleaseGateSettings { MinimumScore = 3.5, MaxGroupGap = 0.5 };

        Assert.Empty(new QualityGatePolicy { MinimumScores = { ["Groundedness"] = 4.0 }, MaxGroupScoreGap = 0.3 }.ConflictsWith(releaseGate));
        Assert.Equal(
            [
                "QualityGate:MinimumScores:Relevance (3) is below ReleaseGate:MinimumScore (3.5): lower that instead",
                "QualityGate:MaxGroupScoreGap (1) is above ReleaseGate:MaxGroupGap (0.5): raise that instead",
            ],
            new QualityGatePolicy { MinimumScores = { ["Relevance"] = 3.0 }, MaxGroupScoreGap = 1.0 }.ConflictsWith(releaseGate));
    }
}
