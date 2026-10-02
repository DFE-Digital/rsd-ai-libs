using GovUK.Dfe.AI.Agents.Orchestration.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Services.Interfaces;

/// <summary>Advanced: resolving, creating and running agents directly, and cleaning up ephemeral ones.</summary>
public interface IAgentRuntimeService
{
    IAgentOrchestrator Orchestrator { get; }

    /// <summary>The agent at this environment's pinned version, or its latest.</summary>
    Task<AgentReference> ResolveAsync(string agentName, CancellationToken cancellationToken);

    /// <summary><paramref name="created"/>, or its pinned version if this environment pins it.</summary>
    Task<AgentReference> ResolveAsync(AgentReference created, CancellationToken cancellationToken);

    /// <summary>The pinned version if there is one; otherwise gets or creates the version for <paramref name="buildSpec"/>.</summary>
    Task<AgentReference> GetOrCreateAsync(string agentName, Func<CancellationToken, Task<AgentSpec>> buildSpec,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the agent under a unique name, runs it once, then deletes it: running its tool calls with
    /// <paramref name="resolveToolCalls"/>, sending <paramref name="evidence"/> fenced, and retrying one invalid answer.
    /// </summary>
    Task<AgentResult> RunEphemeralAsync(AgentSpec spec, string prompt, ToolCallResolver? resolveToolCalls = null,
        string? evidence = null, Func<AgentResult, string?>? validateOutput = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes this app's ephemeral agents left by crashes or failed deletes. Call it from your own schedule, or add
    /// <c>agents.AddEphemeralAgentSweep()</c>. Only agents older than any run can be are deleted, never another app's;
    /// safe on every instance at once.
    /// </summary>
    /// <param name="minimumAge">Only agents older than this; null (default) is the safe minimum, and less is refused.</param>
    /// <returns>The names deleted.</returns>
    Task<IReadOnlyList<string>> DeleteOrphanedEphemeralAgentsAsync(TimeSpan? minimumAge = null, CancellationToken cancellationToken = default);
}
