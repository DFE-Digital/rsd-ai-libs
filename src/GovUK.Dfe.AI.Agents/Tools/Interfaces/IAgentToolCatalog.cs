using GovUK.Dfe.AI.Agents.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Tools.Interfaces;

/// <summary>
/// The tools an agent is given in this app: the built-in tools of its bindings, e.g. web search, and the function tools in
/// its <see cref="AgentDefinition.AllowedTools"/>. For checks that need to know what an agent can do, e.g. a release gate.
/// </summary>
public interface IAgentToolCatalog
{
    /// <exception cref="InvalidOperationException">An allowed tool isn't offered by any of the agent's bindings.</exception>
    Task<IReadOnlyList<AgentTool>> GetToolsAsync(AgentDefinition definition, CancellationToken cancellationToken = default);
}
