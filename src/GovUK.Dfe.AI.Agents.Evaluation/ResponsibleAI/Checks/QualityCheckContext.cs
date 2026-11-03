using GovUK.Dfe.AI.Agents.Evaluation.Options;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Governance;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Policy;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Scenarios;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Traceability;
using GovUK.Dfe.AI.Agents.Quality;
using GovUK.Dfe.AI.Agents.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks;

/// <summary>
/// Everything a check needs about one agent's release run: its definition, every test case with its answer and scores,
/// the baseline, the guardrail check, the policy, the governance records and the manifest. Checks read it; none runs
/// the agent again.
/// </summary>
public sealed record QualityCheckContext
{
    private static readonly AgentEvaluationReport NoResults = new(string.Empty, []);

    public required AgentDefinition Agent { get; init; }

    public required QualityGatePolicy Policy { get; init; }

    /// <summary>The release gate's settings: its minimum score, tolerance, group gap and refusal phrases.</summary>
    public required ReleaseGateSettings ReleaseGate { get; init; }

    public required ReleaseManifest Manifest { get; init; }

    /// <summary>Each test case with its result. Empty when none ran.</summary>
    public IReadOnlyList<EvaluationScenario> Scenarios { get; init; } = [];

    /// <summary>The release gate's report: every answer, failure and score, without scores for cases expecting a refusal.</summary>
    public AgentEvaluationReport Report { get; init; } = NoResults;

    /// <summary>The approved baseline, the same way; null when there's none.</summary>
    public AgentEvaluationReport? Baseline { get; init; }

    /// <summary>Where the release gate wrote the agent's answers.</summary>
    public string? ReportPath { get; init; }

    /// <summary>False when the guardrail wasn't in place, so no test case ran.</summary>
    public bool Evaluated { get; init; } = true;

    /// <summary>How many guardrail checks ran before testing, e.g. one from <c>AddGuardrails()</c>.</summary>
    public int GuardrailChecks { get; init; }

    /// <summary>Why the guardrail isn't in place, when it isn't.</summary>
    public IReadOnlyList<string> GuardrailProblems { get; init; } = [];

    /// <summary>Every problem the release gate found, as it reports them.</summary>
    public IReadOnlyList<string> ReleaseGateProblems { get; init; } = [];

    /// <summary>
    /// Every tool the agent is given in this app, by name, built-in ones included, e.g. "web_search". Null: its
    /// <see cref="AgentDefinition.AllowedTools"/>.
    /// </summary>
    public IReadOnlyList<string>? Tools { get; init; }

    /// <summary>Why the agent's tools couldn't be listed, when they couldn't, e.g. a tool server was unreachable.</summary>
    public string? ToolsProblem { get; init; }

    /// <summary>The judge model; null without a judge, when nothing is scored.</summary>
    public string? JudgeModel { get; init; }

    public bool HasJudge => JudgeModel is not null;

    /// <summary>The metrics the judge must score, e.g. Groundedness and Relevance.</summary>
    public IReadOnlyList<string> JudgeMetrics { get; init; } = [];

    /// <summary>
    /// The guardrail's blocklist entries (<c>AiAgents:Guardrails:Blocklists</c>) as regular expressions, by a name that
    /// never shows the term, e.g. "pupil-ids pattern 1". Empty when the app has none.
    /// </summary>
    public IReadOnlyDictionary<string, string> SensitiveDataPatterns { get; init; } = new Dictionary<string, string>();

    public GovernanceRecords Governance { get; init; } = GovernanceRecords.None;

    /// <summary>The day the gate runs, for expiry dates.</summary>
    public DateOnly Today { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);

    /// <summary>The agent's part of <see cref="Manifest"/>.</summary>
    public AgentManifest? AgentManifest => Manifest.Agents.FirstOrDefault(agent => agent.Name == Agent.Name);

    /// <summary>The cases that test answers rather than attacks.</summary>
    public IEnumerable<EvaluationScenario> EvaluationScenarios => Scenarios.Where(static scenario => !scenario.IsGuardrailCase);

    /// <summary>The evaluation cases the agent answered.</summary>
    public IEnumerable<EvaluationScenario> AnsweredScenarios => EvaluationScenarios.Where(static scenario => scenario.Answered);

    /// <summary>The policy's minimum for the metric, or the release gate's.</summary>
    public double MinimumScoreFor(string metric) =>
        Policy.MinimumScores.TryGetValue(metric, out var minimum) ? minimum : ReleaseGate.MinimumScore;

    /// <summary>The policy's largest group score gap, or the release gate's, or 1.0.</summary>
    public double MaxGroupScoreGap => Policy.MaxGroupScoreGap ?? ReleaseGate.MaxGroupGap ?? 1.0;

    /// <summary>
    /// Whether the case expects the agent to say it doesn't know (<c>"expectsUncertainty": true</c>, or a release gate
    /// refusal phrase), so the judge doesn't score it: relevance marks any such answer irrelevant, however right.
    /// </summary>
    public bool ExpectsRefusal(AgentTestCase testCase)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        return ReleaseGate.ExpectsRefusal(testCase);
    }
}
