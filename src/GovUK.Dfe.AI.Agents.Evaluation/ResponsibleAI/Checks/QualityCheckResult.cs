using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks;

/// <summary>
/// One check's result for one agent, in the same shape in every app: what was checked, under which principle, how, how
/// much it matters, what was found, the evidence, and what to do about it.
/// </summary>
public sealed record QualityCheckResult
{
    /// <summary>The check, e.g. "privacy.sensitive-data-leakage"; what policy settings and risk acceptances refer to.</summary>
    public required string CheckId { get; init; }

    public required ResponsibleAiPrinciple Principle { get; init; }

    public required CheckSeverity Severity { get; init; }

    public required EvaluationMethod Method { get; init; }

    public required CheckOutcome Outcome { get; init; }

    /// <summary>One line saying what was found, e.g. "Groundedness averaged 3.1, below the minimum 3.5".</summary>
    public required string Summary { get; init; }

    /// <summary>What the result is about when a check gives several, e.g. a test case, metric, group or attestation.</summary>
    public string? Subject { get; init; }

    /// <summary>The risk tested, e.g. "Reveals a pupil's personal data".</summary>
    public string? Risk { get; init; }

    /// <summary>The facts behind the result, one per line, e.g. each group's pass rate.</summary>
    public IReadOnlyList<string> Evidence { get; init; } = [];

    /// <summary>What to do when it didn't pass.</summary>
    public string? Recommendation { get; init; }

    /// <summary>The value measured, when there is one, e.g. an average score.</summary>
    public double? Measured { get; init; }

    /// <summary>The threshold it was compared with.</summary>
    public double? Threshold { get; init; }

    /// <summary>Whether it counts against the release: failed, or inconclusive, as missing evidence is never a pass.</summary>
    public bool NotMet => Outcome is CheckOutcome.Failed or CheckOutcome.Inconclusive;
}
