using System.Globalization;

namespace GovUK.Dfe.AI.Agents.Evaluation.QualityGates;

/// <summary>The release gate's messages, in one place. Log messages are in <see cref="ReleaseGateLog"/>.</summary>
internal static class ReleaseGateMessages
{
    public const string BlockedByGuardrail = "Blocked by the Foundry guardrail, which this test case allows.";

    public static string NoTestCases(string directory) => $"No test cases found in {directory}.";

    public static string BaselineUnreadable(string path, string reason) =>
        $"The baseline {path} isn't a readable evaluation report: {reason}";

    /// <summary>Average scores for the log, e.g. "Groundedness 4.50, Relevance 4.25".</summary>
    public static string Scores(IReadOnlyDictionary<string, double> scores) =>
        scores.Count == 0
            ? "none"
            : string.Join(", ", scores.OrderBy(s => s.Key, StringComparer.Ordinal)
                .Select(s => $"{s.Key} {s.Value.ToString("0.00", CultureInfo.InvariantCulture)}"));

}
