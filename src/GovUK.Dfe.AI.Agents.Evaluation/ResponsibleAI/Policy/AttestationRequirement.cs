using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Policy;

/// <summary>
/// A review a person must record before release, for what can't be tested automatically, e.g. a DPIA, an accessibility
/// audit or a penetration test. Recorded as an attestation in the governance file.
/// </summary>
public sealed class AttestationRequirement
{
    /// <summary>Its id, matched by the attestation that records it, e.g. "dpia".</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The principle it gives evidence for.</summary>
    public ResponsibleAiPrinciple Principle { get; set; } = ResponsibleAiPrinciple.Accountability;

    /// <summary>What must be reviewed, shown in the report, e.g. "Data protection impact assessment approved by the DPO".</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>How much a missing or expired attestation matters.</summary>
    public CheckSeverity Severity { get; set; } = CheckSeverity.High;
}
