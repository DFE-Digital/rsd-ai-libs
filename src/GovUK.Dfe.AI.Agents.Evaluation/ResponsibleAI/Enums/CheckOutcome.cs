namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;

/// <summary>What a check found.</summary>
public enum CheckOutcome
{
    /// <summary>The evidence meets the criterion.</summary>
    Passed,

    /// <summary>The evidence doesn't meet the criterion.</summary>
    Failed,

    /// <summary>
    /// There isn't enough evidence to decide, e.g. the judge couldn't score or too few cases ran. Treated as a failure of
    /// the same severity: missing evidence is never a pass.
    /// </summary>
    Inconclusive,

    /// <summary>The check doesn't apply, e.g. an agent with no tools has none to authorise. Doesn't affect the decision.</summary>
    NotApplicable,
}
