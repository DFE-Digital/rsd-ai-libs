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

    /// <summary>The agent answered in at least one run: not every run was blocked or failed.</summary>
    public bool Answered => Outputs.Count > 0;

    /// <summary>
    /// Every answer the agent gave, one per run that answered (<c>Repeats</c>), even when another run was blocked, so a
    /// check misses none; empty when it never answered.
    /// </summary>
    public IReadOnlyList<string> Outputs => Result.Answers;

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
