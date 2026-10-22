using System.Collections.Immutable;
using System.Diagnostics;
using GovUK.Dfe.AI.Agents.Diagnostics.Interfaces;

namespace GovUK.Dfe.AI.Agents.Diagnostics;

/// <summary>
/// <see cref="IAgentMetrics"/> for one app. Created by <c>AddAgents</c> with the app's name, the same one every library
/// metric is tagged with, so an app can't give a different name or none.
/// </summary>
internal sealed class AgentMetrics : IAgentMetrics
{
    // Ambient, so a scope's tags reach work started inside it, including parallel test cases.
    private static readonly AsyncLocal<ImmutableArray<KeyValuePair<string, object?>>> ScopeTags = new();

    private readonly string _applicationName;

    public AgentMetrics(string applicationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);
        _applicationName = applicationName;
    }

    public void Count(AgentCounter counter, string agentName, params ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        ArgumentNullException.ThrowIfNull(counter);
        counter.Instrument.Add(1, Tags(agentName, tags));
    }

    public void Record(AgentHistogram histogram, string agentName, double value, params ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        ArgumentNullException.ThrowIfNull(histogram);
        histogram.Instrument.Record(value, Tags(agentName, tags));
    }

    public IDisposable BeginScope(params ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var outer = ScopeTags.Value.IsDefault ? [] : ScopeTags.Value;
        var builder = outer.ToBuilder();
        foreach (var tag in tags)
        {
            builder.Add(Allowed(tag));
        }

        ScopeTags.Value = builder.ToImmutable();
        return new Scope(outer);
    }

    /// <summary>The app, the agent (a test copy under its agent's name), the open scopes' tags, then the metric's own.</summary>
    private TagList Tags(string agentName, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        var all = new TagList
        {
            { AgentTelemetry.ApplicationTag, _applicationName },
            { AgentTelemetry.AgentNameTag, AgentTelemetry.AgentNameForTelemetry(agentName) },
        };
        if (!ScopeTags.Value.IsDefault)
        {
            foreach (var tag in ScopeTags.Value)
            {
                all.Add(tag);
            }
        }

        foreach (var tag in tags)
        {
            all.Add(Allowed(tag));
        }

        return all;
    }

    private static KeyValuePair<string, object?> Allowed(KeyValuePair<string, object?> tag) =>
        tag.Key is AgentTelemetry.ApplicationTag or AgentTelemetry.AgentNameTag
            ? throw new ArgumentException(string.Format(Constants.ErrorMessages.ReservedMetricTag, tag.Key), nameof(tag))
            : tag;

    /// <summary>Restores the outer scope's tags when disposed.</summary>
    private sealed class Scope(ImmutableArray<KeyValuePair<string, object?>> outer) : IDisposable
    {
        public void Dispose() => ScopeTags.Value = outer;
    }
}
