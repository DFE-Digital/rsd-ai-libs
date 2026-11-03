using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;

namespace GovUK.Dfe.AI.Agents.Evaluation.ValueObjects;

/// <summary>One principle's results for one agent, by disposition. Not a score: each count is shown, none averaged away.</summary>
/// <param name="Decision">The principle's own decision; null when no check evaluated it.</param>
public sealed record PrincipleSummary(ResponsibleAiPrinciple Principle, ReleaseDecision? Decision, int Passed, int Blocking,
    int Warnings, int RiskAccepted, int NotApplicable);
