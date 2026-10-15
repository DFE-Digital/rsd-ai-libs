using System.Diagnostics.Metrics;
using GovUK.Dfe.AI.Agents.Diagnostics;

namespace GovUK.Dfe.AI.Agents.Evaluation.QualityGates;

/// <summary>
///Provides telemetry instruments for recording release-gate metrics for AI agents.
/// </summary> 
internal static class ReleaseGateMetrics
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string BlockedByGuardrail = "blocked_by_guardrail";

    private const string AgentNameTag = "gen_ai.agent.name";
    private const string EvaluationNameTag = "gen_ai.evaluation.name";
    private const string OutcomeTag = "dfe.ai_agents.release_gate.outcome";

    private static readonly Meter Meter = new(AgentTelemetry.SourceName);

    internal static readonly Counter<long> TestCases = Meter.CreateCounter<long>(
        "dfe.ai_agents.release_gate.test_cases", "{case}", "Release-gate test cases run, by agent and outcome.");

    internal static readonly Histogram<double> Scores = Meter.CreateHistogram<double>(
        "dfe.ai_agents.release_gate.score", "{score}", "An agent's average judge score in a release-gate run, by metric.");

    internal static readonly Counter<long> Agents = Meter.CreateCounter<long>(
        "dfe.ai_agents.release_gate.agents", "{agent}", "Agents through the release gate, by outcome.");

    public static void RecordTestCase(string agentName, string outcome) =>
        TestCases.Add(1, new(AgentNameTag, agentName), new(OutcomeTag, outcome));

    public static void RecordScores(string agentName, IReadOnlyDictionary<string, double> averageScores)
    {
        foreach (var (metric, score) in averageScores)
        {
            Scores.Record(score, new(AgentNameTag, agentName), new(EvaluationNameTag, metric));
        }
    }

    public static void RecordAgent(string agentName, bool passed) =>
        Agents.Add(1, new(AgentNameTag, agentName), new(OutcomeTag, passed ? Passed : Failed));
}
