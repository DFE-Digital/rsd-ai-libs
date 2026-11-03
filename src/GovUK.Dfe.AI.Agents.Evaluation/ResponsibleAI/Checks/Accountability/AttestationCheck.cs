using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks.Accountability;

/// <summary>
/// Each review the policy requires (<c>RequiredAttestations</c>), e.g. a DPIA or accessibility audit, is recorded in the
/// governance file by a named person, with evidence, and hasn't expired. One result per requirement, under its own
/// principle and severity: this is where specialist and human testing joins the decision.
/// </summary>
public sealed class AttestationCheck : QualityCheck
{
    public override string Id => QualityCheckIds.Attestation;

    public override ResponsibleAiPrinciple Principle => ResponsibleAiPrinciple.Accountability;

    public override EvaluationMethod Method => EvaluationMethod.HumanReview;

    public override string Description => "Each required human review is recorded, evidenced and current.";

    protected override IEnumerable<QualityCheckResult> Evaluate(QualityCheckContext context)
    {
        var required = context.Policy.RequiredAttestations;
        if (required.Count == 0)
        {
            yield return NotApplicable("The policy requires no attestations.");
            yield break;
        }

        foreach (var requirement in required)
        {
            var records = context.Governance.Attestations
                .Where(a => string.Equals(a.Id, requirement.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            var valid = records.FirstOrDefault(a => a.Problems(context.Today).Count == 0);
            var result = valid is not null
                ? Passed($"{requirement.Description}: approved", [$"Approved by {valid.ApprovedBy} ({valid.Role}) on {valid.ApprovedOn:yyyy-MM-dd}",
                    $"Evidence: {valid.Evidence}", .. valid.ExpiresOn is { } expires ? [$"Review by {expires:yyyy-MM-dd}"] : Array.Empty<string>()], requirement.Id)
                : Failed(records.Count == 0 ? $"{requirement.Description}: not recorded" : $"{requirement.Description}: not valid",
                    $"Complete the review, then add an attestation with id \"{requirement.Id}\" to the governance file: who approved it, their role, when, and a link to the evidence.",
                    records.SelectMany(a => a.Problems(context.Today)).Select(problem => $"Recorded attestation: {problem}"), requirement.Id);

            yield return result with { Principle = requirement.Principle, Severity = requirement.Severity };
        }
    }
}
