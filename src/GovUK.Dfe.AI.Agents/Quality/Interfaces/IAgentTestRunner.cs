using GovUK.Dfe.AI.Agents.Enums;
using GovUK.Dfe.AI.Agents.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Quality.Interfaces;

/// <summary>Runs test cases against an agent, for a release gate in CI.</summary>
public interface IAgentTestRunner
{
    /// <summary>Runs the cases one at a time, checks each answer's facts, and scores it if <c>AddQualityEvaluation</c> is set up.</summary>
    /// <param name="target">Candidate (default) tests without publishing; Deployed tests what's running now.</param>
    Task<AgentEvaluationReport> RunAsync(AgentDefinition definition, IReadOnlyCollection<AgentTestCase> cases,
        AgentTestTarget target = AgentTestTarget.Candidate, CancellationToken cancellationToken = default);
}
