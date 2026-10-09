using GovUK.Dfe.AI.Agents.Enums;
using GovUK.Dfe.AI.Agents.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Quality.Interfaces;

/// <summary>Runs test cases against an agent, for a release gate in CI.</summary>
public interface IAgentTestRunner
{
    /// <summary>Runs the cases one at a time, checks each answer's facts, and scores it if <c>AddQualityEvaluation</c> is set up.</summary>
    /// <param name="target">Candidate (default) tests without publishing; Deployed tests what's running now.</param>
    /// <param name="repeats">
    /// Runs each case this many times and averages its scores, so one unlucky answer or judge score doesn't decide the
    /// release. A case's facts must be right in every run. Each repeat costs another run and judge call.
    /// </param>
    Task<AgentEvaluationReport> RunAsync(AgentDefinition definition, IReadOnlyCollection<AgentTestCase> cases,
        AgentTestTarget target = AgentTestTarget.Candidate, int repeats = 1, CancellationToken cancellationToken = default);
}
