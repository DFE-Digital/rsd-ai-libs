namespace GovUK.Dfe.AI.Agents.Guardrails.ValueObjects;

/// <summary>A guardrail check. <see cref="Problems"/> is empty when every deployment carries a strong enough guardrail.</summary>
public sealed record GuardrailReport(string Guardrail, IReadOnlyList<string> Problems)
{
    public bool Passed => Problems.Count == 0;
}
