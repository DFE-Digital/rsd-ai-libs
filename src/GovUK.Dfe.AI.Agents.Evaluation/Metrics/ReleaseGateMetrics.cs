using GovUK.Dfe.AI.Agents.Diagnostics;
using GovUK.Dfe.AI.Agents.Diagnostics.Interfaces;
using GovUK.Dfe.AI.Agents.Release;

namespace GovUK.Dfe.AI.Agents.Evaluation.Metrics;

/// <summary>
/// The release gate's metrics, recorded with <see cref="IAgentMetrics"/> to the library's standard: tagged with the app
/// and agent, and with the open run's id. Names are in <see cref="ReleaseGateTelemetry"/>.
/// </summary>
internal sealed class ReleaseGateMetrics(IAgentMetrics metrics)
{
    private static readonly AgentCounter TestCases = new(ReleaseGateTelemetry.TestCasesMetric,
        "Release-gate test cases run, by agent, test case, group and outcome.", "{case}");

    private static readonly AgentHistogram Scores = new(ReleaseGateTelemetry.ScoreMetric, "{score}",
        "An agent's average judge score in a release-gate run, by metric.");

    private static readonly AgentCounter Agents = new(ReleaseGateTelemetry.AgentsMetric,
        "Agents through the release gate, by outcome.");

    public void TestCase(string agentName, AgentTestResult result, string outcome) =>
        metrics.Count(TestCases, agentName,
            Tag(ReleaseGateTelemetry.TestCaseTag, result.CaseName),
            Tag(ReleaseGateTelemetry.GroupTag, result.Group ?? string.Empty),
            Tag(ReleaseGateTelemetry.OutcomeTag, outcome));

    public void AverageScores(string agentName, IReadOnlyDictionary<string, double> averageScores)
    {
        foreach (var (metric, score) in averageScores)
        {
            metrics.Record(Scores, agentName, score, Tag(ReleaseGateTelemetry.EvaluationNameTag, metric));
        }
    }

    public void Agent(string agentName, bool passed) =>
        metrics.Count(Agents, agentName, Tag(ReleaseGateTelemetry.OutcomeTag, AgentOutcomes.Of(passed)));

    /// <summary>Opens a run: every metric recorded with <see cref="IAgentMetrics"/> until it's disposed carries its id.</summary>
    public IDisposable Run(string runId) => metrics.BeginScope(Tag(ReleaseGateTelemetry.RunIdTag, runId));

    private static KeyValuePair<string, object?> Tag(string name, object? value) => new(name, value);
}
