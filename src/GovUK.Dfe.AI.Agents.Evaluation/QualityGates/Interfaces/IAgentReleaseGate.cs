using GovUK.Dfe.AI.Agents.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Evaluation.QualityGates.Interfaces;

/// <summary>Tests agents before they're released, set up under <c>AiAgents:Evaluation:ReleaseGate</c>.</summary>
public interface IAgentReleaseGate
{
    /// <summary>
    /// The standard release check, the same in every app: the guardrail must be in place (every
    /// <see cref="Extensibility.Interfaces.IGuardrailCheck"/>, e.g. from <c>AddGuardrails()</c>), then
    /// <see cref="EvaluateAsync"/> runs every agent's guardrail and evaluation test cases. Logs each agent's sections and,
    /// when any agent fails, one error listing every reason, <see cref="AgentGateResult.ProblemLines"/>. Release only the
    /// agents that pass; never release when any fails.
    /// </summary>
    /// <returns>One result per agent, in order. Without the guardrail in place, none is <see cref="AgentGateResult.Evaluated"/>.</returns>
    /// <remarks>Outside a <see cref="BeginRun"/>, it opens a run of its own for its metrics.</remarks>
    Task<IReadOnlyList<AgentGateResult>> CheckReleaseAsync(IReadOnlyCollection<AgentDefinition> definitions,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs each agent's test cases on a temporary copy of the agent, so no Foundry version is created, then applies the
    /// gate: every case must pass and, with a judge, each metric must meet the minimum, its baseline and the group gap.
    /// Leftover test agents are deleted afterwards, even when this throws.
    /// </summary>
    /// <remarks>Outside a <see cref="BeginRun"/>, it opens a run of its own for its metrics.</remarks>
    Task<IReadOnlyList<AgentGateResult>> EvaluateAsync(IReadOnlyCollection<AgentDefinition> definitions,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a release: until it's disposed, every metric recorded with <c>IAgentMetrics</c> carries its id, so open it
    /// before the guardrail check and dispose it after provisioning, and one query finds the whole release.
    /// </summary>
    IReleaseRun BeginRun();
}
