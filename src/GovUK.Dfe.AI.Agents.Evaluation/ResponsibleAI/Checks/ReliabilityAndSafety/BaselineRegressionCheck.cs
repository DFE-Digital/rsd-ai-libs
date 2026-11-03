using System.Globalization;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;
using GovUK.Dfe.AI.Agents.Models;
using GovUK.Dfe.AI.Agents.Options;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks.ReliabilityAndSafety;

/// <summary>
/// Nothing got worse than the approved baseline (the release gate's <c>Baselines/&lt;agent&gt;.json</c>): no judge metric
/// fell by more than <c>ReleaseGate:Tolerance</c>, and the share of evaluation cases passing fell by no more than
/// <c>MaxPassRateDrop</c>. Cases that passed before and fail now are listed.
/// </summary>
public sealed class BaselineRegressionCheck : QualityCheck
{
    public override string Id => QualityCheckIds.BaselineRegression;

    public override ResponsibleAiPrinciple Principle => ResponsibleAiPrinciple.ReliabilityAndSafety;

    public override EvaluationMethod Method => EvaluationMethod.Statistical;

    public override string Description => "Scores and pass rates haven't fallen below the approved baseline.";

    protected override IEnumerable<QualityCheckResult> Evaluate(QualityCheckContext context)
    {
        if (!context.EvaluationScenarios.Any())
        {
            yield return NotApplicable("No evaluation case ran.");
            yield break;
        }

        if (context.Baseline is not { } baseline)
        {
            yield return context.Policy.RequireBaseline
                ? Inconclusive("There's no approved baseline to compare with",
                    "Copy a passing evaluation-reports/<agent>.json into the release gate's BaselinesDirectory, reviewed like code.")
                : NotApplicable("There's no approved baseline yet; copy a passing report into BaselinesDirectory to set one.");
            yield break;
        }

        var tolerance = context.ReleaseGate.Tolerance;
        var before = baseline.AverageScores;
        var now = context.Report.AverageScores;
        foreach (var metric in context.JudgeMetrics.Where(m => before.ContainsKey(m) && now.ContainsKey(m)))
        {
            var drop = before[metric] - now[metric];
            var evidence = new[] { string.Create(CultureInfo.InvariantCulture, $"Baseline {before[metric]:0.##}, now {now[metric]:0.##}, tolerance {tolerance:0.##}") };
            yield return drop > tolerance
                ? Failed(string.Create(CultureInfo.InvariantCulture, $"{metric} fell from {before[metric]:0.##} to {now[metric]:0.##}, more than the tolerance {tolerance:0.##}"),
                    $"Compare this run's answers with the baseline's: the latest prompt, model or evidence change lowered {metric}.",
                    evidence, metric, now[metric], before[metric] - tolerance)
                : Passed(string.Create(CultureInfo.InvariantCulture, $"{metric} is within {tolerance:0.##} of the baseline"), evidence, metric, now[metric], before[metric] - tolerance);
        }

        yield return PassRate(context, baseline.Results.Where(static r => !ReleaseSections.IsGuardrailCase(r.CaseName)).ToList());
    }

    private QualityCheckResult PassRate(QualityCheckContext context, List<AgentTestResult> baseline)
    {
        const string Subject = "pass-rate";
        var current = context.EvaluationScenarios.Select(static s => s.Result).ToList();
        if (baseline.Count == 0)
        {
            return NotApplicable("The baseline has no evaluation cases.", Subject);
        }

        var (before, now) = (Rate(baseline), Rate(current));
        var maxDrop = context.Policy.MaxPassRateDrop;
        var passedBefore = baseline.Where(static r => r.Passed).Select(static r => r.CaseName).ToHashSet(StringComparer.Ordinal);
        List<string> evidence =
        [
            string.Create(CultureInfo.InvariantCulture, $"Baseline {before * 100:0}% of {baseline.Count} cases passed; now {now * 100:0}% of {current.Count}"),
            .. current.Where(r => !r.Passed && passedBefore.Contains(r.CaseName)).Select(static r => $"Now failing: {r.CaseName}"),
        ];

        return before - now > maxDrop
            ? Failed(string.Create(CultureInfo.InvariantCulture, $"The pass rate fell from {before * 100:0}% to {now * 100:0}%, more than {maxDrop * 100:0} points"),
                "Fix the cases now failing that passed in the baseline, listed in the evidence, before release.",
                evidence, Subject, now, before - maxDrop)
            : Passed(string.Create(CultureInfo.InvariantCulture, $"The pass rate is {now * 100:0}% (baseline {before * 100:0}%)"), evidence, Subject, now, before - maxDrop);
    }

    private static double Rate(List<AgentTestResult> results) =>
        results.Count == 0 ? 0 : (double)results.Count(static r => r.Passed) / results.Count;
}
