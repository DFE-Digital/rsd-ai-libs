using GovUK.Dfe.AI.Agents.Quality.Interfaces;
using GovUK.Dfe.AI.Agents.ValueObjects;
using Microsoft.Extensions.AI.Evaluation.Quality;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;

namespace GovUK.Dfe.AI.Agents.Evaluation.QualityGates;

/// <summary>
/// Scores answers with Microsoft.Extensions.AI.Evaluation evaluators, e.g. <see cref="GroundednessEvaluator"/> and
/// <see cref="RelevanceEvaluator"/>, judged by the model in <paramref name="chatConfiguration"/>. The run's evidence
/// is the grounding context. A metric left unscored, by a failed judge call or an unreadable reply, is logged with the reason.
/// </summary>
/// <remarks>
/// The judge's question is the agent's instructions as well as its prompt: Microsoft's evaluators read only the user's
/// message, so without them an answer that does exactly what the agent was told, e.g. one section of a briefing, is
/// scored against the prompt alone and looks irrelevant.
/// </remarks>
public sealed class ExtensionsAiEvaluator(IEvaluator evaluator, ChatConfiguration chatConfiguration,
    ILogger<ExtensionsAiEvaluator>? logger = null) : IAgentRunEvaluator
{
    private readonly ILogger _logger = logger ?? NullLogger<ExtensionsAiEvaluator>.Instance;

    public async Task<IReadOnlyDictionary<string, double>> EvaluateAsync(CompletedAgentRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        ChatMessage[] messages = [new(ChatRole.User, QuestionFor(run))];
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, run.Output));
        EvaluationContext[] context = run.Evidence is null ? [] : [new GroundednessEvaluatorContext(run.Evidence)];

        var result = await evaluator.EvaluateAsync(messages, response, chatConfiguration, context, cancellationToken).ConfigureAwait(false);

        var scores = new Dictionary<string, double>();
        // Not only NumericMetric: when the judge call throws, CompositeEvaluator reports a plain EvaluationMetric.
        foreach (var metric in result.Metrics.Values)
        {
            if (metric is NumericMetric { Value: { } score })
            {
                scores[metric.Name] = score;
                continue;
            }

            _logger.LogWarning("No {Metric} score for {AgentName}: {Reason}", metric.Name, run.AgentName, WhyUnscored(metric));
        }

        return scores;
    }

    /// <summary>The evaluator's diagnostics without stack traces, e.g. the judge call's error or an unreadable reply.</summary>
    internal static string WhyUnscored(EvaluationMetric metric)
    {
        var reasons = (metric.Diagnostics ?? [])
            .Select(static diagnostic => diagnostic.Message.Split("\n   at ", 2)[0].Trim())
            .Where(static reason => reason.Length > 0)
            .ToList();

        return reasons.Count > 0 ? string.Join(" | ", reasons) : metric.Reason ?? "the evaluator gave no reason";
    }

    /// <summary>What the agent was asked: its instructions, when known, then the prompt.</summary>
    internal static string QuestionFor(CompletedAgentRun run)
        => string.IsNullOrWhiteSpace(run.Instructions)
            ? run.Prompt
            : $"The assistant's instructions:\n{run.Instructions}\n\nThe request:\n{run.Prompt}";
}
