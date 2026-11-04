using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Policy;

/// <summary>One check's policy, under <c>QualityGate:Checks:&lt;check id&gt;</c>, e.g. <c>"inclusiveness.readability"</c>.</summary>
public sealed class CheckPolicy
{
    /// <summary>
    /// False turns the check off. The report lists every check turned off, so record why in the policy's change history;
    /// a principle in <c>Principles</c> left with no check still fails the gate.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Replaces the check's own severity for every result it gives, e.g. to make a warning mandatory.</summary>
    public CheckSeverity? Severity { get; set; }
}
