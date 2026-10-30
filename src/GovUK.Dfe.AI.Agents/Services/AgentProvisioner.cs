using GovUK.Dfe.AI.Agents.Builders;
using GovUK.Dfe.AI.Agents.Diagnostics;
using GovUK.Dfe.AI.Agents.Diagnostics.Interfaces;
using GovUK.Dfe.AI.Agents.Factories.Interfaces;
using GovUK.Dfe.AI.Agents.Services.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GovUK.Dfe.AI.Agents.Services;

/// <summary>Kept apart from running agents: only a provisioning job needs it.</summary>
internal sealed class AgentProvisioner(AgentSpecBuilder specs, IAgentFactory factory, IAgentMetrics? metrics = null,
    ILogger<AgentProvisioner>? logger = null) : IAgentProvisioner
{
    private readonly ILogger<AgentProvisioner> _logger = logger ?? NullLogger<AgentProvisioner>.Instance;

    public async Task<IReadOnlyList<AgentReference>> ProvisionAsync(IReadOnlyCollection<AgentDefinition> definitions,
        CancellationToken cancellationToken)
    {
        var provisioned = new List<AgentReference>(definitions.Count);

        // One at a time: a provisioning job isn't latency-sensitive, and it keeps Foundry calls gentle.
        foreach (var definition in definitions)
        {
            if (!definition.IsManagedAgent)
            {
                _logger.SkippingEphemeralAgent(definition.Name);
                continue;
            }

            var spec = await specs.BuildAsync(definition, cancellationToken).ConfigureAwait(false);
            if (spec is null)
            {
                _logger.SkippingExternallyManagedAgent(definition.Name);
                continue;
            }

            var agent = await factory.GetOrCreateAsync(spec, cancellationToken).ConfigureAwait(false);
            _logger.ProvisionedAgent(agent.Name, agent.Version);
            metrics?.Count(AgentTelemetry.AgentsProvisioned, agent.Name,
                new KeyValuePair<string, object?>(AgentTelemetry.AgentVersionTag, agent.Version));
            provisioned.Add(agent);
        }

        return provisioned;
    }
}
