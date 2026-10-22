namespace GovUK.Dfe.AI.Agents.Diagnostics;

/// <summary>
/// The outcomes the library's metrics record, e.g. on a release-gate test case or a guardrail check, so apps and queries
/// use the same words.
/// </summary>
public static class AgentOutcomes
{
    public const string Passed = "passed";
    public const string Failed = "failed";

    /// <summary>A guardrail blocked it, as a test case allowed or required.</summary>
    public const string BlockedByGuardrail = "blocked_by_guardrail";

    /// <summary><see cref="Passed"/> or <see cref="Failed"/>.</summary>
    public static string Of(bool passed) => passed ? Passed : Failed;
}
