namespace GovUK.Dfe.AI.Agents.Evaluation.QualityGates;

/// <summary>One agent's release-gate result.</summary>
/// <param name="AgentName">The agent tested.</param>
/// <param name="Problems">Why it can't be released, one readable line each. Empty when it can.</param>
public sealed record AgentGateResult(string AgentName, IReadOnlyList<string> Problems)
{
    public bool Passed => Problems.Count == 0;
}
