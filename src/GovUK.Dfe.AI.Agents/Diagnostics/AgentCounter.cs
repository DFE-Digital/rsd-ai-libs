using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;

namespace GovUK.Dfe.AI.Agents.Diagnostics;

/// <summary>
/// A count of something that happens to an agent, made to the library's standard, so an app's own metrics look like the
/// library's and only the name differs: on the library's meter (<see cref="AgentTelemetry.SourceName"/>), named under
/// <c>dfe.ai_agents.</c>, counted per agent. Define each as a <c>static readonly</c> field, and record it with
/// <see cref="Interfaces.IAgentMetrics"/>, which tags every count with the app and agent.
/// </summary>
/// <example>
/// <code>
/// public static readonly AgentCounter GuardrailChecks = new("dfe.ai_agents.release_gate.guardrail_checks",
///     "Agents whose Foundry guardrail was checked before their release checks, by outcome.");
/// </code>
/// </example>
public sealed partial class AgentCounter
{
    private const string Unit = "{agent}";

    private static readonly Meter Meter = new(AgentTelemetry.SourceName);

    // One instrument per name: a second definition of the same name records to the same one, rather than a duplicate
    // that exporters report as a conflict.
    private static readonly ConcurrentDictionary<string, Counter<long>> Instruments = new(StringComparer.Ordinal);

    /// <param name="name">
    /// E.g. "dfe.ai_agents.release_gate.guardrail_checks": under <c>dfe.ai_agents.</c>, then lower-case words joined by dots
    /// or underscores, as OpenTelemetry names metrics.
    /// </param>
    /// <param name="description">What one count means.</param>
    /// <exception cref="ArgumentException">The name doesn't follow the standard, or there's no description.</exception>
    public AgentCounter(string name, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (name is null || !NameRule().IsMatch(name))
        {
            throw new ArgumentException(string.Format(Constants.ErrorMessages.InvalidMetricName, name), nameof(name));
        }

        Name = name;
        Instrument = Instruments.GetOrAdd(name, static (metric, about) => Meter.CreateCounter<long>(metric, Unit, about), description);
    }

    public string Name { get; }

    internal Counter<long> Instrument { get; }

    [GeneratedRegex(@"^dfe\.ai_agents(\.[a-z][a-z0-9_]*)+$")]
    private static partial Regex NameRule();
}
