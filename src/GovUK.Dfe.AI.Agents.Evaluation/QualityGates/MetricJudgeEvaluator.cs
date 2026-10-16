using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace GovUK.Dfe.AI.Agents.Evaluation.QualityGates;

/// <summary>
/// Runs an evaluator with its own judge, rather than one shared by every metric, so each judge can be named after what it
/// scores, e.g. "answer-judge-groundedness".
/// </summary>
/// <param name="inner">The evaluator, e.g. <c>GroundednessEvaluator</c>.</param>
/// <param name="judge">The judge it scores with.</param>
internal sealed class MetricJudgeEvaluator(IEvaluator inner, ChatConfiguration judge) : IEvaluator
{
    public IReadOnlyCollection<string> EvaluationMetricNames => inner.EvaluationMetricNames;

    public ValueTask<EvaluationResult> EvaluateAsync(IEnumerable<ChatMessage> messages, ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null, IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
        => inner.EvaluateAsync(messages, modelResponse, judge, additionalContext, cancellationToken);
}
