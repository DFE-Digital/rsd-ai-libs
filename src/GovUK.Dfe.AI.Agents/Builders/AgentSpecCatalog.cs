using GovUK.Dfe.AI.Agents.Builders.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Builders;

/// <summary>An agent's spec, built by the same <see cref="AgentSpecBuilder"/> a run uses.</summary>
internal sealed class AgentSpecCatalog(AgentSpecBuilder specs) : IAgentSpecCatalog
{
    public Task<AgentSpec?> GetSpecAsync(AgentDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return specs.BuildAsync(definition, cancellationToken);
    }
}
