using GovUK.Dfe.AI.Agents.Factories.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;
using GovUK.Dfe.AI.Agents.Services.Interfaces;
using GovUK.Dfe.AI.Agents.Providers.Interfaces;

namespace GovUK.Dfe.AI.Agents.Providers;

/// <summary>An agent another pipeline provisions: resolved at its pinned (or latest) version, never created.</summary>
public sealed class ExternallyManagedAgentProvider(string agentName, IAgentFactory agentFactory, IAgentRuntimeService agentRuntime)
    : IManagedAgentProvider
{
    public string AgentName => agentName;

    public bool CreatesAgent => false;

    public Task<AgentSpec?> BuildSpecAsync(CancellationToken cancellationToken = default) => Task.FromResult<AgentSpec?>(null);

    /// <summary>Resolves the agent by name, applying the configured version pin (or floating to latest if unpinned).</summary>
    public Task<AgentReference> GetAgentAsync(CancellationToken cancellationToken = default)
        => agentRuntime.ResolveAsync(agentName, cancellationToken);

    /// <summary>Resolves the agent's latest version, ignoring any configured pin.</summary>
    public Task<AgentReference> GetLatestAgentAsync(CancellationToken cancellationToken = default)
        => agentFactory.ResolveLatestAsync(agentName, cancellationToken);
}
