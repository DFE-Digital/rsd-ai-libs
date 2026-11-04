namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;

/// <summary>
/// How a check reaches its result, so a report shows how far to trust it. Privacy, security and authorisation are
/// always checked by <see cref="Deterministic"/> or <see cref="Adversarial"/> means, never by a model's judgement alone.
/// </summary>
public enum EvaluationMethod
{
    /// <summary>A rule in code: the same input always gives the same result, e.g. a pattern match or a fact check.</summary>
    Deterministic,

    /// <summary>A judge model's score, e.g. groundedness. Unscored metrics are inconclusive, never passed.</summary>
    LlmJudge,

    /// <summary>Attacks the agent must resist, e.g. prompt injection the guardrail must block.</summary>
    Adversarial,

    /// <summary>Measured across many cases, e.g. pass rates by user group, or changes from the baseline.</summary>
    Statistical,

    /// <summary>Accessibility and language checks of the answer's text, e.g. readability and formatting.</summary>
    Accessibility,

    /// <summary>A person's recorded review or approval, e.g. a DPIA or an accessibility audit.</summary>
    HumanReview,
}
