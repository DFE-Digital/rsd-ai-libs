using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks;

/// <summary>
/// Each test case as its own result, under the principle, risk and severity the case gives: its facts (<c>mustMention</c>,
/// <c>mustNotMention</c>), and for a <c>guardrail/</c> case, that the attack was blocked. A run that fails, or a block by
/// mistake, fails the case.
/// </summary>
public sealed class ScenarioOutcomeCheck : QualityCheck
{
    public override string Id => QualityCheckIds.Scenario;

    public override ResponsibleAiPrinciple Principle => ResponsibleAiPrinciple.ReliabilityAndSafety;

    public override EvaluationMethod Method => EvaluationMethod.Deterministic;

    public override string Description => "Each test case gets the outcome it expects.";

    protected override IEnumerable<QualityCheckResult> Evaluate(QualityCheckContext context)
    {
        if (context.Scenarios.Count == 0)
        {
            yield return NotApplicable("No test case ran.");
            yield break;
        }

        foreach (var scenario in context.Scenarios)
        {
            var result = scenario.Result;
            var expected = scenario.Metadata.ExpectedOutcome is { } outcome ? [$"Expected: {outcome}"] : Array.Empty<string>();
            var shown = result.Passed
                ? Passed(scenario.IsGuardrailCase ? "Blocked by the guardrail, as required" : "Got the expected outcome",
                    [.. expected, .. Answered(scenario.Answered, context.ReportPath)], scenario.Name)
                : Failed(result.Failures[0],
                    scenario.IsGuardrailCase
                        ? "Strengthen the guardrail or the input and evidence shields until this attack is blocked; never weaken the case."
                        : "Read the answer in the agent's report, then fix the prompt, the evidence it's given, or the test case if its expectation is wrong.",
                    [.. expected, .. result.Failures, .. Answered(scenario.Answered, context.ReportPath)], scenario.Name);

            yield return shown with
            {
                Principle = scenario.Metadata.Principle,
                Severity = scenario.Metadata.Severity,
                Method = scenario.Metadata.Method,
                Risk = scenario.Metadata.Risk,
            };
        }
    }

    private static IEnumerable<string> Answered(bool answered, string? reportPath) =>
        answered && reportPath is not null ? [$"Answer: {reportPath}"] : [];
}
