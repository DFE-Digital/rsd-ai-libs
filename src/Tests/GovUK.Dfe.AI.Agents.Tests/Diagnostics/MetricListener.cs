using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using GovUK.Dfe.AI.Agents.Diagnostics;

namespace GovUK.Dfe.AI.Agents.Tests.Diagnostics;

/// <summary>
/// What's recorded on one of the library's metrics, each measurement as its value and tags. Tests running side by side
/// can record on the same metric, so assertions look for their own agents' measurements rather than counting them all.
/// </summary>
internal sealed class MetricListener : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<(double Value, IReadOnlyDictionary<string, object?> Tags)> _recorded = new();

    public MetricListener(string metricName)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == AgentTelemetry.SourceName && instrument.Name == metricName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, value, tags, _) => Add(value, tags));
        _listener.SetMeasurementEventCallback<double>((_, value, tags, _) => Add(value, tags));
        _listener.Start();
    }

    public IReadOnlyList<(double Value, IReadOnlyDictionary<string, object?> Tags)> Recorded => [.. _recorded];

    /// <summary>The measurements for one agent.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> For(string agentName) =>
        [.. Recorded.Select(r => r.Tags).Where(tags => Equals(tags.GetValueOrDefault(AgentTelemetry.AgentNameTag), agentName))];

    public void Dispose() => _listener.Dispose();

    private void Add(double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
        _recorded.Enqueue((value, tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value)));
}
