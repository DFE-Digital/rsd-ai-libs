namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;

/// <summary>
/// What a check's result means for the release, once the policy and risk acceptances are applied. In order of
/// importance, least first, as the report lists them most important first.
/// </summary>
public enum FindingDisposition
{
    /// <summary>The check didn't apply.</summary>
    NotApplicable,

    /// <summary>The check passed.</summary>
    Passed,

    /// <summary>A failure covered by an approved, unexpired risk acceptance: visible in the report, but doesn't block.</summary>
    RiskAccepted,

    /// <summary>
    /// A non-critical failure: release needs the policy's permission and, for a medium one by default, a risk acceptance.
    /// A low one is advisory.
    /// </summary>
    Warning,

    /// <summary>A mandatory failure: blocks the release.</summary>
    Blocking,
}
