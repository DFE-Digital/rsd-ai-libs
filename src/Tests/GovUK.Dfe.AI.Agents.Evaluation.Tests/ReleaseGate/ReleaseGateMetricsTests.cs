using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using GovUK.Dfe.AI.Agents.Diagnostics;
using GovUK.Dfe.AI.Agents.Evaluation.QualityGates;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Evaluation.Tests.ReleaseGate;

/// <summary>
/// The release gate's metrics, to the library's standard: every one tagged with the app, agent and run, so one query finds
/// a whole release. Each test uses its own agents, as other tests record on the same metrics at the same time.
/// </summary>
public sealed class ReleaseGateMetricsTests : IDisposable
{
    private readonly ReleaseGateTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Tags_every_metric_in_a_run_with_its_id_and_each_test_case_with_its_name_group_and_outcome()
    {
        AgentDefinition[] agents = [new("run-tagged-agent", "Ofsted")];
        _host.AddCase("run-tagged-agent", caseName: "evaluation/outcome", mustMention: ["Good"], group: "academy");
        using var testCases = new Measurements(ReleaseGateTelemetry.TestCasesMetric);
        using var outcomes = new Measurements(ReleaseGateTelemetry.AgentsMetric);
        var gate = _host.Build();

        string runId;
        using (var run = gate.BeginRun())
        {
            runId = run.Id;
            await gate.EvaluateAsync(agents, Token);
        }

        var testCase = Assert.Single(testCases.For("run-tagged-agent"));
        Assert.Equal(runId, testCase[ReleaseGateTelemetry.RunIdTag]);
        Assert.Equal(("evaluation/outcome", "academy", AgentOutcomes.Passed),
            (testCase[ReleaseGateTelemetry.TestCaseTag], testCase[ReleaseGateTelemetry.GroupTag], testCase[ReleaseGateTelemetry.OutcomeTag]));
        Assert.False(string.IsNullOrEmpty(testCase[AgentTelemetry.ApplicationTag] as string));
        var outcome = Assert.Single(outcomes.For("run-tagged-agent"));
        Assert.Equal((runId, AgentOutcomes.Passed), (outcome[ReleaseGateTelemetry.RunIdTag], outcome[ReleaseGateTelemetry.OutcomeTag]));
    }

    [Fact]
    public async Task Evaluating_outside_a_run_opens_one_per_evaluation()
    {
        AgentDefinition[] agents = [new("own-run-agent", "Ofsted")];
        _host.AddCase("own-run-agent", mustMention: ["Good"]);
        using var outcomes = new Measurements(ReleaseGateTelemetry.AgentsMetric);
        var gate = _host.Build();

        await gate.EvaluateAsync(agents, Token);
        await gate.EvaluateAsync(agents, Token);

        var runIds = outcomes.For("own-run-agent").Select(tags => tags[ReleaseGateTelemetry.RunIdTag] as string).ToList();
        Assert.Equal(2, runIds.Count);
        Assert.All(runIds, id => Assert.False(string.IsNullOrEmpty(id)));
        Assert.NotEqual(runIds[0], runIds[1]);
    }

    [Fact]
    public void Each_run_has_its_own_id_and_logs_it_for_the_console()
    {
        var gate = _host.Build();

        using var first = gate.BeginRun();
        using var second = gate.BeginRun();

        Assert.NotEqual(first.Id, second.Id);
        Assert.Contains(_host.Logs, log => log.Message.Contains(first.Id, StringComparison.Ordinal));
    }

    /// <summary>The tags of what's recorded on one of the library's metrics.</summary>
    private sealed class Measurements : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<IReadOnlyDictionary<string, object?>> _recorded = new();

        public Measurements(string metricName)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == AgentTelemetry.SourceName && instrument.Name == metricName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
                _recorded.Enqueue(tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value)));
            _listener.Start();
        }

        public IReadOnlyList<IReadOnlyDictionary<string, object?>> For(string agentName) =>
            [.. _recorded.Where(tags => Equals(tags.GetValueOrDefault(AgentTelemetry.AgentNameTag), agentName))];

        public void Dispose() => _listener.Dispose();
    }
}
