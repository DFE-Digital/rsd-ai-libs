using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks.Inclusiveness;

/// <summary>
/// Every user group the policy names (<c>RequiredGroups</c>), e.g. special schools or Welsh-medium settings, has at least
/// <c>MinimumCasesPerGroup</c> evaluation cases, so the agent is tested for the people it must serve, not only the most
/// common ones.
/// </summary>
public sealed class UserGroupCoverageCheck : QualityCheck
{
    public override string Id => QualityCheckIds.UserGroupCoverage;

    public override ResponsibleAiPrinciple Principle => ResponsibleAiPrinciple.Inclusiveness;

    public override EvaluationMethod Method => EvaluationMethod.Statistical;

    public override string Description => "Every user group the policy requires is tested with enough cases.";

    protected override IEnumerable<QualityCheckResult> Evaluate(QualityCheckContext context)
    {
        var required = context.Policy.RequiredGroups;
        if (required.Count == 0)
        {
            yield return NotApplicable("The policy names no required user groups.");
            yield break;
        }

        var minimum = context.Policy.MinimumCasesPerGroup;
        var counts = required.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(static group => group,
            group => context.EvaluationScenarios.Count(s => string.Equals(s.Case.Group, group, StringComparison.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);
        var evidence = counts.Select(static c => $"{c.Key}: {c.Value} cases").ToList();
        var missing = counts.Where(c => c.Value < minimum).Select(static c => c.Key).ToList();

        yield return missing.Count > 0
            ? Failed($"Too few cases for {string.Join(", ", missing)} (at least {minimum} each)",
                $"Add evaluation cases with \"group\" set to {string.Join(", ", missing.Select(static g => $"\"{g}\""))}, written from those users' needs.",
                evidence)
            : Passed($"Every required group has at least {minimum} cases", evidence);
    }
}
