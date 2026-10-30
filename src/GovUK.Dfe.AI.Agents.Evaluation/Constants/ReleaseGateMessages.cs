using System.Globalization;
using GovUK.Dfe.AI.Agents.Diagnostics;
using GovUK.Dfe.AI.Agents.Evaluation.QualityGates;

namespace GovUK.Dfe.AI.Agents.Evaluation.Constants;

/// <summary>The release gate's messages, in one place. Log messages are in <see cref="ReleaseGateLog"/>.</summary>
internal static class ReleaseGateMessages
{
    public const string BlockedByGuardrail = "Blocked by the guardrail, as this test case allows.";

    public const string NoGuardrailCheck =
        "No guardrail is checked before release: add agents.AddGuardrails() with its AiAgents:Guardrails section, or set AiAgents:Evaluation:ReleaseGate:RequireGuardrail to false.";

    public static string NoTestCases(string directory) => $"No test cases found in {directory}.";

    public static string GuardrailNotChecked(string guardrail, string reason) => $"The {guardrail} couldn't be checked: {reason}";

    /// <summary>A section's result for the log: "passed", "failed (2 problems)", or "not run".</summary>
    public static string SectionResult(int problems, bool ran = true) => (ran, problems) switch
    {
        (false, _) => "not run",
        (_, 0) => AgentOutcomes.Passed,
        _ => $"{AgentOutcomes.Failed} ({problems} problems)",
    };

    public static string BaselineUnreadable(string path, string reason) =>
        $"The baseline {path} isn't a readable evaluation report: {reason}";

    /// <summary>Average scores for the log, e.g. "Groundedness 4.50, Relevance 4.25".</summary>
    public static string Scores(IReadOnlyDictionary<string, double> scores) =>
        scores.Count == 0
            ? "none"
            : string.Join(", ", scores.OrderBy(s => s.Key, StringComparer.Ordinal)
                .Select(s => $"{s.Key} {s.Value.ToString("0.00", CultureInfo.InvariantCulture)}"));

}
