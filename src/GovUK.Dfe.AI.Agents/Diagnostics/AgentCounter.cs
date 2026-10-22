using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;

namespace GovUK.Dfe.AI.Agents.Diagnostics;

/// <summary>
/// Tracks per-agent event counts using the library’s standard tags and meter (dfe.ai_agents.), differing only in metric name. Define counters as static readonly fields in AgentCounter and record them through this interface, which adds app and agent tags.
/// </summary>
public sealed partial class AgentCounter
{
    private const string Unit = "{agent}";

    private static readonly Meter Meter = new(AgentTelemetry.SourceName);
     
    private static readonly ConcurrentDictionary<string, Counter<long>> Instruments = new(StringComparer.Ordinal);
     
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
