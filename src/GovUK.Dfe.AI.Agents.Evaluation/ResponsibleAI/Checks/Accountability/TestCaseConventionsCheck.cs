using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Enums;
using GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Scenarios;
using GovUK.Dfe.AI.Agents.Options;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Checks.Accountability;

/// <summary>
/// Every test case says what it tests and how it passes, so a result can be understood and audited: the release gate's
/// folder conventions (<c>ReleaseSections</c>), and with <c>RequireScenarioMetadata</c>, its principle, risk and an explicit
/// expectation (<see cref="ScenarioMetadata.Problems"/>).
/// </summary>
public sealed class TestCaseConventionsCheck : QualityCheck
{
    public override string Id => QualityCheckIds.TestCaseConventions;

    public override ResponsibleAiPrinciple Principle => ResponsibleAiPrinciple.Accountability;

    public override EvaluationMethod Method => EvaluationMethod.Deterministic;

    public override string Description => "Every test case names its principle and risk, and has an explicit pass condition.";

    protected override IEnumerable<QualityCheckResult> Evaluate(QualityCheckContext context)
    {
        if (context.Scenarios.Count == 0)
        {
            yield return NotApplicable("No test case ran.");
            yield break;
        }

        var problems = context.Scenarios.SelectMany(s => ReleaseSections.Problems(s.Case)
            .Concat(ScenarioMetadata.Problems(s.Case, context.Policy.RequireScenarioMetadata))).ToList();

        yield return problems.Count > 0
            ? Failed($"{problems.Count} test case problem(s)",
                "Fix each case listed; check them in a unit test too, so a wrong file fails the build rather than the release.", problems)
            : Passed($"All {context.Scenarios.Count} test cases follow the conventions");
    }
}
