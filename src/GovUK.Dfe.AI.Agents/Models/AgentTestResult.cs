using GovUK.Dfe.AI.Agents.Options;
using GovUK.Dfe.AI.Agents.Quality;

namespace GovUK.Dfe.AI.Agents.Models;

/// <summary>
/// Represents the result of executing an agent test case, including output, failures, and scores.
/// </summary>
/// <param name="CaseName">The name of the test case.</param>
/// <param name="Output">The output produced by the agent, if any.</param>
/// <param name="Failures">A list of failure messages for the test case.</param>
/// <param name="Scores">A dictionary of score names and their corresponding values.</param>
public sealed record AgentTestResult(string CaseName, string? Output, IReadOnlyList<string> Failures, IReadOnlyDictionary<string, double> Scores)
{
    public bool Passed => Failures.Count == 0;

    /// <summary>The case's <see cref="AgentTestCase.Group"/>.</summary>
    public string? Group { get; init; }

    /// <summary>
    /// A Foundry guardrail blocked the run, as the case allowed or required (<see cref="AgentTestCase.GuardrailMayBlock"/>,
    /// <see cref="AgentTestCase.GuardrailMustBlock"/>).
    /// </summary>
    public bool BlockedByGuardrail { get; init; }

    /// <summary>
    /// A guardrail blocked a case that neither allows nor requires it: a false positive, so the case fails in the
    /// <see cref="ReleaseSections.Guardrail"/> section.
    /// </summary>
    public bool BlockedByGuardrailInError { get; init; }
}
