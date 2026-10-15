using GovUK.Dfe.AI.Agents.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Evaluation.QualityGates.Interfaces;

/// <summary>Tests agents before they're released, set up under <c>AiAgents:Evaluation:ReleaseGate</c>.</summary>
public interface IAgentReleaseGate
{
    /// <summary>
    /// Runs each agent's test cases on a temporary copy of the agent, so no Foundry version is created, then applies the
    /// gate: every case must pass and, with a judge, each metric must meet the minimum, its baseline and the group gap.
    /// Leftover test agents are deleted afterwards, even when this throws.
    /// </summary>
    Task<IReadOnlyList<AgentGateResult>> EvaluateAsync(IReadOnlyCollection<AgentDefinition> definitions,
        CancellationToken cancellationToken = default);
}
