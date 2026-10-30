using GovUK.Dfe.AI.Agents.Models;
using GovUK.Dfe.AI.Agents.Quality;

namespace GovUK.Dfe.AI.Agents.Evaluation.Publishers.Interfaces;

/// <summary>Sends release-gate answers to a Foundry cloud evaluation, so their scores show in the Foundry portal.</summary>
internal interface IFoundryEvaluationPublisher
{
    /// <summary>
    /// Starts a Foundry evaluation run of the agent's answered test cases. It doesn't wait for the scores, and a failure
    /// is logged, never thrown: the release decision is the gate's own.
    /// </summary>
    /// <param name="agentName">The agent the answers came from.</param>
    /// <param name="cases">Each test case with its result, in the same order.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task PublishAsync(string agentName, IReadOnlyList<(AgentTestCase Case, AgentTestResult Result)> cases,
        CancellationToken cancellationToken = default);
}
