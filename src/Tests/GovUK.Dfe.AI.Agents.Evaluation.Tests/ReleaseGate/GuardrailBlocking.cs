namespace GovUK.Dfe.AI.Agents.Evaluation.Tests.ReleaseGate;

/// <summary>What a test case written by <see cref="ReleaseGateTestHost"/> expects of the guardrail.</summary>
internal enum GuardrailBlocking
{
    /// <summary>Neither flag: the case is checked as usual.</summary>
    None,

    /// <summary><c>"guardrailMayBlock": true</c>: a block passes the case.</summary>
    May,

    /// <summary><c>"guardrailMustBlock": true</c>: only a block passes the case.</summary>
    Must,
}
