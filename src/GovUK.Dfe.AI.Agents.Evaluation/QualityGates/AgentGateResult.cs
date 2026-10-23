using GovUK.Dfe.AI.Agents.Options;

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
