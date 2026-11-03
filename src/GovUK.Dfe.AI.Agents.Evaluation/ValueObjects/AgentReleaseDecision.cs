using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Decisions;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;

namespace GovUK.Dfe.AI.Agents.Evaluation.ValueObjects;

/// <summary>One agent's release decision, with the reasons, evidence and recommendations behind it.</summary>
public sealed record AgentReleaseDecision
{
    public required string AgentName { get; init; }

    public required ReleaseDecision Decision { get; init; }

    /// <summary>
    /// Whether the agent may be released: on a pass; on a warning, only when the policy allows warnings and, by default,
    /// every medium warning's risk is accepted; never on a fail.
    /// </summary>
    public required bool ReleasePermitted { get; init; }

    /// <summary>Why, most important first: each blocking failure, then each warning and accepted risk.</summary>
    public required IReadOnlyList<string> Reasons { get; init; }

    /// <summary>What to do, one line per distinct recommendation, from the failures first.</summary>
    public IReadOnlyList<string> Recommendations { get; init; } = [];

    /// <summary>Every check's result, with its disposition: blocking first, then warnings, accepted risks, passes.</summary>
    public IReadOnlyList<DecidedFinding> Findings { get; init; } = [];

    /// <summary>Each principle's results, in the principles' order.</summary>
    public IReadOnlyList<PrincipleSummary> Principles { get; init; } = [];

    /// <summary>Risk acceptances that would have applied but can't be relied on, and why, e.g. expired.</summary>
    public IReadOnlyList<string> IgnoredRiskAcceptances { get; init; } = [];

    /// <summary>The findings with a disposition, in report order.</summary>
    public IEnumerable<DecidedFinding> With(FindingDisposition disposition) =>
        Findings.Where(finding => finding.Disposition == disposition);
}
