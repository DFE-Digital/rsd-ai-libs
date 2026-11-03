using System.Globalization;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks.Fairness;

/// <summary>
/// The judge's scores are about the same for every user group: the release gate's group gap
/// (<c>AgentEvaluationReport.GroupGaps</c>), held to the policy's <c>MaxGroupScoreGap</c>.
/// </summary>
public sealed class GroupScoreParityCheck : QualityCheck
{
    public override string Id => QualityCheckIds.GroupScoreParity;

    public override ResponsibleAiPrinciple Principle => ResponsibleAiPrinciple.Fairness;

    public override EvaluationMethod Method => EvaluationMethod.Statistical;

    public override string Description => "Judge scores don't differ between user groups by more than the policy allows.";

    protected override IEnumerable<QualityCheckResult> Evaluate(QualityCheckContext context)
    {
        var byGroup = context.Report.AverageScoresByGroup;
        if (!context.HasJudge || byGroup.Count(static group => group.Value.Count > 0) < 2)
        {
            yield return NotApplicable("Fewer than two groups have judge scores to compare.");
            yield break;
        }

        var maxGap = context.MaxGroupScoreGap;
        var gaps = context.Report.GroupGaps(maxGap, [.. context.JudgeMetrics]);
        var evidence = byGroup.OrderBy(static group => group.Key, StringComparer.Ordinal).Select(static group =>
            $"{group.Key}: {string.Join(", ", group.Value.OrderBy(static s => s.Key, StringComparer.Ordinal)
                .Select(static s => string.Create(CultureInfo.InvariantCulture, $"{s.Key} {s.Value:0.##}")))}");

        yield return gaps.Count > 0
            ? Failed(string.Create(CultureInfo.InvariantCulture, $"{string.Join("; ", gaps)}: a gap over {maxGap:0.##}"),
                "Read the lowest-scoring group's answers in the agent's report: the agent may be less grounded or relevant for them. Add evidence or instructions for that group.",
                evidence, threshold: maxGap)
            : Passed(string.Create(CultureInfo.InvariantCulture, $"Scores are within {maxGap:0.##} across groups"), evidence, threshold: maxGap);
    }
}
