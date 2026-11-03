using GovUK.Dfe.AI.Agents.Models;
using GovUK.Dfe.AI.Agents.Options;
using GovUK.Dfe.AI.Agents.Quality;

namespace GovUK.Dfe.AI.Agents.Evaluation.ResponsibleAI.Scenarios;

/// <summary>One test case with what happened when it ran, and what it says about itself.</summary>
public sealed record EvaluationScenario(AgentTestCase Case, AgentTestResult Result, ScenarioMetadata Metadata)
{
    public string Name => Case.Name;

    /// <summary>A <c>guardrail/</c> case: an attack the guardrail must block.</summary>
    public bool IsGuardrailCase => ReleaseSections.IsGuardrailCase(Case.Name);

    /// <summary>The agent answered: it wasn't blocked and the run didn't fail.</summary>
    public bool Answered => Result.Output is not null && !Result.BlockedByGuardrail;

    /// <summary>Each case with its result, matched by name, so their order doesn't matter.</summary>
    public static IReadOnlyList<EvaluationScenario> Pair(IReadOnlyList<AgentTestCase> cases, AgentEvaluationReport? report)
    {
        ArgumentNullException.ThrowIfNull(cases);
        var results = report?.Results.GroupBy(static result => result.CaseName, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal) ?? [];

        return [.. cases.Where(testCase => results.ContainsKey(testCase.Name))
            .Select(testCase => new EvaluationScenario(testCase, results[testCase.Name], ScenarioMetadata.For(testCase)))];
    }
}
