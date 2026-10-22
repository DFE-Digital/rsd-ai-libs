namespace GovUK.Dfe.AI.Agents.Diagnostics.Interfaces;

/// <summary>
/// Tracks counts of things that happen to agents, with the library's standard tags, so an app's own metrics look like the library's and only the name differs: on the library's meter (<see cref="AgentTelemetry.SourceName"/>), named under <c>dfe.ai_agents.</c>, counted per agent. Define each as a <c>static readonly</c> field in <see cref="AgentCounter"/>, and record it with this interface, which tags every count with the app and agent.
/// </summary>
public interface IAgentMetrics
{
    /// <summary>Counts one for <paramref name="agentName"/>, with the counter's own tags, e.g. an outcome.</summary>
    /// <exception cref="ArgumentException">A tag tries to set the app or agent, which the library sets itself.</exception>
    void Count(AgentCounter counter, string agentName, params ReadOnlySpan<KeyValuePair<string, object?>> tags);
}
