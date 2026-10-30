using GovUK.Dfe.AI.Agents.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Services.Interfaces;

/// <summary>
/// Creates or reuses a Foundry version for each managed agent, for a provisioning job. Apps call it through
/// <see cref="IAgentService.ProvisionAsync"/>.
/// </summary>
internal interface IAgentProvisioner
{
    /// <summary>The version each managed agent now runs. Ephemeral and externally managed agents are skipped.</summary>
    Task<IReadOnlyList<AgentReference>> ProvisionAsync(IReadOnlyCollection<AgentDefinition> definitions, CancellationToken cancellationToken);
}
