using System.Diagnostics;
using GovUK.Dfe.AI.Agents.Diagnostics.Interfaces;

namespace GovUK.Dfe.AI.Agents.Diagnostics;

/// <summary>
/// <see cref="IAgentMetrics"/> for one app. Created by <c>AddAgents</c> with the app's name, the same one every library
/// metric is tagged with, so an app can't give a different name or none.
/// </summary>
internal sealed class AgentMetrics : IAgentMetrics
{
    private readonly string _applicationName;

    public AgentMetrics(string applicationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);
        _applicationName = applicationName;
    }

    public void Count(AgentCounter counter, string agentName, params ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        ArgumentNullException.ThrowIfNull(counter);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        var all = new TagList
        {
            { AgentTelemetry.ApplicationTag, _applicationName },
            { AgentTelemetry.AgentNameTag, AgentTelemetry.AgentNameForTelemetry(agentName) },
        };
        foreach (var tag in tags)
        {
            if (tag.Key is AgentTelemetry.ApplicationTag or AgentTelemetry.AgentNameTag)
            {
                throw new ArgumentException(string.Format(Constants.ErrorMessages.ReservedMetricTag, tag.Key), nameof(tags));
            }

            all.Add(tag);
        }

        counter.Instrument.Add(1, all);
    }
}
