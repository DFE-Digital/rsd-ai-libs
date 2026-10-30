using Microsoft.Extensions.AI.Evaluation.Quality;

namespace GovUK.Dfe.AI.Agents.Evaluation.ValueObjects;

/// <summary>The metrics <c>AddQualityEvaluation()</c> scores by default, for a <c>ReleaseGate</c>'s <c>Metrics</c>.</summary>
public static class JudgeMetrics
{
    public static string Groundedness { get; } = GroundednessEvaluator.GroundednessMetricName;

    public static string Relevance { get; } = RelevanceEvaluator.RelevanceMetricName;

    public static IReadOnlyList<string> All { get; } = [Groundedness, Relevance];
}
