using System.Globalization;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks.ReliabilityAndSafety;

/// <summary>
/// Each judge metric, e.g. groundedness and relevance from <c>AddQualityEvaluation()</c>, averages at least its minimum.
/// The scores are the release gate's own; this check only holds them to the policy. A metric left unscored is
/// inconclusive, never a pass, and a high score on one metric never makes up for another.
/// </summary>
public sealed class JudgeScoreCheck : QualityCheck
{
    private const int LowestShown = 3;

    public override string Id => QualityCheckIds.JudgeScore;

    public override ResponsibleAiPrinciple Principle => ResponsibleAiPrinciple.ReliabilityAndSafety;

    public override EvaluationMethod Method => EvaluationMethod.LlmJudge;

    public override string Description => "Each judge metric averages at least its minimum score.";

    protected override IEnumerable<QualityCheckResult> Evaluate(QualityCheckContext context)
    {
        if (!context.EvaluationScenarios.Any())
        {
            yield return NotApplicable("No evaluation case ran.");
            yield break;
        }

        if (!context.HasJudge)
        {
            // The team chose no judge (gate.WithoutJudge()): answers are checked by their test cases only.
            yield return NotApplicable("No judge: the team chose to check answers by their test cases only.");
            yield break;
        }

        var averages = context.Report.AverageScores;
        foreach (var metric in context.JudgeMetrics)
        {
            var minimum = context.MinimumScoreFor(metric);
            if (!averages.TryGetValue(metric, out var average))
            {
                yield return Inconclusive($"{metric} wasn't scored",
                    $"Look for \"No {metric} score\" warnings in the gate's log: usually a wrong judge model or a judge call that failed.",
                    subject: metric);
                continue;
            }

            var scored = context.Report.Results.Where(r => r.Scores.ContainsKey(metric)).ToList();
            List<string> evidence =
            [
                string.Create(CultureInfo.InvariantCulture, $"Averaged {average:0.##} over {scored.Count} cases (minimum {minimum:0.##}), judged by {context.JudgeModel}"),
                .. scored.OrderBy(r => r.Scores[metric]).Take(LowestShown)
                    .Select(r => string.Create(CultureInfo.InvariantCulture, $"Lowest: {r.CaseName} {r.Scores[metric]:0.##}")),
            ];

            yield return average < minimum
                ? Failed(string.Create(CultureInfo.InvariantCulture, $"{metric} averaged {average:0.##}, below the minimum {minimum:0.##}"),
                    $"Read the lowest-scoring answers in the agent's report. Low {metric} usually means the prompt doesn't keep the agent to its evidence and task.",
                    evidence, metric, average, minimum)
                : Passed(string.Create(CultureInfo.InvariantCulture, $"{metric} averaged {average:0.##} (minimum {minimum:0.##})"), evidence, metric, average, minimum);
        }
    }
}
