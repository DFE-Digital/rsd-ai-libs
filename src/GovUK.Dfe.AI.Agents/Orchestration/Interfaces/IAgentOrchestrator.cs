using GovUK.Dfe.AI.Agents.Context;
using GovUK.Dfe.AI.Agents.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Orchestration.Interfaces;

/// <summary>
/// Advanced: runs agents you've already resolved to <see cref="AgentReference"/>s. Most apps use
/// <c>IAgentService</c>, which resolves agents from definitions and wires up their tools for you.
/// </summary>
public interface IAgentOrchestrator
{
    /// <summary>
    /// Runs agents one after another. The first gets <paramref name="initialInput"/> as its prompt; each
    /// later agent gets the earlier outputs as fenced evidence, so they can't act as instructions.
    /// </summary>
    /// <param name="initialInput">The prompt for the first agent.</param>
    /// <param name="context">Records each successful step; its history is what later agents receive.</param>
    /// <param name="shouldSuppress">Which failures become fallback results.</param>
    /// <param name="resolveToolCallsFor">Runs each agent's function tool calls in this app; null when none have any.</param>
    Task<OrchestrationResult> RunSequentialAsync(IReadOnlyList<AgentReference> agents, string initialInput,
        AgentContext context, Func<Exception, bool>? shouldSuppress = null,
        Func<AgentReference, ToolCallResolver?>? resolveToolCallsFor = null, CancellationToken cancellationToken = default);

    /// <summary>Runs agents side by side with the same prompt.</summary>
    /// <param name="shouldSuppress">Which failures become fallback results.</param>
    /// <param name="resolveToolCallsFor">Runs each agent's function tool calls in this app; null when none have any.</param>
    Task<OrchestrationResult> RunParallelAsync(IReadOnlyList<AgentReference> agents, string input,
        AgentContext context, int? maxConcurrency = null, Func<Exception, bool>? shouldSuppress = null,
        Func<AgentReference, ToolCallResolver?>? resolveToolCallsFor = null, CancellationToken cancellationToken = default);

    /// <summary>Runs steps side by side, each resolving its own agent, prompt, evidence and tool calls.</summary>
    /// <param name="shouldSuppress">Which failures become fallback results.</param>
    Task<OrchestrationResult> RunParallelAsync(IReadOnlyList<AgentOrchestrationStep> steps,
        AgentContext context, int? maxConcurrency = null, Func<Exception, bool>? shouldSuppress = null,
        CancellationToken cancellationToken = default);
}
