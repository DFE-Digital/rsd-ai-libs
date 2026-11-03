namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks;

/// <summary>
/// The library's check ids, as the policy's <c>Checks</c> settings, risk acceptances and reports name them. Each starts
/// with its principle.
/// </summary>
public static class QualityCheckIds
{
    /// <summary>Each test case passes: right facts, attack blocked. One result per case, under the case's own principle.</summary>
    public const string Scenario = "scenario";

    public const string GroupOutcomeParity = "fairness.group-outcome-parity";
    public const string GroupScoreParity = "fairness.group-score-parity";
    public const string GroupSampleSize = "fairness.group-sample-size";

    public const string JudgeScore = "reliability.judge-score";
    public const string JudgeConfidence = "reliability.judge-confidence";
    public const string BaselineRegression = "reliability.baseline-regression";

    public const string GuardrailInPlace = "privacy.guardrail-in-place";
    public const string SensitiveDataLeakage = "privacy.sensitive-data-leakage";
    public const string ToolAuthorisation = "privacy.tool-authorisation";

    public const string Readability = "inclusiveness.readability";
    public const string AccessibleFormatting = "inclusiveness.accessible-formatting";
    public const string UserGroupCoverage = "inclusiveness.user-group-coverage";

    public const string CitationAccuracy = "transparency.citation-accuracy";

    public const string EvaluationDataset = "accountability.evaluation-dataset";
    public const string TestCaseConventions = "accountability.test-case-conventions";
    public const string Traceability = "accountability.traceability";
    public const string Attestation = "accountability.attestation";

    /// <summary>Added by the gate when no check evaluated a required principle.</summary>
    public const string PrincipleCoverage = "accountability.principle-coverage";

    /// <summary>Added by the gate when the release gate failed an agent but no check explains why, so it can't pass.</summary>
    public const string ReleaseGateConsistency = "accountability.release-gate-consistency";
}
