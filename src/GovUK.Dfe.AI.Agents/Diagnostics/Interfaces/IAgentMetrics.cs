namespace GovUK.Dfe.AI.Agents.Diagnostics.Interfaces;

/// <summary>
/// Records metrics to the library's standard, tagged like its own: with the app (<see cref="AgentTelemetry.ApplicationTag"/>,
/// from <c>AiAgents:ApplicationName</c>) and the agent (<see cref="AgentTelemetry.AgentNameTag"/>). The library sets both,
/// and neither can be left out or replaced, so apps sharing an Application Insights workspace never mix their metrics.
/// Registered by <c>AddAgents</c>.
/// </summary>
public interface IAgentMetrics
{
    /// <summary>Counts one for <paramref name="agentName"/>, with the counter's own tags, e.g. an outcome.</summary>
    /// <exception cref="ArgumentException">A tag tries to set the app or agent, which the library sets itself.</exception>
    void Count(AgentCounter counter, string agentName, params ReadOnlySpan<KeyValuePair<string, object?>> tags);

    /// <summary>Records <paramref name="value"/> for <paramref name="agentName"/>, with the histogram's own tags.</summary>
    /// <exception cref="ArgumentException">A tag tries to set the app or agent, which the library sets itself.</exception>
    void Record(AgentHistogram histogram, string agentName, double value, params ReadOnlySpan<KeyValuePair<string, object?>> tags);

    /// <summary>
    /// Adds <paramref name="tags"/> to everything counted or recorded with this interface until the scope is disposed,
    /// across the async work started inside it, e.g. a release-gate run's id on its guardrail check, test cases and
    /// provisioned versions. Scopes nest. Only these metrics get the tags, not the library's per-call ones such as tokens,
    /// so a value that changes every run never multiplies their series.
    /// </summary>
    /// <exception cref="ArgumentException">A tag tries to set the app or agent, which the library sets itself.</exception>
    IDisposable BeginScope(params ReadOnlySpan<KeyValuePair<string, object?>> tags);
}
