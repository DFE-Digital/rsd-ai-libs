using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace GovUK.Dfe.AI.Agents.Diagnostics;

/// <summary>
/// A measured value for an agent, such as a score, made to the same standard as <see cref="AgentCounter"/>: on the
/// library's meter, named under <c>dfe.ai_agents.</c>, recorded per agent with <see cref="Interfaces.IAgentMetrics"/>.
/// </summary>
public sealed class AgentHistogram
{
    private static readonly Meter Meter = new(AgentTelemetry.SourceName);

    private static readonly ConcurrentDictionary<string, Histogram<double>> Instruments = new(StringComparer.Ordinal);

    /// <param name="name">E.g. "dfe.ai_agents.release_gate.score". See <see cref="MetricNames"/>.</param>
    /// <param name="unit">The value's unit, in OpenTelemetry's form, e.g. "{score}" or "s".</param>
    /// <param name="description">What one value means.</param>
    /// <exception cref="ArgumentException">The name doesn't follow the standard, or there's no unit or description.</exception>
    public AgentHistogram(string name, string unit, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        Name = MetricNames.Checked(name, nameof(name));
        Instrument = Instruments.GetOrAdd(Name,
            static (metric, definition) => Meter.CreateHistogram<double>(metric, definition.unit, definition.description), (unit, description));
    }

    public string Name { get; }

    internal Histogram<double> Instrument { get; }
}
