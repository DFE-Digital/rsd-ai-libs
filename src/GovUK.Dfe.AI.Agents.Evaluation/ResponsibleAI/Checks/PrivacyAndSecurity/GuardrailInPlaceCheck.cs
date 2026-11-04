using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks.PrivacyAndSecurity;

/// <summary>
/// The guardrail (e.g. from <c>AddGuardrails()</c>) was in place before any case ran. It doesn't check the guardrail
/// itself: the Guardrails package does, and the release gate runs that check. This files its result under Privacy and
/// security. Whether a guardrail is required at all is the team's choice: <c>ReleaseGate:RequireGuardrail</c>.
/// </summary>
public sealed class GuardrailInPlaceCheck : QualityCheck
{
    public override string Id => QualityCheckIds.GuardrailInPlace;

    public override ResponsibleAiPrinciple Principle => ResponsibleAiPrinciple.PrivacyAndSecurity;

    public override CheckSeverity DefaultSeverity => CheckSeverity.Critical;

    public override EvaluationMethod Method => EvaluationMethod.Deterministic;

    public override string Description => "The guardrail is in place and checked before the agent is tested.";

    protected override IEnumerable<QualityCheckResult> Evaluate(QualityCheckContext context)
    {
        if (!context.Evaluated)
        {
            yield return Failed("The guardrail isn't in place, so no test case ran",
                "Fix each guardrail problem listed, e.g. create or restore the guardrail with gate --apply-guardrails, then run the gate again.",
                context.GuardrailProblems);
        }
        else if (context.GuardrailChecks == 0)
        {
            // Only reachable with ReleaseGate:RequireGuardrail off: the release gate fails every agent otherwise.
            yield return NotApplicable("No guardrail: the team turned off ReleaseGate:RequireGuardrail.");
        }
        else
        {
            yield return Passed($"{context.GuardrailChecks} guardrail check(s) passed before testing");
        }
    }
}
