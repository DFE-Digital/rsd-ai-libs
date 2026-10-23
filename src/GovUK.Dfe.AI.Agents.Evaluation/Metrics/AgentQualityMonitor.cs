using System.Diagnostics.Metrics;
using System.Threading.Channels;
using GovUK.Dfe.AI.Agents.Diagnostics;
using GovUK.Dfe.AI.Agents.Extensibility.Interfaces;
using GovUK.Dfe.AI.Agents.Quality.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GovUK.Dfe.AI.Agents.Evaluation.Metrics;

/// <summary>
/// Monitors and evaluates the quality of agent runs by sampling, scoring, and recording evaluation metrics for
/// observability.  
/// </summary>
/// <param name="evaluator">Evaluator used to score completed agent runs.</param>
/// <param name="sampleRate">Fraction of completed runs to sample for evaluation, between 0.0 and 1.0.</param>
/// <param name="applicationName">Application name used for tagging metrics.</param>
/// <param name="logger">Logger for recording warnings and operational events.</param>
internal sealed class AgentQualityMonitor(IAgentRunEvaluator evaluator, double sampleRate, string applicationName,
    ILogger<AgentQualityMonitor> logger) : BackgroundService, IAgentRunObserver
{
    private static readonly Meter Meter = new(AgentTelemetry.SourceName);

    internal static readonly Histogram<double> EvaluationScore = Meter.CreateHistogram<double>(
        "dfe.ai_agents.evaluation.score", "{score}", "Judge scores of sampled agent answers, by evaluation.");

    internal static readonly Counter<long> SamplesDropped = Meter.CreateCounter<long>(
        "dfe.ai_agents.evaluation.samples_dropped", "{run}", "Sampled runs not scored because the scoring queue was full.");

    private readonly Channel<CompletedAgentRun> _queue = Channel.CreateBounded<CompletedAgentRun>(
        new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

    public void OnRunCompleted(CompletedAgentRun run)
    {
        if (Random.Shared.NextDouble() >= sampleRate)
        {
            return;
        }

        // A full queue means the judge can't keep up: drop rather than slow runs, but count it so monitoring shows the gap.
        if (!_queue.Writer.TryWrite(run))
        {
            SamplesDropped.Add(1,
                new KeyValuePair<string, object?>(AgentTelemetry.ApplicationTag, applicationName),
                new KeyValuePair<string, object?>("gen_ai.agent.name", run.AgentName));
        }
    }

    internal int Queued => _queue.Reader.Count;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var sample in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await ScoreAsync(sample, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    internal async Task ScoreAsync(CompletedAgentRun sample, CancellationToken cancellationToken)
    {
        try
        {
            var scores = await evaluator.EvaluateAsync(sample, cancellationToken).ConfigureAwait(false);
            foreach (var (metric, score) in scores)
            {
                EvaluationScore.Record(score,
                    new KeyValuePair<string, object?>(AgentTelemetry.ApplicationTag, applicationName),
                    new KeyValuePair<string, object?>("gen_ai.agent.name", sample.AgentName),
                    new KeyValuePair<string, object?>("gen_ai.agent.version", sample.AgentVersion),
                    new KeyValuePair<string, object?>("gen_ai.evaluation.name", metric));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't score a run of {AgentName}", sample.AgentName);
        }
    }
}
