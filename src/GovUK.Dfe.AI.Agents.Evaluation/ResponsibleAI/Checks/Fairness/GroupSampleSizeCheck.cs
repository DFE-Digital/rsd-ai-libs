using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks.Fairness;

/// <summary>
/// The test cases cover user groups, each with enough cases to compare. Without groups, fairness isn't tested at all, so
/// that's a mandatory gap; a group with too few cases is a warning, as its comparison could be chance.
/// </summary>
public sealed class GroupSampleSizeCheck : QualityCheck
{
    public override string Id => QualityCheckIds.GroupSampleSize;

    public override ResponsibleAiPrinciple Principle => ResponsibleAiPrinciple.Fairness;

    public override CheckSeverity DefaultSeverity => CheckSeverity.Medium;

    public override EvaluationMethod Method => EvaluationMethod.Statistical;

    public override string Description => "Evaluation cases cover user groups, each with enough cases to compare.";

    protected override IEnumerable<QualityCheckResult> Evaluate(QualityCheckContext context)
    {
        if (!context.EvaluationScenarios.Any())
        {
            yield return NotApplicable("No evaluation case ran.");
            yield break;
        }

        var groups = context.EvaluationScenarios.Where(static s => s.Case.Group is not null)
            .GroupBy(static s => s.Case.Group!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static g => g.Key, static g => g.Count(), StringComparer.OrdinalIgnoreCase);

        if (groups.Count == 0)
        {
            yield return Inconclusive("No evaluation case names a group, so fairness across users isn't tested",
                "Give each evaluation case the \"group\" of users it represents, e.g. \"special\" or \"rural\", with several cases per group.") with
            {
                Severity = CheckSeverity.High,
            };
            yield break;
        }

        var minimum = context.Policy.MinimumCasesPerGroup;
        var evidence = groups.OrderBy(static g => g.Key, StringComparer.Ordinal).Select(static g => $"{g.Key}: {g.Value} cases").ToList();
        var thin = groups.Where(g => g.Value < minimum).Select(static g => g.Key).Order(StringComparer.Ordinal).ToList();

        yield return thin.Count > 0
            ? Inconclusive($"Too few cases to compare {string.Join(", ", thin)} (fewer than {minimum} each)",
                $"Add evaluation cases for {string.Join(", ", thin)} until each has at least {minimum}.", evidence)
            : Passed($"{groups.Count} groups, each with at least {minimum} cases", evidence);
    }
}
