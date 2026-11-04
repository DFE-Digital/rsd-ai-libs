namespace GovUK.Dfe.AI.Agents.Evaluation.Metrics;

/// <summary>
/// The release gate's metrics, recorded with <see cref="IAgentMetrics"/> to the library's standard: tagged with the app
/// </summary>
public static class ReleaseGateTelemetry
{
    /// <summary>Agents through the gate, by <see cref="OutcomeTag"/>: passed or failed.</summary>
    public const string AgentsMetric = "dfe.ai_agents.release_gate.agents";

    /// <summary>Test cases run, by <see cref="TestCaseTag"/>, <see cref="GroupTag"/> and <see cref="OutcomeTag"/>.</summary>
    public const string TestCasesMetric = "dfe.ai_agents.release_gate.test_cases";

    /// <summary>An agent's average judge score in a run, by <see cref="EvaluationNameTag"/>.</summary>
    public const string ScoreMetric = "dfe.ai_agents.release_gate.score";

    /// <summary>The release run every metric recorded during it carries, from <c>IAgentReleaseGate.BeginRun</c>.</summary>
    public const string RunIdTag = "dfe.ai_agents.release_gate.run_id";

    /// <summary>Passed, failed or blocked_by_guardrail: see <c>AgentOutcomes</c>.</summary>
    public const string OutcomeTag = "dfe.ai_agents.release_gate.outcome";

    /// <summary>The test case's name, with its section and principle, e.g. "evaluation/fairness/FAIR-001".</summary>
    public const string TestCaseTag = "dfe.ai_agents.release_gate.test_case";

    /// <summary>The test case's group, e.g. "academy"; empty when it has none.</summary>
    public const string GroupTag = "dfe.ai_agents.release_gate.group";

    /// <summary>The judge's metric, e.g. "Groundedness", as OpenTelemetry's generative AI conventions name it.</summary>
    public const string EvaluationNameTag = "gen_ai.evaluation.name";

    /// <summary>Agents through the Responsible AI quality gate, by <see cref="DecisionTag"/> and <see cref="ReleasePermittedTag"/>.</summary>
    public const string QualityGateDecisionsMetric = "dfe.ai_agents.quality_gate.decisions";

    /// <summary>
    /// Quality gate check results, by <see cref="PrincipleTag"/>, <see cref="CheckTag"/>, <see cref="SeverityTag"/> and
    /// <see cref="DispositionTag"/>.
    /// </summary>
    public const string QualityGateFindingsMetric = "dfe.ai_agents.quality_gate.findings";

    /// <summary>Pass, Warning or Fail.</summary>
    public const string DecisionTag = "dfe.ai_agents.quality_gate.decision";

    /// <summary>"true" or "false".</summary>
    public const string ReleasePermittedTag = "dfe.ai_agents.quality_gate.release_permitted";

    /// <summary>The Responsible AI principle, e.g. "PrivacyAndSecurity".</summary>
    public const string PrincipleTag = "dfe.ai_agents.quality_gate.principle";

    /// <summary>The check id, e.g. "privacy.sensitive-data-leakage".</summary>
    public const string CheckTag = "dfe.ai_agents.quality_gate.check";

    /// <summary>Low, Medium, High or Critical.</summary>
    public const string SeverityTag = "dfe.ai_agents.quality_gate.severity";

    /// <summary>Passed, NotApplicable, Warning, RiskAccepted or Blocking.</summary>
    public const string DispositionTag = "dfe.ai_agents.quality_gate.disposition";
}
