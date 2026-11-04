using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Governance;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Decisions;

/// <summary>A check's result and what it means for the release.</summary>
/// <param name="Result">What the check found.</param>
/// <param name="Disposition">Whether it passed, blocks, warns, or has its risk accepted.</param>
public sealed record DecidedFinding(QualityCheckResult Result, FindingDisposition Disposition)
{
    /// <summary>The risk acceptance that covers it, when one does.</summary>
    public RiskAcceptance? Acceptance { get; init; }

    /// <summary>Why the disposition differs from what the severity alone would give, e.g. a critical failure can't be accepted.</summary>
    public string? Note { get; init; }

    /// <summary>One line for a console or log, e.g. <c>[High] Fairness fairness.group-outcome-parity: ...</c>.</summary>
    public string Line()
    {
        var subject = Result.Subject is null ? string.Empty : $" ({Result.Subject})";
        return $"[{Result.Severity}] {Result.Principle} {Result.CheckId}{subject}: {Result.Summary.ReplaceLineEndings(" ")}";
    }
}
