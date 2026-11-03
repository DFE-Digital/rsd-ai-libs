using GovUK.Dfe.AI.Agents.Builders;
using GovUK.Dfe.AI.Agents.Tools.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Tools;

/// <summary>An agent's tools, resolved from its bindings the same way a run resolves them.</summary>
internal sealed class AgentToolCatalog(AgentSpecBuilder specs) : IAgentToolCatalog
{
    public Task<IReadOnlyList<AgentTool>> GetToolsAsync(AgentDefinition definition, CancellationToken cancellationToken = default) =>
        AgentToolResolver.ResolveAsync(specs.ToolProviders, definition, cancellationToken);
}
