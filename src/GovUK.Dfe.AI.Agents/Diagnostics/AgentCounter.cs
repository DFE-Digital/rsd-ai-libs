using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace GovUK.Dfe.AI.Agents.Diagnostics;

/// <summary>
/// A count of something that happens to an agent, made to the library's standard, so every app's metrics look alike and
/// only the name differs: on the library's meter (<see cref="AgentTelemetry.SourceName"/>), named under
/// <c>dfe.ai_agents.</c>, counted per agent. Define each as a <c>static readonly</c> field, and record it with
/// <see cref="Interfaces.IAgentMetrics"/>, which tags every count with the app and agent.
/// </summary>
public sealed class AgentCounter
{
    /// <summary>The unit when none is given: one count per agent.</summary>
    public const string DefaultUnit = "{agent}";

    private static readonly Meter Meter = new(AgentTelemetry.SourceName);

    // One instrument per name: a second definition of the same name records to the same one, rather than a duplicate
    // that exporters report as a conflict.
    private static readonly ConcurrentDictionary<string, Counter<long>> Instruments = new(StringComparer.Ordinal);

    /// <param name="name">E.g. "dfe.ai_agents.guardrail.checks". See <see cref="MetricNames"/>.</param>
    /// <param name="description">What one count means.</param>
    /// <param name="unit">What's counted, in OpenTelemetry's form, e.g. "{case}"; <see cref="DefaultUnit"/> when not given.</param>
    /// <exception cref="ArgumentException">The name doesn't follow the standard, or there's no description or unit.</exception>
    public AgentCounter(string name, string description, string unit = DefaultUnit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        Name = MetricNames.Checked(name, nameof(name));
        Instrument = Instruments.GetOrAdd(Name,
            static (metric, definition) => Meter.CreateCounter<long>(metric, definition.unit, definition.description), (unit, description));
    }

    public string Name { get; }

    internal Counter<long> Instrument { get; }
}
