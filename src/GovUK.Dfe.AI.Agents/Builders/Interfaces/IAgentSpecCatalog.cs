using GovUK.Dfe.AI.Agents.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Builders.Interfaces;

/// <summary>
/// The spec this app gives an agent, exactly as a run builds it: its full instructions, and every tool, its own, its
/// bindings' and built-in ones such as web search. For checks that need to know what an agent is and can do, e.g. a
/// release gate.
/// </summary>
public interface IAgentSpecCatalog
{
    /// <returns>The spec, or null for an externally managed agent, whose version is made where it's provisioned.</returns>
    /// <exception cref="InvalidOperationException">An allowed tool isn't offered by any of the agent's bindings.</exception>
    Task<AgentSpec?> GetSpecAsync(AgentDefinition definition, CancellationToken cancellationToken = default);
}
