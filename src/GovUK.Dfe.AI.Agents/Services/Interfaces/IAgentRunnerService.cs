using GovUK.Dfe.AI.Agents.ValueObjects;

namespace GovUK.Dfe.AI.Agents.Services.Interfaces;

/// <summary>Advanced: runs one agent, from a spec or an already-resolved reference.</summary>
public interface IAgentRunnerService
{
    /// <summary>
    /// Gets or creates the agent for <paramref name="spec"/>, then runs it. The run's history isn't stored in Foundry; the
    /// agent version, with the spec's instructions, tools and output schema, is.
    /// </summary>
    /// <param name="additionalContext">Untrusted evidence, sent fenced as data.</param>
    /// <param name="validateOutput">Returns why an answer is invalid, or null. An invalid answer is retried once.</param>
    Task<AgentResult> RunFromSpecAsync(AgentSpec spec, string prompt, string? additionalContext = null, ToolCallResolver? resolveToolCalls = null, Func<AgentResult, string?>? validateOutput = null,
        CancellationToken cancellationToken = default);

    /// <summary>Runs an already-resolved agent. The run's history isn't stored in Foundry.</summary>
    /// <param name="additionalContext">Untrusted evidence, sent fenced as data.</param>
    /// <param name="validateOutput">Returns why an answer is invalid, or null. An invalid answer is retried once.</param>
    Task<AgentResult> RunAsync(AgentReference agent, string prompt, string? additionalContext = null,
        ToolCallResolver? resolveToolCalls = null,
        Func<AgentResult, string?>? validateOutput = null,
        CancellationToken cancellationToken = default);
}
