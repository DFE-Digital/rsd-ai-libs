using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Governance;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Policy;

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
            yield return ResultFor(requirement, records, context.Today) with
            {
                Principle = requirement.Principle,
                Severity = requirement.Severity,
            };
        }
    }

    /// <summary>Passed with the first valid record; otherwise failed, with why each recorded one isn't valid.</summary>
    private QualityCheckResult ResultFor(AttestationRequirement requirement, List<Attestation> records, DateOnly today)
    {
        if (records.Find(a => a.Problems(today).Count == 0) is { } valid)
        {
            List<string> evidence = [$"Approved by {valid.ApprovedBy} ({valid.Role}) on {valid.ApprovedOn:yyyy-MM-dd}", $"Evidence: {valid.Evidence}"];
            if (valid.ExpiresOn is { } expires)
            {
                evidence.Add($"Review by {expires:yyyy-MM-dd}");
            }

            return Passed($"{requirement.Description}: approved", evidence, requirement.Id);
        }

        var state = records.Count == 0 ? "not recorded" : "not valid";
        return Failed($"{requirement.Description}: {state}",
            $"Complete the review, then add an attestation with id \"{requirement.Id}\" to the governance file: who approved it, their role, when, and a link to the evidence.",
            records.SelectMany(a => a.Problems(today)).Select(problem => $"Recorded attestation: {problem}"), requirement.Id);
    }
}
