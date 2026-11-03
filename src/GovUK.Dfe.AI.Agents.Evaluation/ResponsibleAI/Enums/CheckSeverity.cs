namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;

/// <summary>How much a failed check matters to the release decision, from least to most.</summary>
public enum CheckSeverity
{
    /// <summary>Advisory: a failure is a warning, shown in the report, that never needs a risk acceptance.</summary>
    Low,

    /// <summary>Non-critical: a failure is a warning that, by default, needs a risk acceptance before release.</summary>
    Medium,

    /// <summary>Mandatory: a failure blocks the release unless an approved, unexpired risk acceptance covers it.</summary>
    High,

    /// <summary>
    /// Critical: a failure always blocks the release. A risk acceptance can't waive it unless the policy's
    /// <c>AllowCriticalExceptions</c> is on.
    /// </summary>
    Critical,
}
