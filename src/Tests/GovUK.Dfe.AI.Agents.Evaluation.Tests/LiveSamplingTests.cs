using System.Diagnostics.Metrics;
using Azure.Core;
using GovUK.Dfe.AI.Agents.Diagnostics;
using GovUK.Dfe.AI.Agents.Evaluation.Quality;
using GovUK.Dfe.AI.Agents.Extensibility.Interfaces;
using GovUK.Dfe.AI.Agents.Quality.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace GovUK.Dfe.AI.Agents.Evaluation.Tests;

/// <summary>Scoring a sample of live runs in the background, with any evaluator.</summary>
public sealed class LiveSamplingTests
{
    private static readonly CompletedAgentRun Sample = new()
    {
        AgentName = "ofsted-agent", AgentVersion = "3", Model = "gpt-4o", Prompt = "Summarise.", Evidence = "Rated Good.", Output = "Rated Good.",
    };

    private static IAgentRunEvaluator ScoresGroundedness(double score)
    {
        var evaluator = Substitute.For<IAgentRunEvaluator>();
        evaluator.EvaluateAsync(Arg.Any<CompletedAgentRun>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, double> { ["Groundedness"] = score });
        return evaluator;
    }

    private static AgentQualityMonitor Monitor(IAgentRunEvaluator evaluator, double sampleRate = 1, string applicationName = "briefing-tool")
        => new(evaluator, sampleRate, applicationName, NullLogger<AgentQualityMonitor>.Instance);

    /// <summary>Your own evaluator, with <c>AiAgents:Evaluation:SampleRate</c> set, or no Evaluation section when null.</summary>
    private static ServiceProvider Build(string? sampleRate)
    {
        var settings = new Dictionary<string, string?>
        {
            ["AiAgents:Foundry:Endpoint"] = "https://example.services.ai.azure.com/api/projects/briefings",
            ["AiAgents:Foundry:DefaultModel"] = "myconnection/gpt-5.1",
        };
        if (sampleRate is not null)
        {
            settings["AiAgents:Evaluation:SampleRate"] = sampleRate;
        }

        var services = new ServiceCollection();
        services.AddAgents(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), agents => agents
            .UseCredential(Substitute.For<TokenCredential>())
            .AddCustomQualityEvaluation(_ => ScoresGroundedness(5)));
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("0.1", true)]
    [InlineData(null, true)]   // no section: the default 5%, and no judge needed
    public void AnOwnEvaluator_ScoresTests_AndLiveRunsOnlyWhenSampled(string? sampleRate, bool scoresLiveRuns)
    {
        using var provider = Build(sampleRate);

        Assert.NotNull(provider.GetRequiredService<IAgentRunEvaluator>());
        Assert.Equal(scoresLiveRuns, provider.GetServices<IAgentRunObserver>().OfType<AgentQualityMonitor>().Any());
        Assert.Equal(scoresLiveRuns, provider.GetServices<IHostedService>().OfType<AgentQualityMonitor>().Any());
        if (scoresLiveRuns)
        {
            // One monitor serves both roles, so what runs report is what the background service scores.
            Assert.Same(provider.GetServices<IAgentRunObserver>().Single(), provider.GetServices<IHostedService>().OfType<AgentQualityMonitor>().Single());
        }
    }

    [Fact]
    public void AnOwnEvaluator_WithAnInvalidSampleRate_FailsStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build("2"));

        Assert.Contains("AiAgents:Evaluation:SampleRate (from 0 to 1)", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(1.0, 1)]
    public void Sampling_FollowsTheRate(double rate, int queued)
    {
        var monitor = Monitor(ScoresGroundedness(5), rate);

        monitor.OnRunCompleted(Sample);

        Assert.Equal(queued, monitor.Queued);
    }

    [Fact]
    public void WhenTheQueueIsFull_FurtherRunsAreDroppedAndCounted_RatherThanWaitedFor()
    {
        long dropped = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == AgentTelemetry.SourceName && instrument.Name == "dfe.ai_agents.evaluation.samples_dropped")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            if (tags.ToArray().Any(tag => tag.Key == AgentTelemetry.ApplicationTag && Equals(tag.Value, "queue-full-app")))
            {
                Interlocked.Add(ref dropped, value);
            }
        });
        listener.Start();
        var monitor = Monitor(ScoresGroundedness(5), applicationName: "queue-full-app");

        for (var i = 0; i < 150; i++)
        {
            monitor.OnRunCompleted(Sample);
        }

        Assert.Equal(100, monitor.Queued);
        Assert.Equal(50, Interlocked.Read(ref dropped));
    }

    [Fact]
    public async Task EachScore_IsRecordedOnCoresMeter_TaggedWithTheAgentVersionAndMetric()
    {
        var recorded = new List<(double Score, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == AgentTelemetry.SourceName && instrument.Name == "dfe.ai_agents.evaluation.score")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, score, tags, _) => recorded.Add((score, tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
        listener.Start();

        await Monitor(ScoresGroundedness(4.5), applicationName: "scoring-test-app").ScoreAsync(Sample, TestContext.Current.CancellationToken);

        var (score, tags) = Assert.Single(recorded, r => Equals(r.Tags[AgentTelemetry.ApplicationTag], "scoring-test-app"));
        Assert.Equal(4.5, score);
        Assert.Equal("ofsted-agent", tags["gen_ai.agent.name"]);
        Assert.Equal("3", tags["gen_ai.agent.version"]);
        Assert.Equal("Groundedness", tags["gen_ai.evaluation.name"]);
    }

    [Fact]
    public async Task AScoringFailure_IsOnlyLogged()
    {
        var evaluator = Substitute.For<IAgentRunEvaluator>();
        evaluator.EvaluateAsync(Arg.Any<CompletedAgentRun>(), Arg.Any<CancellationToken>()).Throws(new InvalidOperationException("Judge down."));

        Assert.Null(await Record.ExceptionAsync(() => Monitor(evaluator).ScoreAsync(Sample, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task InTheBackground_QueuedRunsAreScored()
    {
        var scored = new TaskCompletionSource<CompletedAgentRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        var evaluator = Substitute.For<IAgentRunEvaluator>();
        evaluator.EvaluateAsync(Arg.Any<CompletedAgentRun>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            scored.TrySetResult(call.Arg<CompletedAgentRun>());
            return new Dictionary<string, double>();
        });
        using var monitor = Monitor(evaluator);
        await monitor.StartAsync(TestContext.Current.CancellationToken);

        monitor.OnRunCompleted(Sample);

        Assert.Same(Sample, await scored.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await monitor.StopAsync(TestContext.Current.CancellationToken);
    }
}
