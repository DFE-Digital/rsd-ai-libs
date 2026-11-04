using System.Globalization;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks.Fairness;

/// <summary>
/// The judge's scores are about the same for every user group, metric by metric: for each judge metric at least two
/// groups were scored on, the best and worst groups' averages are no more than the policy's <c>MaxGroupScoreGap</c>
/// apart. One result per metric, as the release gate reports its group gaps, so each can be fixed or accepted on its own.
/// </summary>
public sealed class GroupScoreParityCheck : QualityCheck
{
    public override string Id => QualityCheckIds.GroupScoreParity;

    public override ResponsibleAiPrinciple Principle => ResponsibleAiPrinciple.Fairness;

    public override EvaluationMethod Method => EvaluationMethod.Statistical;

    public override string Description => "Judge scores don't differ between user groups by more than the policy allows, for any metric.";

    protected override IEnumerable<QualityCheckResult> Evaluate(QualityCheckContext context)
    {
        var byGroup = context.Report.AverageScoresByGroup;
        var compared = context.HasJudge
            ? context.JudgeMetrics
                .Select(metric => (Metric: metric, Groups: byGroup.Where(group => group.Value.ContainsKey(metric))
                    .Select(group => (Group: group.Key, Score: group.Value[metric])).OrderBy(static g => g.Group, StringComparer.Ordinal).ToList()))
                .Where(static m => m.Groups.Count >= 2)
                .ToList()
            : [];
        if (compared.Count == 0)
        {
            yield return NotApplicable("No judge metric has scores from two groups to compare.");
            yield break;
        }

        var maxGap = context.MaxGroupScoreGap;
        foreach (var (metric, groups) in compared)
        {
            var (worst, best) = (groups.MinBy(static g => g.Score), groups.MaxBy(static g => g.Score));
            var gap = best.Score - worst.Score;
            var evidence = groups.Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Group}: {metric} {g.Score:0.##}"));

            yield return gap > maxGap
                ? Failed(string.Create(CultureInfo.InvariantCulture, $"{metric}: {worst.Group} {worst.Score:0.##}, {best.Group} {best.Score:0.##}: a gap over {maxGap:0.##}"),
                    $"Read the {worst.Group} group's answers in the agent's report: their {metric} score is the lowest. Add evidence or instructions for that group.",
                    evidence, metric, gap, maxGap)
                : Passed(string.Create(CultureInfo.InvariantCulture, $"{metric}: within {maxGap:0.##} across {groups.Count} groups"), evidence, metric, gap, maxGap);
        }
    }
}
