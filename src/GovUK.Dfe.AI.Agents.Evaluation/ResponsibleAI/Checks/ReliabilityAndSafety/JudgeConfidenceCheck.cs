using System.Globalization;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks.ReliabilityAndSafety;

/// <summary>
/// How far to trust the judge's scores: a metric some answers weren't scored on, or one averaging only just above its
/// minimum (within <c>JudgeBorderlineMargin</c>), is a warning, as the judge's scores vary from run to run.
/// </summary>
public sealed class JudgeConfidenceCheck : QualityCheck
{
    public override string Id => QualityCheckIds.JudgeConfidence;

    public override ResponsibleAiPrinciple Principle => ResponsibleAiPrinciple.ReliabilityAndSafety;

    public override CheckSeverity DefaultSeverity => CheckSeverity.Medium;

    public override EvaluationMethod Method => EvaluationMethod.LlmJudge;

    public override string Description => "Every answer is scored, and no average is too close to its minimum to trust.";

    protected override IEnumerable<QualityCheckResult> Evaluate(QualityCheckContext context)
    {
        // Answers expecting a refusal aren't scored on purpose.
        var scorable = context.AnsweredScenarios.Where(s => !context.ExpectsRefusal(s.Case)).Select(static s => s.Name).ToHashSet();
        if (!context.HasJudge || scorable.Count == 0)
        {
            yield return NotApplicable("No answer was scored by a judge.");
            yield break;
        }

        var averages = context.Report.AverageScores;
        var margin = context.Policy.JudgeBorderlineMargin;
        foreach (var metric in context.JudgeMetrics.Where(averages.ContainsKey))
        {
            var unscored = context.Report.Results.Where(r => scorable.Contains(r.CaseName) && !r.Scores.ContainsKey(metric))
                .Select(static r => r.CaseName).ToList();
            var average = averages[metric];
            var minimum = context.MinimumScoreFor(metric);

            if (unscored.Count > 0)
            {
                yield return Inconclusive($"{metric}: {unscored.Count} of {scorable.Count} answers weren't scored, so the average leaves them out",
                    $"Look for \"No {metric} score\" warnings in the gate's log and fix the judge, then run the gate again.",
                    unscored.Select(static name => $"Not scored: {name}"), metric);
            }
            else if (margin > 0 && average >= minimum && average < minimum + margin)
            {
                yield return Inconclusive(string.Create(CultureInfo.InvariantCulture, $"{metric} averaged {average:0.##}, within {margin:0.##} of its minimum {minimum:0.##}"),
                    "Raise ReleaseGate:Repeats so the average is steadier, or have a person review a sample of answers before release.",
                    subject: metric) with { Measured = average, Threshold = minimum + margin };
            }
            else
            {
                yield return Passed(string.Create(CultureInfo.InvariantCulture, $"{metric}: every answer scored, average {average:0.##}"), subject: metric, measured: average);
            }
        }
    }
}
