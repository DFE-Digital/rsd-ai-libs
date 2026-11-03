using GovUK.Dfe.AI.Agents.Options;
using GovUK.Dfe.AI.Agents.Quality;

namespace GovUK.Dfe.AI.Agents.Evaluation.QualityGates;

/// <summary>One agent's release-gate result, by <see cref="ReleaseSections"/> section.</summary>
/// <param name="AgentName">The agent tested.</param>
/// <param name="GuardrailProblems">
/// Why its guardrail section fails, one readable line each: the guardrail isn't in place, a <c>guardrail/</c> case
/// wasn't blocked, or another case was blocked by mistake.
/// </param>
/// <param name="EvaluationProblems">Why its evaluation section fails: a case's facts are wrong, or a score misses the gate.</param>
public sealed record AgentGateResult(string AgentName, IReadOnlyList<string> GuardrailProblems, IReadOnlyList<string> EvaluationProblems)
{
    public bool Passed => GuardrailProblems.Count == 0 && EvaluationProblems.Count == 0;

    /// <summary>False when the guardrail wasn't in place, so its test cases weren't run.</summary>
    public bool Evaluated { get; init; } = true;

    /// <summary>The agent's test cases, in the order they ran; empty when none ran.</summary>
    public IReadOnlyList<AgentTestCase> TestCases { get; init; } = [];

    /// <summary>
    /// The results the gate was applied to: every case's answer and failures, without scores for cases expecting a
    /// refusal. Null when no case ran.
    /// </summary>
    public AgentEvaluationReport? Report { get; init; }

    /// <summary>The baseline the scores were compared with, without the same scores; null when there's none.</summary>
    public AgentEvaluationReport? Baseline { get; init; }

    /// <summary>Where the agent's report was written, with every answer, score and failure; null when no case ran.</summary>
    public string? ReportPath { get; init; }

    /// <summary>Every problem, the guardrail's first.</summary>
    public IReadOnlyList<string> Problems => [.. GuardrailProblems, .. EvaluationProblems];

    /// <summary>Every problem with its <see cref="ReleaseSections"/> section, the guardrail's first.</summary>
    public IEnumerable<(string Section, string Problem)> ProblemsBySection() =>
        GuardrailProblems.Select(static problem => (ReleaseSections.Guardrail, problem))
            .Concat(EvaluationProblems.Select(static problem => (ReleaseSections.Evaluation, problem)));

    /// <summary>Each problem as one line, <c>- {agent} [{section}]: {problem}</c>, for a console or an exception message.</summary>
    public IEnumerable<string> ProblemLines() =>
        ProblemsBySection().Select(item => $"- {AgentName} [{item.Section}]: {item.Problem.ReplaceLineEndings(" ")}");
}
